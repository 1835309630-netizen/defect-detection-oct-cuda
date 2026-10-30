// bscan_cuda.cu
// 单帧 B-scan 重建 CUDA 版（与 Python 标杆 getBscan_image.py 结果一致）
//
// 流程（与 Python 程序严格一致）：
//   读 int16 -> 逐行求平均光谱 -> 逐列 去直流 -> 按 MAmean 排序重排 -> 线性插值重采样
//   -> 色散补偿 + 乘 CArray -> FFT -> 取模 -> log10 -> 灰度截断映射 -> 存 BMP
//
// 编译：nvcc -O2 -Xcompiler /utf-8 -arch=native -o bscan_cuda.exe bscan_cuda.cu
//       注意：源码含中文注释，必须带 -Xcompiler /utf-8，否则 MSVC 会按 GBK 解析而误吞代码行。
//
// ===========================================================================
// 相对旧版的改动
// ---------------------------------------------------------------------------
// 【速度】
//  [S1] 去掉 cuFFT，改用自带的 radix-2 FFT（共享内存，一次核函数内完成）。
//       实测本机 cuFFT 建一次 2048 点 plan 要 ~45 ms（与 batch 无关，只与长度有关），
//       而实际 256x2048 复数 FFT 只要 0.005 ms —— 建 plan 的开销比整个 Python 程序还大。
//       自写 FFT 与 np.fft 的相对误差 ~2.5e-14，对最终灰度无影响。
//  [S2] 编译加 -arch=native（本机 CC 12.0）。旧版没写 -arch，nvcc 默认 sm_52，
//       只能靠驱动 PTX JIT 运行：实测整机墙钟 208 ms -> 163 ms。
//  [S3] 核函数由 5 个（去直流/重排/插值/色散加窗/取模）合并为 2 个：
//       rowMean + 一个融合核（建输入 + FFT + 取模 + log10 + 量化）。
//       中间数组 d_data/d_fs/d_ss/d_cplx/d_out 全部不再需要，显存往返从 ~24 MB 降到 ~2 MB。
//  [S4] 每行标定因子 dispersion(i)*CArray(i)、插值括号 (srcL,srcR,den,q)、
//       每列 bit-reversal 表都在 CPU 上预计算一次（各 2048 项），
//       消除了旧版每个元素重算 cos/sin、以及每列重复 256 次的二分查找。
//  [S5] 只把 uint8 结果拷回（128 KB），而不是 double（512 KB）。
//
// 【稳定】
//  [T1] 所有 CUDA API 检查返回值，核函数后检查 cudaGetLastError，失败即报错退出。
//  [T2] 校验输入 .dat 尺寸、4 个 .bin 尺寸，并检查短读。
//  [T3] 标定 .bin 按文件大小自动识别 float64 / float32；推荐 float64（见下）。
//  [T4] 编译期断言 fftLen == pixNum（融合核依赖此前提），运行期校验 endPoint <= pixNum。
//  [T5] BMP 头与 PIL 完全对齐（biSizeImage / biXPelsPerMeter / biYPelsPerMeter /
//       biClrImportant），输出与 Python 版逐字节一致。
//  [T6] 写盘失败会统计并返回非 0 退出码。
//
// 【精度】
//  [P1] 标定文件由 float32 换成 float64 后，与 Python 标杆的差异从 172 个像素
//       （0.131%）降到 0（逐字节相同）。float32 .bin 是 .mat 的 double 截断，
//       插值权重因此有 ~1e-5 的相对误差；程序会自动识别，无需改代码。
// ===========================================================================

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cerrno>
#include <chrono>
#include <vector>
#include <algorithm>
#include <cuda_runtime.h>

// ---------------- Config (与 Python 版一致) ----------------
const int    pixNum      = 2048;    // A-line 像素点数
const int    linNum      = 256;     // 每帧 A-line 个数
const int    fftLen      = 2048;    // FFT 点数
const int    startPoint  = 1;       // 深度方向显示起始点（Python 下标从 0 开始 -> 1 基）
const int    endPoint    = 512;     // 深度方向显示终止点
const int    bytesLen    = 2;       // int16
const double disp_coeff1 = -2.0;
const double disp_coeff2 =  1.0;
const double grayScale1  =  1.5;
const double grayScale2  =  4.5;

const char*  dataFile   = "e3_bscan1.dat";
const char*  outFile    = "bscan_image_cuda.bmp";

const int    LOG2N      = 11;       // log2(2048)
const int    outRows    = endPoint - startPoint + 1;

// 融合核把 FFT 结果留在共享内存里直接取模，要求变换长度等于行数
static_assert(fftLen == pixNum, "fused FFT kernel requires fftLen == pixNum");
static_assert((1 << LOG2N) == pixNum, "LOG2N must equal log2(pixNum)");
static_assert(startPoint >= 1 && endPoint <= pixNum, "display window must be inside [1, pixNum]");

// ---------------- 错误检查 ----------------
#define CUDA_CHECK(call)                                                        \
    do {                                                                        \
        cudaError_t _e = (call);                                                \
        if (_e != cudaSuccess) {                                                \
            printf("[CUDA ERROR] %s:%d  %s -> %s\n", __FILE__, __LINE__, #call, \
                   cudaGetErrorString(_e));                                     \
            return 1;                                                           \
        }                                                                       \
    } while (0)

// ---------------- BMP 写入 (8 位灰度, 头部字段与 PIL 完全一致) ----------------
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
    int32_t  biXPelsPerMeter = 3780;   // PIL: 96 dpi
    int32_t  biYPelsPerMeter = 3780;
    uint32_t biClrUsed = 256;
    uint32_t biClrImportant = 256;
};
#pragma pack(pop)

static bool saveBMP(const char* filename, const uint8_t* data, int width, int height) {
    int rowSize   = (width + 3) & ~3;
    int imageSize = rowSize * height;

    BMPFileHeader fh;
    BMPInfoHeader ih;
    ih.biWidth     = width;
    ih.biHeight    = height;
    ih.biSizeImage = (uint32_t)imageSize;
    fh.bfSize      = 54 + 256 * 4 + imageSize;

    FILE* f = fopen(filename, "wb");
    if (!f) { printf("cannot open %s (errno=%d)\n", filename, errno); return false; }

    fwrite(&fh, 1, sizeof(fh), f);
    fwrite(&ih, 1, sizeof(ih), f);
    uint8_t pal[4];
    for (int i = 0; i < 256; i++) {
        pal[0] = pal[1] = pal[2] = (uint8_t)i; pal[3] = 0;
        fwrite(pal, 1, 4, f);
    }
    // 自底向上写：data 是 [depth][A-line] 行主序，与 Python 的 im[d, j] 一致
    std::vector<uint8_t> rowBuf(rowSize, 0);
    for (int y = height - 1; y >= 0; y--) {
        memcpy(rowBuf.data(), data + (size_t)y * width, width);
        fwrite(rowBuf.data(), 1, rowSize, f);
    }
    bool ok = (ferror(f) == 0);
    if (fclose(f) != 0) ok = false;
    if (!ok) printf("write failed: %s (errno=%d)\n", filename, errno);
    return ok;
}

// ---------------- 设备端常量表 ----------------
__device__ double2 g_tw[pixNum / 2];        // exp(-2*pi*i*k/N), k = 0..N/2-1

// ---------------- Kernel 1: 每行（每个像素点）的平均光谱 ----------------
// mean[i] = mean_j data[i + j*rows]
// 线程 i 读 data[i + j*rows]：同一 warp 内 i 连续 -> 访存完全合并。
__global__ void rowMeanKernel(const short* __restrict__ data,
                              float* __restrict__ mean,
                              int rows, int cols) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= rows) return;
    double s = 0.0;                          // int16 数据，256 项和 <= 8.4e6，double/float32 均精确
    for (int j = 0; j < cols; j++) s += (double)data[i + (size_t)j * rows];
    mean[i] = (float)(s / (double)cols);
}

// ---------------- Kernel 2: 建输入 + FFT + 取模 + 量化 ----------------
// 一个 block 处理一列（一条 A-line）：
//   1) 按 (srcL,srcR,den,q) 做 numpy.interp 式的线性插值（slope 先算），
//      再乘预计算的行标定因子，直接得到 float32 的频域输入（与 Python 链一致）
//   2) 共享内存内做 radix-2 DIT FFT（原位，先 bit-reversal）
//   3) 取模 -> log10 -> 截断 -> 线性映射 -> uint8（截断，对应 np.astype(np.uint8)）
__global__ void buildFFTKernel(const short* __restrict__ data,
                               const float* __restrict__ mean,
                               const int*   __restrict__ srcL,
                               const int*   __restrict__ srcR,
                               const double* __restrict__ den,
                               const double* __restrict__ q,
                               const double* __restrict__ scaleRe,
                               const double* __restrict__ scaleIm,
                               const int*   __restrict__ brev,
                               uint8_t*     __restrict__ out,
                               int rows, int cols,
                               double g1, double g2) {
    __shared__ double2 sm[pixNum];           // 2048 * 16 B = 32 KB
    const int j   = blockIdx.x;
    const int tid = threadIdx.x;
    const int N   = rows;

    // ---- 1) 建输入（自然序）----
    for (int k = tid; k < N; k += blockDim.x) {
        const int L = srcL[k];
        const int R = srcR[k];
        // 与 Python 完全一致的精度链：先算 float32 的 fringe（去直流），再在 double 里插值，
        // 结果截断回 float32，最后按 double 乘复数标定因子。
        float y0 = (float)data[L + (size_t)j * N] - mean[L];
        float y1 = (float)data[R + (size_t)j * N] - mean[R];
        // numpy.interp: slope = (y1-y0)/(x1-x0);  result = slope*(x-x0) + y0
        double slope = ((double)y1 - (double)y0) / den[k];
        float  ss    = (float)(slope * q[k] + (double)y0);
        double2 v;
        v.x = (double)ss * scaleRe[k];
        v.y = (double)ss * scaleIm[k];
        sm[k] = v;
    }
    __syncthreads();

    // ---- 2) bit-reversal 置换（自然序 -> DIT 所需顺序）----
    for (int k = tid; k < N; k += blockDim.x) {
        int r = brev[k];
        if (k < r) { double2 t = sm[k]; sm[k] = sm[r]; sm[r] = t; }
    }
    __syncthreads();

    // ---- 3) radix-2 DIT，共 LOG2N 级 ----
    for (int s = 0; s < LOG2N; s++) {
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

    // ---- 4) 取模 + log10 + 灰度映射，只写需要的深度区间 ----
    for (int d = tid; d < outRows; d += blockDim.x) {
        const double2 z = sm[startPoint - 1 + d];
        const double mag = sqrt(z.x * z.x + z.y * z.y);
        double lv = log10(mag > 1e-10 ? mag : 1e-10);
        if (lv < g1) lv = g1;
        if (lv > g2) lv = g2;
        double v = (lv - g1) / (g2 - g1) * 255.0;
        if (v < 0.0)   v = 0.0;
        if (v > 255.0) v = 255.0;
        out[(size_t)d * cols + j] = (uint8_t)v;   // 截断，与 np.astype(np.uint8) 一致
    }
}

// ---------------- 主机端小工具 ----------------
static bool g_verbose     = false;   // -v：额外打印诊断信息
static bool g_calibFloat32 = false;  // 标定文件是 float32 时给出告警

static double nowMs() {
    using namespace std::chrono;
    return duration<double, std::milli>(high_resolution_clock::now().time_since_epoch()).count();
}

// 读标定 bin：按文件大小自动识别 float64 / float32
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
        if (g_verbose) printf("  %-16s float64 (%d values)\n", name, n);
    } else if (sz == (long long)n * 4) {
        std::vector<float> t(n);
        if (fread(t.data(), 4, n, fp) != (size_t)n) { printf("%s: short read\n", name); fclose(fp); return false; }
        for (int i = 0; i < n; i++) buf[i] = (double)t[i];
        g_calibFloat32 = true;
        if (g_verbose) printf("  %-16s float32 (%d values)\n", name, n);
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
    for (int i = 1; i < argc; i++)
        if (!strcmp(argv[i], "-v") || !strcmp(argv[i], "--verbose")) g_verbose = true;

    if (g_verbose)
        printf("=== B-scan CUDA Reconstruction (double precision, built-in FFT) ===\n");

    // ============ 0. 预热：建立 CUDA 上下文（对应 Python 的 import，不计入 Total）============
    // 与 python\getBscan_image.py 的口径对齐：那边 t0 记在 import numpy/scipy/PIL 之后，
    // 把 ~610 ms 的导入排除在外；这里把驱动建 CUDA 上下文（~70 ms）同样排除在外。
    // 两者剩下的都是"纯流水线"，可以直接比。被排除的耗时在 -v 里会打印出来。
    const double tWarm0 = nowMs();
    CUDA_CHECK(cudaFree(0));                       // 逼出上下文创建
    const double tWarm1 = nowMs();

    const double tStart = nowMs();                 // ← 计时起点，与 Python 的 t0 对等

    // ============ 1. 读原始数据 ============
    const double t0 = nowMs();
    FILE* f = fopen(dataFile, "rb");
    if (!f) { printf("Cannot open %s\n", dataFile); return 1; }
    fseek(f, 0, SEEK_END);
    const long long fsz = (long long)ftell(f);
    fseek(f, 0, SEEK_SET);
    const long long need = (long long)pixNum * linNum * bytesLen;
    if (fsz != need) {
        printf("%s size mismatch: %lld bytes, expected %lld\n", dataFile, fsz, need);
        fclose(f);
        return 1;
    }
    std::vector<short> h_raw(pixNum * linNum);
    if (fread(h_raw.data(), sizeof(short), h_raw.size(), f) != h_raw.size()) {
        printf("%s: short read\n", dataFile);
        fclose(f);
        return 1;
    }
    fclose(f);

    // ============ 2. 读标定参数 ============
    std::vector<double> MAmean, xi, CRe, CIm;
    if (!readBin("MAmean.bin", MAmean, pixNum))    return 1;
    if (!readBin("xi.bin", xi, pixNum))            return 1;
    if (!readBin("CArray_re.bin", CRe, pixNum))    return 1;
    if (!readBin("CArray_im.bin", CIm, pixNum))    return 1;
    if (g_calibFloat32)
        printf("[WARN] calibration .bin is float32; run export_calib_bins.py to match Python exactly\n");
    const double t1 = nowMs();

    // ============ 3. CPU 预处理：排序 + 预计算所有逐行/逐列表 ============
    const double t2 = nowMs();

    // 3a) MAmean 排序（与 np.argsort(MAmean) 等价；用 stable_sort 保证有重复值时也确定）
    std::vector<int> ord(pixNum);
    for (int i = 0; i < pixNum; i++) ord[i] = i;
    std::stable_sort(ord.begin(), ord.end(),
                     [&](int a, int b) { return MAmean[a] < MAmean[b]; });
    std::vector<double> MA_s(pixNum);
    for (int i = 0; i < pixNum; i++) MA_s[i] = MAmean[ord[i]];

    // 3b) 每列插值括号 + 权重分量（只依赖标定点，与数据无关）
    std::vector<int>    h_srcL(pixNum), h_srcR(pixNum), h_brev(pixNum);
    std::vector<double> h_den(pixNum), h_q(pixNum), h_scaleRe(pixNum), h_scaleIm(pixNum);

    for (int k = 0; k < pixNum; k++) {
        const double x = xi[k];
        int L, R;
        double den, q;
        if (x <= MA_s[0]) {                       // np.interp 对越界点取端点值
            L = R = ord[0]; den = 1.0; q = 0.0;
        } else if (x >= MA_s[pixNum - 1]) {
            L = R = ord[pixNum - 1]; den = 1.0; q = 0.0;
        } else {
            // 第一个 MA_s >= x 的下标（MA_s 升序）
            int lo = 0, hi = pixNum - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1;
                if (MA_s[mid] < x) lo = mid + 1; else hi = mid; }
            L = ord[lo - 1]; R = ord[lo];
            den = MA_s[lo] - MA_s[lo - 1];
            q   = x - MA_s[lo - 1];
        }
        h_srcL[k] = L; h_srcR[k] = R; h_den[k] = den; h_q[k] = q;
    }

    // 3c) 行标定因子 scale(i) = dispersion(i) * CArray(i)
    //     linspace(-1,1,pixNum) 按 numpy 的算法复现：-1 + step*i，末点精确取 1
    for (int i = 0; i < pixNum; i++) {
        const double step = 2.0 / (pixNum - 1);
        double l = -1.0 + step * (double)i;
        if (i == pixNum - 1) l = 1.0;
        const double phase = disp_coeff1 * l * l + disp_coeff2 * l * l * l;
        const double c = cos(phase), s = sin(phase);
        const double cre = CRe[i], cim = CIm[i];
        h_scaleRe[i] = c * cre - s * cim;         // (c + i s) * (cre + i cim)
        h_scaleIm[i] = c * cim + s * cre;
    }

    // 3d) 11 位 bit-reversal 表
    for (int k = 0; k < pixNum; k++) {
        int r = 0, v = k;
        for (int b = 0; b < LOG2N; b++) { r = (r << 1) | (v & 1); v >>= 1; }
        h_brev[k] = r;
    }

    // 3e) FFT 旋转因子 exp(-2*pi*i*k/N)
    const double PI = 3.14159265358979323846264338327950288;
    std::vector<double2> h_tw(pixNum / 2);
    for (int k = 0; k < pixNum / 2; k++) {
        const double ang = -2.0 * PI * (double)k / (double)pixNum;
        h_tw[k].x = cos(ang);
        h_tw[k].y = sin(ang);
    }
    const double t3 = nowMs();

    // ============ 4. GPU ============
    const double t4 = nowMs();
    const size_t dataBytes = (size_t)pixNum * linNum * sizeof(short);

    short*  d_raw   = nullptr;
    float*  d_mean  = nullptr;
    int*    d_srcL  = nullptr;
    int*    d_srcR  = nullptr;
    int*    d_brev  = nullptr;
    double* d_den   = nullptr;
    double* d_q     = nullptr;
    double* d_scRe  = nullptr;
    double* d_scIm  = nullptr;
    uint8_t* d_out  = nullptr;

    CUDA_CHECK(cudaMemcpyToSymbol(g_tw, h_tw.data(), h_tw.size() * sizeof(double2)));
    CUDA_CHECK(cudaMalloc(&d_raw,  dataBytes));
    CUDA_CHECK(cudaMalloc(&d_mean, pixNum * sizeof(float)));
    CUDA_CHECK(cudaMalloc(&d_srcL, pixNum * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_srcR, pixNum * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_brev, pixNum * sizeof(int)));
    CUDA_CHECK(cudaMalloc(&d_den,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_q,    pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_scRe, pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_scIm, pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_out,  (size_t)outRows * linNum));
    const double t4b = nowMs();

    CUDA_CHECK(cudaMemcpy(d_raw,  h_raw.data(),  dataBytes, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_srcL, h_srcL.data(), pixNum * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_srcR, h_srcR.data(), pixNum * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_brev, h_brev.data(), pixNum * sizeof(int),    cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_den,  h_den.data(),  pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_q,    h_q.data(),    pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_scRe, h_scaleRe.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_scIm, h_scaleIm.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    const double t5 = nowMs();

    cudaEvent_t evA, evB;
    CUDA_CHECK(cudaEventCreate(&evA));
    CUDA_CHECK(cudaEventCreate(&evB));
    CUDA_CHECK(cudaEventRecord(evA));

    // 4a) 平均光谱
    rowMeanKernel<<<(pixNum + 255) / 256, 256>>>(d_raw, d_mean, pixNum, linNum);
    CUDA_CHECK(cudaGetLastError());

    // 4b) 建输入 + FFT + 后处理（一列一个 block）
    buildFFTKernel<<<linNum, 1024>>>(d_raw, d_mean, d_srcL, d_srcR, d_den, d_q,
                                     d_scRe, d_scIm, d_brev, d_out, pixNum, linNum,
                                     grayScale1, grayScale2);
    CUDA_CHECK(cudaGetLastError());

    // 4c) 只回传 uint8
    std::vector<uint8_t> img((size_t)outRows * linNum);
    CUDA_CHECK(cudaMemcpy(img.data(), d_out, img.size(), cudaMemcpyDeviceToHost));

    CUDA_CHECK(cudaEventRecord(evB));
    CUDA_CHECK(cudaEventSynchronize(evB));
    float gpuMs = 0.0f;
    CUDA_CHECK(cudaEventElapsedTime(&gpuMs, evA, evB));

    // ============ 5. 写文件 ============
    const double t7 = nowMs();
    const bool saved = saveBMP(outFile, img.data(), linNum, outRows);
    const double t8 = nowMs();

    // ============ 计时（口径与格式都与 python\getBscan_image.py 对齐） ============
    // Python: t0 记在 import 之后 -> 排除运行时启动；这里同样把建 CUDA 上下文排除在 tStart 之前。
    // 两边剩下的都是纯流水线，Compute 一行可以直接比。
    if (!saved) fprintf(stderr, "FAILED: cannot write %s\n", outFile);

    printf("# Bscan image saved to: %s\n", outFile);
    printf("--- Timing Breakdown ---\n");
    printf("Read files:       %8.3f ms\n", t1 - t0);
    printf("Compute (GPU):    %8.3f ms\n", t7 - t1);
    printf("Postprocess+save:%8.3f ms\n", t8 - t7);
    printf("Total:            %8.3f ms\n", t8 - tStart);

    if (g_verbose) {
        printf("\n(detail)\n");
        printf("  [excluded] CUDA context init: %8.3f ms   <- 对应 Python 的 import, 在 Total 之外\n", tWarm1 - tWarm0);
        printf("  read .dat + 4 x .bin:   %8.3f ms\n", t1 - t0);
        printf("  CPU tables (sort/etc):  %8.3f ms\n", t3 - t2);
        printf("  cudaMalloc x11:         %8.3f ms\n", t4b - t4);
        printf("  uploads:                %8.3f ms\n", t5 - t4b);
        printf("  GPU kernels + download: %8.3f ms\n", gpuMs);
        printf("  save BMP:               %8.3f ms\n", t8 - t7);
        printf("  ------------------------------------------------------------\n");
        printf("  Compute (GPU) = CPU tables + cudaMalloc + uploads + kernels + download\n");
        printf("  Total 不含 CUDA 上下文创建(上面 [excluded] 那行);\n");
        // 字面量以 ASCII 收尾，避免 nvcc 按 GBK 解析时吞掉结尾字节
        printf("%s\n", "  Python 的 Total 同样不含解释器与 numpy/scipy/PIL 的导入。(~610 ms)");
        printf("  含一切的真实墙钟约: %.0f ms (进程启动约 10 ms + 上下文 %.0f ms + Total %.1f ms)\n",
               10.0 + (tWarm1 - tWarm0) + (t8 - tStart), tWarm1 - tWarm0, t8 - tStart);
    }

    cudaFree(d_raw); cudaFree(d_mean); cudaFree(d_srcL); cudaFree(d_srcR);
    cudaFree(d_brev); cudaFree(d_den); cudaFree(d_q); cudaFree(d_scRe);
    cudaFree(d_scIm); cudaFree(d_out);
    cudaEventDestroy(evA); cudaEventDestroy(evB);
    return saved ? 0 : 1;
}
