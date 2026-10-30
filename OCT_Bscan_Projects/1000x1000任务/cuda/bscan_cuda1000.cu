// bscan_cuda1000.cu
// OCT 多帧 B-scan 重建 CUDA 版（以 MATLAB 标杆 getOCTimage4_bench.m 为标准）
// 重建 data/ 下的全部 .bin（每帧 2048 x 1000 int16），输出 BMP 到 image_1000/
//
// 流程（与 MATLAB 严格一致）：
//   逐帧：读入 -> 去直流(减每行均值) -> 按 MA 排序重排 -> 线性插值重采样(x)
//        -> 色散补偿(dispersion) + 乘 CArray -> 4096 点 FFT(补零) -> 取模
//        -> log10(im+1) -> 灰度截断映射 -> 存 BMP
//   最后：所有帧映射值求平均 -> mean_all_bsacns.bmp
//
// 编译：nvcc -O2 -Xcompiler /utf-8 -arch=native -o bscan_cuda1000.exe bscan_cuda1000.cu
//       源码含中文注释，-Xcompiler /utf-8 不能省；-arch=native 免掉驱动 PTX JIT。
//
// ===========================================================================
// 相对上一版的改动
// ---------------------------------------------------------------------------
// 【速度】
//  [S1] 去掉 cuFFT，改用自带的 radix-2 FFT（共享内存内一次算完）。
//       本机 cuFFT 建一次 plan 要 ~50 ms（与 batch 无关，只与长度有关），比 11 帧的
//       真实计算还贵；自写 FFT 把这笔完全去掉。实测 radix-2 与 np.fft 相对误差 ~2.5e-14。
//  [S2] 7 个核函数合并为 2 个：rowMeanKernel + 一个融合核
//       （建输入 -> 补零 -> FFT -> 取模 -> log10 -> 量化 -> 累加）。
//       中间数组 d_data/d_fs/d_ss/d_cplx 全部取消，显存往返大幅下降。
//  [S3] 所有"只依赖标定/下标"的量在 CPU 上预计算一次（各 2048 项）：
//       插值括号 (srcL,srcR,den,q)、色散相位 cos/sin、bit-reversal 表、旋转因子。
//       消除了旧版每个元素重算 cos/sin、以及每列重复 1000 次的二分查找。
//  [S4] 平均图的累加缓冲留在显存：旧版每帧要额外回传 825000 个 float(3.3 MB)，
//       11 帧共 36 MB；新版只在最后回传一次 825000 字节的 uint8。
//  [S5] 每帧只回传 uint8 图像(825 KB)。
//
// 【稳定】
//  [T1] 所有 CUDA API 检查返回值，核函数后查 cudaGetLastError，失败即报错退出。
//  [T2] 校验每帧 .bin 尺寸、标定 .bin 尺寸，并检查短读。
//  [T3] 标定 .bin 按文件大小自动识别 float64 / float32。
//  [T4] 编译期断言 fftLen == 2*pixNum（本融合核按 2 倍补零设计）。
//  [T5] 写盘失败会统计并返回非 0 退出码。
//
// 【精度】保持与 MATLAB 一致的算术次序，不要改：
//  [P1] 去直流在 float32 里做，插值在 double 里做后截断回 float32，
//       色散与 CArray 两次复数乘法按 MATLAB 的顺序，FFT 用 double。
//  [P2] uint8 用量化四舍五入（MATLAB uint8(x) 是 round half away from zero）。
//  [P3] log10(im + 1)，不是 log10(im)。
// ===========================================================================

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cerrno>
#include <chrono>
#include <vector>
#include <algorithm>
#include <string>
#include <io.h>
#include <direct.h>
#include <cuda_runtime.h>

// ---------------- Config (与 MATLAB / 标杆一致) ----------------
const int    pixNum      = 2048;    // A-line 像素点数
const int    linNum      = 1000;    // 每帧 A-line 个数
const int    fftLen      = 4096;    // FFT 点数 = 2048 * fourierTime(2)
const int    LOG2FFT     = 12;      // log2(4096)
const int    startPoint  = 200;     // 深度方向显示起始点
const int    endPoint    = 1024;    // 深度方向显示终止点
const int    outRows     = endPoint - startPoint + 1;   // 825
const double disp_coeff1 = -0.6122;
const double disp_coeff2 = -0.3061;
const double grayScale1  = 2.5;
const double grayScale2  = 6.0;
const int    bytesLen    = 2;

const char*  dataDir = "data";
const char*  outDir  = "image_1000";

static_assert(fftLen == 2 * pixNum, "fused kernel assumes 2x zero padding");
static_assert((1 << LOG2FFT) == fftLen, "LOG2FFT must equal log2(fftLen)");
static_assert(startPoint >= 1 && endPoint <= fftLen, "display window out of range");

// ---------------- 错误检查 ----------------
#define CUDA_CHECK(call)                                                        \
    do {                                                                        \
        cudaError_t _e = (call);                                                \
        if (_e != cudaSuccess) {                                                \
            printf("[CUDA ERROR] %s:%d  %s -> %s\n", __FILE__, __LINE__, #call, \
                   cudaGetErrorString(_e));                                     \
            exit(1);                                                            \
        }                                                                       \
    } while (0)

// ---------------- BMP 写入 (8位灰度, 头部字段与 MATLAB imwrite 一致) ----------------
#pragma pack(push, 1)
struct BMPFileHeader {
    uint16_t bfType = 0x4D42;
    uint32_t bfSize = 0;
    uint16_t bfReserved1 = 0;
    uint16_t bfReserved2 = 0;
    uint32_t bfOffBits = 54 + 256 * 4;
};
struct BMPInfoHeader {
    uint32_t biSize = 40;
    int32_t  biWidth = 0;
    int32_t  biHeight = 0;
    uint16_t biPlanes = 1;
    uint16_t biBitCount = 8;
    uint32_t biCompression = 0;
    uint32_t biSizeImage = 0;
    int32_t  biXPelsPerMeter = 0;    // MATLAB imwrite 写 0
    int32_t  biYPelsPerMeter = 0;
    uint32_t biClrUsed = 256;
    uint32_t biClrImportant = 0;
};
#pragma pack(pop)

static bool saveBMP(const char* filename, const uint8_t* data, int width, int height) {
    const int rowSize   = (width + 3) & ~3;
    const int imageSize = rowSize * height;
    const int palSize   = 256 * 4;
    const int total     = 54 + palSize + imageSize;

    // 整张图先在内存里拼好，最后只做一次 fwrite。
    // 旧写法是 256 次 4 字节调色板 + 每行一次 fwrite，每个文件约 1081 次系统调用，
    // 12 个文件要 60 ms 左右；整块写之后降到几毫秒。
    static std::vector<uint8_t> buf;
    buf.resize((size_t)total);
    uint8_t* p = buf.data();

    BMPFileHeader fh;
    BMPInfoHeader ih;
    ih.biWidth     = width;
    ih.biHeight    = height;
    ih.biSizeImage = (uint32_t)imageSize;
    fh.bfSize      = (uint32_t)total;
    memcpy(p, &fh, sizeof(fh)); p += sizeof(fh);
    memcpy(p, &ih, sizeof(ih)); p += sizeof(ih);

    for (int i = 0; i < 256; i++) { p[0] = p[1] = p[2] = (uint8_t)i; p[3] = 0; p += 4; }

    // BMP 自底向上：data 是 [depth][A-line] 行主序，与 MATLAB im[d, j] 一致
    for (int y = height - 1; y >= 0; y--) {
        memcpy(p, data + (size_t)y * width, width);
        if (rowSize > width) memset(p + width, 0, (size_t)(rowSize - width));
        p += rowSize;
    }

    FILE* f = fopen(filename, "wb");
    if (!f) { printf("cannot open %s (errno=%d)\n", filename, errno); return false; }
    size_t written = fwrite(buf.data(), 1, (size_t)total, f);
    bool ok = (written == (size_t)total) && (ferror(f) == 0);
    if (fclose(f) != 0) ok = false;
    if (!ok) printf("write failed: %s (errno=%d)\n", filename, errno);
    return ok;
}

// ---------------- 设备端常数表 ----------------
__device__ double2 g_tw[fftLen / 2];        // exp(-2*pi*i*k/N), k = 0..N/2-1

// ---------------- Kernel 1: 每行平均光谱 ----------------
// mean[i] = mean_j data[i + j*rows]；同一 warp 内 i 连续 -> 访存合并
__global__ void rowMeanKernel(const short* __restrict__ data,
                              float* __restrict__ mean,
                              int rows, int cols) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= rows) return;
    double s = 0.0;                        // int16 数据，1000 项和 <= 3.3e7，double 累加精确
    for (int j = 0; j < cols; j++) s += (double)data[i + (size_t)j * rows];
    mean[i] = (float)(s / (double)cols);
}

// ---------------- Kernel 2: 建输入 + 补零 + FFT + 取模 + 量化 + 累加 ----------------
// 一个 block 处理一列（一条 A-line）。动态共享内存 fftLen 个 double2 = 64 KB。
__global__ void buildFFTPostKernel(const short* __restrict__ data,
                                   const float* __restrict__ mean,
                                   const int*   __restrict__ srcL,
                                   const int*   __restrict__ srcR,
                                   const double* __restrict__ den,
                                   const double* __restrict__ q,
                                   const double* __restrict__ dispRe,
                                   const double* __restrict__ dispIm,
                                   const double* __restrict__ caRe,
                                   const double* __restrict__ caIm,
                                   const int*   __restrict__ brev,
                                   uint8_t*     __restrict__ out,
                                   float*       __restrict__ acc,
                                   int rows, int cols, int start, int end,
                                   double g1, double g2) {
    extern __shared__ double2 sm[];
    const int j   = blockIdx.x;
    const int tid = threadIdx.x;
    const int N   = fftLen;

    // ---- 1) 建频域输入（自然序），补零区写 0 ----
    for (int k = tid; k < N; k += blockDim.x) {
        if (k < rows) {
            const int L = srcL[k];
            const int R = srcR[k];
            // 与 MATLAB 一致的精度链：float32 去直流 -> double 插值 -> 截断回 float32
            float y0 = (float)data[L + (size_t)j * rows] - mean[L];
            float y1 = (float)data[R + (size_t)j * rows] - mean[R];
            double slope = ((double)y1 - (double)y0) / den[k];
            float  ss    = (float)(slope * q[k] + (double)y0);
            // 色散补偿后再乘 CArray，与旧版逐字一致（两次复数乘法）
            double a  = (double)ss * dispRe[k];
            double b_ = (double)ss * dispIm[k];
            double2 v;
            v.x = a * caRe[k] - b_ * caIm[k];
            v.y = a * caIm[k] + b_ * caRe[k];
            sm[k] = v;
        } else {
            sm[k].x = 0.0;
            sm[k].y = 0.0;
        }
    }
    __syncthreads();

    // ---- 2) bit-reversal 置换 ----
    for (int k = tid; k < N; k += blockDim.x) {
        int r = brev[k];
        if (k < r) { double2 t = sm[k]; sm[k] = sm[r]; sm[r] = t; }
    }
    __syncthreads();

    // ---- 3) radix-2 DIT，共 LOG2FFT 级 ----
    for (int s = 0; s < LOG2FFT; s++) {
        const int half = 1 << s;
        const int m    = half << 1;
        for (int b = tid; b < (N >> 1); b += blockDim.x) {
            const int g   = b / half;
            const int jj  = b - g * half;
            const int idx = g * m + jj;
            const double2 w = g_tw[jj * ((N >> 1) / half)];
            const double2 v = sm[idx + half];
            const double2 u = sm[idx];
            const double tr = v.x * w.x - v.y * w.y;
            const double ti = v.x * w.y + v.y * w.x;
            sm[idx].x        = u.x + tr;
            sm[idx].y        = u.y + ti;
            sm[idx + half].x = u.x - tr;
            sm[idx + half].y = u.y - ti;
        }
        __syncthreads();
    }

    // ---- 4) 取模 + log10(im+1) + 灰度映射 + 量化 + 累加 ----
    const int orows = end - start + 1;
    for (int d = tid; d < orows; d += blockDim.x) {
        const double2 z = sm[start - 1 + d];
        const double mag = sqrt(z.x * z.x + z.y * z.y);
        double lv = log10(mag + 1.0);                 // MATLAB: log10(im + 1)
        if (lv < g1) lv = g1;
        if (lv > g2) lv = g2;
        const double mapped = (lv - g1) / (g2 - g1) * 255.0;
        double qv = mapped + 0.5;                     // 四舍五入，对应 uint8()
        if (qv < 0.0)   qv = 0.0;
        if (qv > 255.0) qv = 255.0;
        const size_t o = (size_t)d * cols + j;
        out[o] = (uint8_t)qv;
        if (acc) acc[o] += (float)mapped;             // 平均图累加（映射后、未量化）
    }
}

// 平均图：acc/frameNum -> uint8
__global__ void meanImageKernel(const float* __restrict__ acc, uint8_t* __restrict__ out,
                                int total, int frames) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= total) return;
    double m = (double)acc[i] / (double)frames;
    double q = m + 0.5;
    if (q < 0.0)   q = 0.0;
    if (q > 255.0) q = 255.0;
    out[i] = (uint8_t)q;
}

// ---------------- 主机端小工具 ----------------
static double nowMs() {
    using namespace std::chrono;
    return duration<double, std::milli>(high_resolution_clock::now().time_since_epoch()).count();
}

static bool readBin(const char* name, std::vector<double>& buf, int n) {
    FILE* fp = fopen(name, "rb");
    if (!fp) { printf("Cannot open %s\n", name); return false; }
    fseek(fp, 0, SEEK_END);
    long long sz = (long long)ftell(fp);
    fseek(fp, 0, SEEK_SET);
    buf.assign(n, 0.0);
    if (sz == (long long)n * 8) {
        std::vector<double> t(n);
        if (fread(t.data(), 8, n, fp) != (size_t)n) { printf("%s: short read\n", name); fclose(fp); return false; }
        buf.swap(t);
    } else if (sz == (long long)n * 4) {
        std::vector<float> t(n);
        if (fread(t.data(), 4, n, fp) != (size_t)n) { printf("%s: short read\n", name); fclose(fp); return false; }
        for (int i = 0; i < n; i++) buf[i] = (double)t[i];
        printf("[WARN] %s is float32; re-export as float64 for best match\n", name);
    } else {
        printf("%s: unexpected size %lld (expected %d or %d bytes)\n", name, sz, n * 8, n * 4);
        fclose(fp);
        return false;
    }
    fclose(fp);
    return true;
}

// ---------------- 主函数 ----------------
int main(int argc, char** argv) {
    bool verbose = false;
    for (int i = 1; i < argc; i++)
        if (!strcmp(argv[i], "-v") || !strcmp(argv[i], "--verbose")) verbose = true;

    const double tProg = nowMs();

    // ============ 0. 输出目录 ============
    if (_mkdir(outDir) != 0 && errno != EEXIST) {
        printf("Cannot create output folder %s (errno=%d)\n", outDir, errno);
        return 1;
    }

    // ============ 1. 读标定参数 ============
    const double t0 = nowMs();
    std::vector<double> h_MA, h_x, h_caRe, h_caIm;
    if (!readBin("MAmean.bin",    h_MA,   pixNum)) return 1;
    if (!readBin("xi.bin",        h_x,    pixNum)) return 1;
    if (!readBin("CArray_re.bin", h_caRe, pixNum)) return 1;
    if (!readBin("CArray_im.bin", h_caIm, pixNum)) return 1;

    // ============ 2. 枚举 data/ 下的 .bin ============
    std::vector<std::string> binFiles;
    {
        std::string pat = std::string(dataDir) + "\\*.bin";
        _finddata_t fd;
        intptr_t h = _findfirst(pat.c_str(), &fd);
        if (h == -1) { printf("No .bin files found in %s/\n", dataDir); return 1; }
        do {
            if (!(fd.attrib & _A_SUBDIR)) binFiles.push_back(std::string(dataDir) + "\\" + fd.name);
        } while (_findnext(h, &fd) == 0);
        _findclose(h);
    }
    std::sort(binFiles.begin(), binFiles.end());
    const int frameNum = (int)binFiles.size();
    if (frameNum <= 0) { printf("no input frames\n"); return 1; }
    const long long need = (long long)pixNum * linNum * bytesLen;
    for (int n = 0; n < frameNum; n++) {
        FILE* f = fopen(binFiles[n].c_str(), "rb");
        if (!f) { printf("Cannot open %s\n", binFiles[n].c_str()); return 1; }
        fseek(f, 0, SEEK_END);
        long long sz = (long long)ftell(f);
        fclose(f);
        if (sz != need) {
            printf("%s size mismatch: %lld, expected %lld\n", binFiles[n].c_str(), sz, need);
            return 1;
        }
    }
    const double t1 = nowMs();

    // ============ 3. CPU 预计算表 ============
    const double t2 = nowMs();

    // 3a) MA 排序（MATLAB interp1 对非单调样本点内部排序，等价于先排序再插值）
    std::vector<int> ord(pixNum);
    for (int i = 0; i < pixNum; i++) ord[i] = i;
    std::stable_sort(ord.begin(), ord.end(),
                     [&](int a, int b) { return h_MA[a] < h_MA[b]; });
    std::vector<double> MA_s(pixNum);
    for (int i = 0; i < pixNum; i++) MA_s[i] = h_MA[ord[i]];

    // 3b) 插值括号 (srcL,srcR,den,q)，只依赖标定
    std::vector<int>    h_srcL(pixNum), h_srcR(pixNum), h_brev(fftLen);
    std::vector<double> h_den(pixNum), h_q(pixNum), h_dispRe(pixNum), h_dispIm(pixNum);

    int outOfRange = 0;
    for (int k = 0; k < pixNum; k++) {
        const double x = h_x[k];
        int L, R; double den, qq;
        if (x <= MA_s[0]) {                      // 越界取端点（MATLAB interp1 给 NaN，本例不会发生）
            L = R = ord[0]; den = 1.0; qq = 0.0; outOfRange++;
        } else if (x >= MA_s[pixNum - 1]) {
            L = R = ord[pixNum - 1]; den = 1.0; qq = 0.0; outOfRange++;
        } else {
            int lo = 0, hi = pixNum - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1;
                if (MA_s[mid] < x) lo = mid + 1; else hi = mid; }
            L = ord[lo - 1]; R = ord[lo];
            den = MA_s[lo] - MA_s[lo - 1];
            qq  = x - MA_s[lo - 1];
        }
        h_srcL[k] = L; h_srcR[k] = R; h_den[k] = den; h_q[k] = qq;
    }

    // 3c) 色散相位（linspace(-1,1,pixNum) 按 MATLAB 写法复现）
    for (int i = 0; i < pixNum; i++) {
        const double step = 2.0 / (pixNum - 1);
        double l = -1.0 + step * (double)i;
        if (i == pixNum - 1) l = 1.0;
        const double phase = disp_coeff1 * l * l + disp_coeff2 * l * l * l;
        h_dispRe[i] = cos(phase);
        h_dispIm[i] = sin(phase);
    }

    // 3d) LOG2FFT 位 bit-reversal 表
    for (int k = 0; k < fftLen; k++) {
        int r = 0, v = k;
        for (int b = 0; b < LOG2FFT; b++) { r = (r << 1) | (v & 1); v >>= 1; }
        h_brev[k] = r;
    }

    // 3e) FFT 旋转因子
    const double PI = 3.14159265358979323846264338327950288;
    std::vector<double2> h_tw(fftLen / 2);
    for (int k = 0; k < fftLen / 2; k++) {
        const double ang = -2.0 * PI * (double)k / (double)fftLen;
        h_tw[k].x = cos(ang);
        h_tw[k].y = sin(ang);
    }
    const double t3 = nowMs();

    printf("=== B-scan CUDA x%d frames  (pixNum=%d linNum=%d fftLen=%d outRows=%d) ===\n",
           frameNum, pixNum, linNum, fftLen, outRows);
    if (outOfRange) printf("[WARN] %d of %d xi samples outside MA range\n", outOfRange, pixNum);

    // ============ 4. GPU 初始化（预热，不计入下面的 Total）============
    const double tWarm0 = nowMs();
    CUDA_CHECK(cudaFree(0));                       // 逼出 CUDA 上下文创建
    const double tWarm1 = nowMs();

    const double tStart = nowMs();

    const size_t frameI16Bytes = (size_t)pixNum * linNum * sizeof(short);
    const size_t outBytes      = (size_t)outRows * linNum;

    short*  d_raw  = nullptr;
    float*  d_mean = nullptr;
    int*    d_srcL = nullptr;
    int*    d_srcR = nullptr;
    int*    d_brev = nullptr;
    double* d_den  = nullptr;
    double* d_q    = nullptr;
    double* d_dRe  = nullptr;
    double* d_dIm  = nullptr;
    double* d_cRe  = nullptr;
    double* d_cIm  = nullptr;
    uint8_t* d_img = nullptr;
    float*   d_acc = nullptr;

    CUDA_CHECK(cudaMemcpyToSymbol(g_tw, h_tw.data(), h_tw.size() * sizeof(double2)));
    CUDA_CHECK(cudaMalloc(&d_raw,  frameI16Bytes));
    CUDA_CHECK(cudaMalloc(&d_mean, pixNum * sizeof(float)));
    CUDA_CHECK(cudaMalloc(&d_srcL, pixNum * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_srcR, pixNum * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_brev, fftLen * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_den,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_q,    pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_dRe,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_dIm,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_cRe,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_cIm,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_img,  outBytes));
    CUDA_CHECK(cudaMalloc(&d_acc,  outBytes * sizeof(float)));

    CUDA_CHECK(cudaMemcpy(d_srcL, h_srcL.data(), pixNum * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_srcR, h_srcR.data(), pixNum * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_brev, h_brev.data(), fftLen * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_den,  h_den.data(),  pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_q,    h_q.data(),    pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_dRe,  h_dispRe.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_dIm,  h_dispIm.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_cRe,  h_caRe.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_cIm,  h_caIm.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));

    // 融合核需要 64 KB 动态共享内存，必须显式放开上限
    CUDA_CHECK(cudaFuncSetAttribute(buildFFTPostKernel,
                                    cudaFuncAttributeMaxDynamicSharedMemorySize,
                                    fftLen * (int)sizeof(double2)));
    CUDA_CHECK(cudaMemset(d_acc, 0, outBytes * sizeof(float)));

    cudaEvent_t evA, evB;
    CUDA_CHECK(cudaEventCreate(&evA));
    CUDA_CHECK(cudaEventCreate(&evB));
    const double t4 = nowMs();

    // ============ 5. 逐帧计算 + 写盘 ============
    // 软件流水：本帧核函数启动后（异步），先去写上一帧的 BMP，
    // 让 CPU 的写盘时间和 GPU 的计算时间重叠。
    CUDA_CHECK(cudaEventRecord(evA));
    std::vector<int16_t> raw((size_t)pixNum * linNum);
    std::vector<uint8_t> imgBuf[2];
    imgBuf[0].resize(outBytes);
    imgBuf[1].resize(outBytes);
    int  cur = 0, lastFilled = 0;
    bool havePrev = false;
    std::string base, outName, prevName;
    base.reserve(128); outName.reserve(160); prevName.reserve(160);
    int failedSaves = 0;

    for (int n = 0; n < frameNum; n++) {
        FILE* f = fopen(binFiles[n].c_str(), "rb");
        if (!f) { printf("Cannot open %s\n", binFiles[n].c_str()); return 1; }
        size_t rd = fread(raw.data(), sizeof(int16_t), raw.size(), f);
        fclose(f);
        if (rd != raw.size()) { printf("short read: %s\n", binFiles[n].c_str()); return 1; }

        CUDA_CHECK(cudaMemcpy(d_raw, raw.data(), frameI16Bytes, cudaMemcpyHostToDevice));
        rowMeanKernel<<<(pixNum + 255) / 256, 256>>>(d_raw, d_mean, pixNum, linNum);
        CUDA_CHECK(cudaGetLastError());

        buildFFTPostKernel<<<linNum, 1024, fftLen * sizeof(double2)>>>(
            d_raw, d_mean, d_srcL, d_srcR, d_den, d_q,
            d_dRe, d_dIm, d_cRe, d_cIm, d_brev,
            d_img, d_acc, pixNum, linNum, startPoint, endPoint,
            grayScale1, grayScale2);
        CUDA_CHECK(cudaGetLastError());

        // 趁 GPU 在跑本帧的核函数，先把上一帧写盘（两个缓冲交替，避免覆盖）
        if (havePrev) {
            if (!saveBMP(prevName.c_str(), imgBuf[1 - cur].data(), linNum, outRows)) failedSaves++;
            if (verbose) printf("  saved (pipelined) %s\n", prevName.c_str());
        }

        CUDA_CHECK(cudaMemcpy(imgBuf[cur].data(), d_img, outBytes, cudaMemcpyDeviceToHost));

        base.assign(binFiles[n], binFiles[n].rfind('\\') + 1, std::string::npos);
        base.erase(base.size() - 4);                     // 去掉 .bin
        prevName = std::string(outDir) + "\\" + base + ".bmp";
        lastFilled = cur;
        havePrev = true;
        cur ^= 1;
    }
    if (havePrev) {
        if (!saveBMP(prevName.c_str(), imgBuf[lastFilled].data(), linNum, outRows)) failedSaves++;
        if (verbose) printf("  saved %s\n", prevName.c_str());
    }

    // 平均图（累加缓冲一直在显存里，这里只回传一次 uint8）
    uint8_t* d_meanImg = nullptr;
    CUDA_CHECK(cudaMalloc(&d_meanImg, outBytes));
    meanImageKernel<<<(int)((outBytes + 255) / 256), 256>>>(d_acc, d_meanImg, (int)outBytes, frameNum);
    CUDA_CHECK(cudaGetLastError());
    CUDA_CHECK(cudaMemcpy(imgBuf[0].data(), d_meanImg, outBytes, cudaMemcpyDeviceToHost));
    std::string meanName = std::string(outDir) + "\\mean_all_bsacns.bmp";
    if (!saveBMP(meanName.c_str(), imgBuf[0].data(), linNum, outRows)) failedSaves++;
    cudaFree(d_meanImg);

    CUDA_CHECK(cudaEventRecord(evB));
    CUDA_CHECK(cudaEventSynchronize(evB));
    float gpuMs = 0.0f;
    CUDA_CHECK(cudaEventElapsedTime(&gpuMs, evA, evB));
    const double t5 = nowMs();

    printf("--- Timing ---\n");
    printf("read calib + scan data/ : %8.3f ms\n", t1 - t0);
    printf("CPU precompute tables   : %8.3f ms\n", t3 - t2);
    printf("GPU alloc + upload tbl  : %8.3f ms\n", t4 - tStart);
    printf("frame loop + mean + save: %8.3f ms  (%.3f ms/frame)\n", t5 - t4, (t5 - t4) / frameNum);
    printf("  [excluded] CUDA init  : %8.3f ms   (驱动建上下文, 与数据量无关)\n", tWarm1 - tWarm0);
    printf("------------------------\n");
    printf("Total in-process        : %8.3f ms  (%.3f ms/frame)\n", t5 - tStart, (t5 - tStart) / frameNum);
    printf("Total incl. CUDA init   : %8.3f ms\n", t5 - tProg);
    if (failedSaves) printf("FAILED: %d images could not be written\n", failedSaves);
    else             printf("OK: %d images + mean image written to %s/\n", frameNum, outDir);

    cudaFree(d_raw); cudaFree(d_mean); cudaFree(d_srcL); cudaFree(d_srcR);
    cudaFree(d_brev); cudaFree(d_den); cudaFree(d_q); cudaFree(d_dRe);
    cudaFree(d_dIm); cudaFree(d_cRe); cudaFree(d_cIm); cudaFree(d_img); cudaFree(d_acc);
    cudaEventDestroy(evA); cudaEventDestroy(evB);

    return failedSaves ? 1 : 0;
}
