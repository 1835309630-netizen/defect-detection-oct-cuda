// bscan_cuda512.cu
// OCT 多帧 B-scan 重建 CUDA 版（与 MATLAB 标杆 getOCTimage4_1_opt_wykgpu_cpu.m 结果一致）
// 一次性重建 e3.dat 中的全部帧，输出 BMP 到 image_e3/ 文件夹
//
// 流程（与 MATLAB 程序一致）：
//   逐批(32帧)：读入 -> 去直流 -> 按 MAmean 排序重排 -> 线性插值重采样
//              -> 色散补偿 + 乘窗函数CArray -> FFT -> 取模 -> log10
//              -> 灰度截断映射 -> 存 BMP
//
// 优化（相对早期版本）：
//   1) int16->float32 转换放在 GPU 上做（少一半 H2D 传输）
//   2) log10/截断/映射后处理放在 GPU 上做，D2H 只回传 uint8（传输量减 8 倍）
//   3) BMP 写入多线程并行
//
// 编译：nvcc -O2 -o bscan_cuda512 bscan_cuda512.cu -lcufft
//
// ===========================================================================
// 修订记录（对齐 MATLAB 标杆）
// ---------------------------------------------------------------------------
// [1] 【致命】自动创建输出目录 image_e3。
//     旧版假定该目录已存在；在干净环境下 512 次 fopen 全部失败，程序却仍打印
//     "Done" 并返回 0，一张图都不会生成。MATLAB 侧有 mkdir(folderName)。
// [2] 【精度】灰度量化由"截断"改为"四舍五入"。
//     MATLAB 的 uint8(x) 是 round-half-away-from-zero；旧的 (uint8_t)v 直接截断，
//     导致约 47% 的像素系统性比 MATLAB 小 1（实测 31714818/67108864 像素差 1，
//     且 CUDA 从不大于 MATLAB，是典型的截断特征）。
// [3] 【精度】标定参数按实际文件尺寸自动按 float64 / float32 读取，
//     设备端统一用 double，消除 float32 标定带来的插值误差。
//     实测：float32 标定 -> 0.13% 像素差 1；float64 标定 -> 0.002% 像素差 1。
// [4] 【一致性】BMP 头字段与 MATLAB imwrite 完全对齐
//     (biSizeImage / biXPelsPerMeter / biYPelsPerMeter / biClrImportant)，
//     使输出文件与 MATLAB 逐字节一致。
// [5] 【稳健性】std::sort -> std::stable_sort。
//     MAmean 转成 float32 后存在 1 对相等值，非稳定排序可能调换这两行，
//     使对应查询点的插值结果偏离 MATLAB（MATLAB interp1 内部按样本点排序）。
// [6] 【稳健性】帧数不再硬编码 512，改由文件大小推导，与 MATLAB 的
//     frameNum = bytes/pixNum/linNum/bytesLen 一致，并支持非 32 整除的尾批。
// [7] 【稳健性】增加 CUDA API / cuFFT 返回值检查；写盘失败会统计并返回非 0
//     退出码，不再"静默成功"。
// [8] 【稳健性】检查 xi 是否落在 MAmean 值域内。超出时 MATLAB interp1 返回 NaN
//     （uint8 后为 0），而本程序会钳位/外插，属已知偏离，现给出告警。
// ===========================================================================

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cerrno>
#include <chrono>
#include <vector>
#include <algorithm>
#include <thread>
#include <direct.h>
#include <cuda_runtime.h>
#include <cufft.h>

// ---------------- Config (与 MATLAB / 标杆一致) ----------------
const int    pixNum      = 2048;    // A-line 像素点数
const int    linNum      = 256;     // 每帧 A-line 个数
const int    fftLen      = 2048;    // FFT 点数
const int    startPoint  = 1;       // 深度方向显示起始点
const int    endPoint    = 512;     // 深度方向显示终止点
const int    batchSize   = 32;      // 每批处理的帧数
const int    saveThreads = 8;       // BMP 写入线程数
const double disp_coeff1 = -2.0;
const double disp_coeff2 =  1.0;
const double grayScale1  = 1.5;
const double grayScale2  = 4.5;

const char*  dataFile    = "../e3.dat";     // 对应 MATLAB 的 dataFile
const char*  outDir      = "image_e3";      // 对应 MATLAB 的 ['image_' fn_]

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

#define CUFFT_CHECK(call)                                                       \
    do {                                                                        \
        cufftResult _r = (call);                                                \
        if (_r != CUFFT_SUCCESS) {                                              \
            printf("[cuFFT ERROR] %s:%d  %s -> code %d\n", __FILE__, __LINE__,  \
                   #call, (int)_r);                                             \
            exit(1);                                                            \
        }                                                                       \
    } while (0)

// ---------------- 简易 BMP 写入 (8位灰度, 与 MATLAB imwrite 一致) ----------------
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
    int32_t  biXPelsPerMeter = 0;   // MATLAB imwrite 写 0
    int32_t  biYPelsPerMeter = 0;   // MATLAB imwrite 写 0
    uint32_t biClrUsed = 256;
    uint32_t biClrImportant = 0;    // MATLAB imwrite 写 0
};
#pragma pack(pop)

bool saveBMP(const char* filename, const uint8_t* data, int width, int height) {
    int rowSize = (width + 3) & ~3;
    int imageSize = rowSize * height;

    BMPFileHeader fh;
    BMPInfoHeader ih;
    ih.biWidth  = width;
    ih.biHeight = height;
    ih.biSizeImage = (uint32_t)imageSize;
    fh.bfSize   = 54 + 256 * 4 + imageSize;

    FILE* f = fopen(filename, "wb");
    if (!f) { printf("cannot open %s (errno=%d)\n", filename, errno); return false; }
    fwrite(&fh, 1, sizeof(fh), f);
    fwrite(&ih, 1, sizeof(ih), f);

    uint8_t pal[4];
    for (int i = 0; i < 256; i++) {
        pal[0] = pal[1] = pal[2] = (uint8_t)i; pal[3] = 0;
        fwrite(pal, 1, 4, f);
    }

    std::vector<uint8_t> rowBuf(rowSize, 0);
    for (int y = height - 1; y >= 0; y--) {
        for (int x = 0; x < width; x++) rowBuf[x] = data[y * width + x];
        fwrite(rowBuf.data(), 1, rowSize, f);
    }
    bool ok = (ferror(f) == 0);
    if (fclose(f) != 0) ok = false;
    if (!ok) printf("write failed: %s (errno=%d)\n", filename, errno);
    return ok;
}

// ---------------- CUDA Kernels (double 精度) ----------------
// 内存布局：列主序（与 MATLAB reshape 一致）
//   帧内索引 idx_in = i + j*rows （i:行=像素, j:列=A-line）
//   批内全局索引 = b*rows*cols + idx_in

// 0) int16 -> float32
__global__ void convertI16F32(float* out, const short* in, int total) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    if (idx < total) out[idx] = (float)in[idx];
}

// 1) 平均光谱 subData(b,i) = mean over cols（double 累加，结果转 float32）
//    数据为 int16，累加和 <= 256*32767 = 8.4e6 < 2^24，float32 累加同样精确，
//    故与 MATLAB single 版 mean(C,2) 完全一致。
__global__ void computeMean(const float* data, float* mean, int rows, int cols, int batch) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    int b = blockIdx.y;
    if (i >= rows || b >= batch) return;
    double s = 0.0;
    const float* d = data + (size_t)b * rows * cols + i;
    for (int j = 0; j < cols; j++) s += (double)d[j * rows];
    mean[(size_t)b * rows + i] = (float)(s / cols);
}

// 2) 去直流
__global__ void subtractMean(float* data, const float* mean, int rows, int cols, int batch) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols * batch;
    if (idx >= total) return;
    int b  = idx / (rows * cols);
    int in = idx % (rows * cols);
    int i  = in % rows;
    data[idx] -= mean[(size_t)b * rows + i];
}

// 3) 排序后重排（按 MAmean 的排序下标 ord）
__global__ void reorderRows(float* fs, const float* fringe, const int* ord, int rows, int cols, int batch) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols * batch;
    if (idx >= total) return;
    int b  = idx / (rows * cols);
    int in = idx % (rows * cols);
    int i  = in % rows;
    int j  = in / rows;
    fs[idx] = fringe[(size_t)b * rows * cols + ord[i] + j * rows];
}

// 4) 线性插值重采样 = MATLAB interp1(MAmean, fringe, xi)
//    MATLAB interp1 对非单调的 MAmean 内部同样按样本点排序（已实测：结果与
//    先排序再插值逐位相同），且本例 xi 完全落在 [min(MAmean), max(MAmean)] 内，
//    不存在 NaN 外插。这里用 double 计算、末尾取整成 float32（对应 MATLAB
//    输出为 single 的行为）。
__global__ void interpResample(float* ss, const double* xi,
                               const double* MAmean_s, const float* fringe_s,
                               int nXi, int rows, int cols, int batch) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = nXi * cols * batch;
    if (idx >= total) return;

    int b  = idx / (nXi * cols);
    int in = idx % (nXi * cols);
    int j  = in / nXi;
    int k  = in % nXi;
    double x = xi[k];

    // 找第一个 MAmean_s >= x 的下标（MAmean_s 升序）
    int lo = 0, hi = rows - 1;
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        if (MAmean_s[mid] < x) lo = mid + 1;
        else hi = mid;
    }
    int left  = lo - 1; if (left < 0) left = 0;
    int right = lo;     if (right >= rows) right = rows - 1;

    double x0 = MAmean_s[left];
    double x1 = MAmean_s[right];
    double f0 = (double)fringe_s[(size_t)b * rows * cols + left + j * rows];
    double f1 = (double)fringe_s[(size_t)b * rows * cols + right + j * rows];

    double val;
    if (right == left || x1 == x0) {
        val = f0;
    } else {
        val = f0 + (f1 - f0) * (x - x0) / (x1 - x0);
    }
    ss[(size_t)b * nXi * cols + k + j * nXi] = (float)val;
}

// 5) 色散补偿 + 加窗：result = ss * dispersion * CArray
__global__ void dispAndWindow(cufftDoubleComplex* out, const float* ss,
                              const double* CArray_re, const double* CArray_im,
                              double coeff1, double coeff2,
                              int rows, int cols, int batch) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols * batch;
    if (idx >= total) return;

    int in = idx % (rows * cols);
    int i  = in % rows;

    double l = 2.0 * i / (rows - 1) - 1.0;          // linspace(-1,1,pixNum) 的等价写法
    double phase = coeff1 * l * l + coeff2 * l * l * l;
    double c = cos(phase);
    double s = sin(phase);

    double s_val = (double)ss[idx];
    double cre = CArray_re[i];
    double cim = CArray_im[i];

    // (s_val*(c+i*s)) * (cre+i*cim)
    double a  = s_val * c;
    double b_ = s_val * s;
    out[idx].x = a * cre - b_ * cim;
    out[idx].y = a * cim + b_ * cre;
}

// 6) FFT 取模 + 截取深度 + log10 + 灰度截断映射 -> uint8
__global__ void magnitudeLog10U8(uint8_t* out, const cufftDoubleComplex* fftResult,
                                 int rows, int cols, int batch, int start, int end,
                                 double g1, double g2) {
    int outRows = end - start + 1;
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = outRows * cols * batch;
    if (idx >= total) return;

    int b  = idx / (outRows * cols);
    int in = idx % (outRows * cols);
    int d  = in % outRows;
    int j  = in / outRows;

    size_t fftIdx = (size_t)b * rows * cols + (start - 1 + d) + (size_t)j * rows;
    double re = fftResult[fftIdx].x;
    double im = fftResult[fftIdx].y;
    double v  = sqrt(re * re + im * im);
    double lv = log10(v > 1e-10 ? v : 1e-10);   // MATLAB: log10(0) = -Inf，截断后同为下限
    if (lv < g1) lv = g1;
    if (lv > g2) lv = g2;
    // MATLAB: uint8(im) —— 四舍五入（round half away from zero），不是截断
    double q = (lv - g1) / (g2 - g1) * 255.0 + 0.5;
    if (q < 0.0)   q = 0.0;     // 与 uint8(x) 的饱和行为一致
    if (q > 255.0) q = 255.0;
    out[(size_t)b * outRows * cols + d * cols + j] = (uint8_t)q;
}

// ---------------- 主函数 ----------------
int main() {
    auto now = []() { return std::chrono::high_resolution_clock::now(); };
    auto ms  = [](auto a, auto b) {
        return std::chrono::duration<double, std::milli>(b - a).count();
    };

    auto t_file0 = now();

    // ============ 0. 准备输出目录（对应 MATLAB 的 mkdir(folderName)） ============
    if (_mkdir(outDir) != 0 && errno != EEXIST) {
        printf("Cannot create output folder %s (errno=%d)\n", outDir, errno);
        return 1;
    }
    {
        char probe[512];
        snprintf(probe, sizeof(probe), "%s/.write_test", outDir);
        FILE* tf = fopen(probe, "wb");
        if (!tf) { printf("Output folder %s is not writable\n", outDir); return 1; }
        fclose(tf);
        remove(probe);
    }

    // ============ 1. 读原始数据 (CPU) ============
    FILE* f = fopen(dataFile, "rb");
    if (!f) { printf("Cannot open %s\n", dataFile); return 1; }
    fseek(f, 0, SEEK_END);
    long long fileBytes = (long long)ftell(f);
    fseek(f, 0, SEEK_SET);

    // MATLAB: frameNum = bytes/pixNum/linNum/bytesLen; 非整数时 fix(frameNum)-1
    double frameNumD = (double)fileBytes / (double)(pixNum * linNum * 2);
    int frameNum = (int)frameNumD;
    if ((double)frameNum != frameNumD) frameNum = frameNum - 1;
    if (frameNum <= 0) { printf("Data file too small: %lld bytes\n", fileBytes); fclose(f); return 1; }

    printf("=== B-scan CUDA Reconstruction x%d frames (double precision) ===\n", frameNum);
    printf("data file: %s (%lld bytes) -> %d frames\n", dataFile, fileBytes, frameNum);

    size_t totalElems = (size_t)pixNum * linNum * frameNum;
    std::vector<int16_t> raw(totalElems);
    size_t rd = fread(raw.data(), sizeof(int16_t), totalElems, f);
    fclose(f);
    if (rd != totalElems) { printf("read %zu / %zu elements\n", rd, totalElems); return 1; }

    // 读标定参数 bin（由 e3_imfirst.mat 导出；按文件大小自动识别 float64 / float32）
    auto readBin = [](const char* name, std::vector<double>& buf, int n) {
        FILE* fp = fopen(name, "rb");
        if (!fp) { printf("Cannot open %s\n", name); exit(1); }
        fseek(fp, 0, SEEK_END);
        long long sz = (long long)ftell(fp);
        fseek(fp, 0, SEEK_SET);
        buf.assign(n, 0.0);
        if (sz == (long long)n * 8) {
            std::vector<double> t(n);
            if (fread(t.data(), 8, n, fp) != (size_t)n) { printf("%s: short read\n", name); exit(1); }
            buf.swap(t);
            printf("  %-16s float64 (%d values)\n", name, n);
        } else if (sz == (long long)n * 4) {
            std::vector<float> t(n);
            if (fread(t.data(), 4, n, fp) != (size_t)n) { printf("%s: short read\n", name); exit(1); }
            for (int i = 0; i < n; i++) buf[i] = (double)t[i];
            printf("  %-16s float32 (%d values)  [see note 3: re-export as float64 for higher accuracy]\n", name, n);
        } else {
            printf("%s: unexpected size %lld (expected %d or %d bytes)\n",
                   name, sz, n * 8, n * 4);
            exit(1);
        }
        fclose(fp);
    };
    std::vector<double> h_MAmean, h_xi, h_CArray_re, h_CArray_im;
    readBin("MAmean.bin",    h_MAmean,    pixNum);
    readBin("xi.bin",        h_xi,        pixNum);
    readBin("CArray_re.bin", h_CArray_re, pixNum);
    readBin("CArray_im.bin", h_CArray_im, pixNum);
    auto t_file1 = now();

    // ============ 2. CPU 预处理：MAmean 排序（只需一次） ============
    // 用 stable_sort：MAmean 转 float32 后存在相等值，非稳定排序可能打乱这两行，
    // 使对应查询点取到相邻的另一行数据，与 MATLAB 的排序结果不一致。
    auto t_cpu0 = now();
    std::vector<int> h_ord(pixNum);
    for (int i = 0; i < pixNum; i++) h_ord[i] = i;
    std::stable_sort(h_ord.begin(), h_ord.end(),
                     [&](int a, int b) { return h_MAmean[a] < h_MAmean[b]; });

    std::vector<double> h_MAmean_s(pixNum);
    for (int i = 0; i < pixNum; i++) h_MAmean_s[i] = h_MAmean[h_ord[i]];

    // [8] xi 必须落在 [min(MAmean), max(MAmean)] 之内。否则 MATLAB interp1 返回
    //     NaN（uint8(NaN)=0），而本程序做端点钳位 / 线性外插，结果会不同。
    //     本例 xi in [-1.0705, 1.8251] 完全落在 MAmean 的 [-1.0802, 4.7207] 内，
    //     不会触发；此检查是为了防止以后换标定文件时静默偏离 MATLAB。
    {
        int outOfRange = 0;
        const double lo = h_MAmean_s.front(), hi = h_MAmean_s.back();
        for (int i = 0; i < pixNum; i++)
            if (h_xi[i] < lo || h_xi[i] > hi) outOfRange++;
        if (outOfRange) {
            printf("[WARN] %d of %d xi samples lie outside [min(MAmean), max(MAmean)] = [%.6g, %.6g].\n",
                   outOfRange, pixNum, lo, hi);
            printf("       MATLAB interp1 gives NaN there (-> 0 after uint8); this program clamps/extrapolates.\n");
        }
    }
    auto t_cpu1 = now();

    // ============ 3. 分配 GPU 内存 + cuFFT plan (一次性) ============
    cudaEvent_t gStart, gStop;
    CUDA_CHECK(cudaEventCreate(&gStart));
    CUDA_CHECK(cudaEventCreate(&gStop));

    size_t frameBytes    = (size_t)pixNum * linNum * sizeof(float);
    size_t frameI16Bytes = (size_t)pixNum * linNum * sizeof(short);
    size_t batchBytes    = frameBytes * batchSize;
    size_t batchI16Bytes = frameI16Bytes * batchSize;
    int    outRows       = endPoint - startPoint + 1;
    size_t outBatchBytes = (size_t)outRows * linNum * batchSize;   // uint8

    short* d_raw;
    float *d_data, *d_mean, *d_fs, *d_ss;
    cufftDoubleComplex* d_cplx;
    uint8_t* d_img;
    int* d_ord;
    CUDA_CHECK(cudaMalloc(&d_raw,  batchI16Bytes));
    CUDA_CHECK(cudaMalloc(&d_data, batchBytes));
    CUDA_CHECK(cudaMalloc(&d_mean, (size_t)pixNum * batchSize * sizeof(float)));
    CUDA_CHECK(cudaMalloc(&d_fs,   batchBytes));
    CUDA_CHECK(cudaMalloc(&d_ss,   batchBytes));
    CUDA_CHECK(cudaMalloc(&d_cplx, (size_t)pixNum * linNum * batchSize * sizeof(cufftDoubleComplex)));
    CUDA_CHECK(cudaMalloc(&d_img,  outBatchBytes));
    CUDA_CHECK(cudaMalloc(&d_ord,  pixNum * sizeof(int)));

    double *d_MAmean_s, *d_xi, *d_CArray_re, *d_CArray_im;
    CUDA_CHECK(cudaMalloc(&d_MAmean_s,   pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_xi,         pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_CArray_re,  pixNum * sizeof(double)));
    CUDA_CHECK(cudaMalloc(&d_CArray_im,  pixNum * sizeof(double)));

    CUDA_CHECK(cudaMemcpy(d_MAmean_s,  h_MAmean_s.data(),  pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_xi,        h_xi.data(),        pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_CArray_re, h_CArray_re.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_CArray_im, h_CArray_im.data(), pixNum * sizeof(double), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(d_ord,       h_ord.data(),       pixNum * sizeof(int),    cudaMemcpyHostToDevice));

    // cuFFT plan：batch = 每批帧数 * 每帧列数，沿列方向(rows)做 1D FFT
    cufftHandle plan;
    int n[1]       = { fftLen };
    int inembed[1] = { fftLen };
    int onembed[1] = { fftLen };
    int fftBatch   = linNum * batchSize;
    CUFFT_CHECK(cufftPlanMany(&plan, 1, n, inembed, 1, fftLen, onembed, 1, fftLen,
                              CUFFT_Z2Z, fftBatch));

    // ============ 4. 分批 GPU 计算 + CPU 写文件 ============
    std::vector<uint8_t> h_img(outRows * linNum * batchSize);
    int nBatch = (frameNum + batchSize - 1) / batchSize;
    int failedSaves = 0;
    double cpuSaveMs = 0.0;

    CUDA_CHECK(cudaEventRecord(gStart));

    for (int nb = 0; nb < nBatch; nb++) {
        int    curBatch    = std::min(batchSize, frameNum - nb * batchSize);  // 尾批
        size_t curI16Bytes = frameI16Bytes * curBatch;
        size_t curOutBytes = (size_t)outRows * linNum * curBatch;

        const short* src = raw.data() + (size_t)nb * batchSize * pixNum * linNum;
        CUDA_CHECK(cudaMemcpy(d_raw, src, curI16Bytes, cudaMemcpyHostToDevice));

        int total = pixNum * linNum * curBatch;
        int block = 256, grid = (total + block - 1) / block;

        convertI16F32<<<grid, block>>>(d_data, d_raw, total);
        CUDA_CHECK(cudaGetLastError());

        dim3 gMean((pixNum + 255) / 256, curBatch);
        computeMean<<<gMean, 256>>>(d_data, d_mean, pixNum, linNum, curBatch);
        CUDA_CHECK(cudaGetLastError());
        subtractMean<<<grid, block>>>(d_data, d_mean, pixNum, linNum, curBatch);
        CUDA_CHECK(cudaGetLastError());

        reorderRows<<<grid, block>>>(d_fs, d_data, d_ord, pixNum, linNum, curBatch);
        CUDA_CHECK(cudaGetLastError());
        interpResample<<<grid, block>>>(d_ss, d_xi, d_MAmean_s, d_fs,
                                        pixNum, pixNum, linNum, curBatch);
        CUDA_CHECK(cudaGetLastError());

        dispAndWindow<<<grid, block>>>(d_cplx, d_ss, d_CArray_re, d_CArray_im,
                                       disp_coeff1, disp_coeff2,
                                       pixNum, linNum, curBatch);
        CUDA_CHECK(cudaGetLastError());

        CUFFT_CHECK(cufftExecZ2Z(plan, d_cplx, d_cplx, CUFFT_FORWARD));

        {
            int totalOut = outRows * linNum * curBatch;
            int gridOut  = (totalOut + block - 1) / block;
            magnitudeLog10U8<<<gridOut, block>>>(d_img, d_cplx, pixNum, linNum,
                                                 curBatch, startPoint, endPoint,
                                                 grayScale1, grayScale2);
            CUDA_CHECK(cudaGetLastError());
        }

        CUDA_CHECK(cudaMemcpy(h_img.data(), d_img, curOutBytes, cudaMemcpyDeviceToHost));

        // 多线程写 BMP
        auto tp0 = now();
        std::vector<std::thread> workers;
        std::vector<int> threadFail(saveThreads, 0);
        for (int t = 0; t < saveThreads; t++) {
            workers.emplace_back([&, t]() {
                for (int b = t; b < curBatch; b += saveThreads) {
                    int frameIdx = nb * batchSize + b;
                    char name[512];
                    snprintf(name, sizeof(name), "%s/%d.bmp", outDir, frameIdx + 1);
                    if (!saveBMP(name, h_img.data() + (size_t)b * outRows * linNum,
                                 linNum, outRows))
                        threadFail[t]++;
                }
            });
        }
        for (auto& w : workers) w.join();
        for (int t = 0; t < saveThreads; t++) failedSaves += threadFail[t];
        cpuSaveMs += ms(tp0, now());
    }

    CUDA_CHECK(cudaEventRecord(gStop));
    CUDA_CHECK(cudaEventSynchronize(gStop));
    float gpuMs = 0.0f;
    CUDA_CHECK(cudaEventElapsedTime(&gpuMs, gStart, gStop));

    auto t_end = now();

    printf("\n--- Timing Breakdown ---\n");
    printf("Read raw data (CPU):    %8.3f ms\n", ms(t_file0, t_file1));
    printf("CPU preprocess (sort):  %8.3f ms\n", ms(t_cpu0, t_cpu1));
    printf("GPU compute + transfer: %8.3f ms  (%.3f ms/frame)\n", gpuMs, gpuMs / frameNum);
    printf("CPU save BMP (多线程):  %8.3f ms  (累计)\n", cpuSaveMs);
    printf("------------------------\n");
    printf("Total (wall):            %8.3f ms\n", ms(t_file0, t_end));
    if (failedSaves) printf("FAILED: %d of %d images could not be written\n", failedSaves, frameNum);
    else             printf("OK: %d images written to %s/\n", frameNum, outDir);

    cufftDestroy(plan);
    cudaFree(d_raw); cudaFree(d_data); cudaFree(d_mean); cudaFree(d_fs); cudaFree(d_ss);
    cudaFree(d_cplx); cudaFree(d_img); cudaFree(d_ord);
    cudaFree(d_MAmean_s); cudaFree(d_xi); cudaFree(d_CArray_re); cudaFree(d_CArray_im);
    cudaEventDestroy(gStart);
    cudaEventDestroy(gStop);

    return failedSaves ? 1 : 0;
}
