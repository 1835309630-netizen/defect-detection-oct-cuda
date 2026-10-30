// bscan_cuda.cu
// B-scan 重建 CUDA 版（混合精度，与 Python 版结果一致）
// 编译：nvcc -O2 -o bscan_cuda bscan_cuda.cu -lcufft

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <chrono>
#include <vector>
#include <algorithm>
#include <cuda_runtime.h>
#include <cufft.h>

// ---------------- Config (与 Python 版一致) ----------------
const int    pixNum     = 2048;
const int    linNum     = 256;
const int    fftLen     = 2048;
const int    startPoint = 1;
const int    endPoint   = 512;
const double disp_coeff1 = -2.0;
const double disp_coeff2 = 1.0;
const double grayScale1  = 1.5;
const double grayScale2  = 4.5;

// ---------------- 简易 BMP 写入 (8位灰度) ----------------
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
    int32_t  biXPelsPerMeter = 2835;
    int32_t  biYPelsPerMeter = 2835;
    uint32_t biClrUsed = 256;
    uint32_t biClrImportant = 256;
};
#pragma pack(pop)

void saveBMP(const char* filename, const uint8_t* data, int width, int height) {
    int rowSize = (width + 3) & ~3;
    int imageSize = rowSize * height;

    BMPFileHeader fh;
    BMPInfoHeader ih;
    ih.biWidth = width;
    ih.biHeight = height;
    fh.bfSize = 54 + 256 * 4 + imageSize;

    FILE* f = fopen(filename, "wb");
    if (!f) { printf("cannot open %s\n", filename); return; }
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
    fclose(f);
}

// ---------------- CUDA Kernels (double 精度) ----------------

// 1) 去直流
__global__ void subtractMean(float* data, const float* mean, int rows, int cols) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols;
    if (idx < total) {
        int i = idx % rows;
        data[idx] -= mean[i];
    }
}

// 2) 排序后重排
__global__ void reorderRows(float* fs, const float* fringe, const int* ord, int rows, int cols) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols;
    if (idx < total) {
        int j = idx / rows;
        int i = idx % rows;
        fs[idx] = fringe[ord[i] + j * rows];
    }
}

// 3) 线性插值：float32 输入，float64 计算，结果截断成 float32（与 Python np.interp 行为一致）
__global__ void interpResample(float* ss, const float* xi,
                               const float* MAmean_s, const float* fringe_s,
                               int nXi, int rows, int cols) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = nXi * cols;
    if (idx >= total) return;

    int j    = idx / nXi;
    int k    = idx % nXi;
    double x = (double)xi[k];

    int lo = 0, hi = rows - 1;
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        if ((double)MAmean_s[mid] < x) lo = mid + 1;
        else hi = mid;
    }
    int left = lo - 1;
    if (left < 0) left = 0;
    int right = lo;
    if (right >= rows) right = rows - 1;

    double x0 = (double)MAmean_s[left];
    double x1 = (double)MAmean_s[right];
    double f0 = (double)fringe_s[left + j * rows];
    double f1 = (double)fringe_s[right + j * rows];

    double val;
    if (right == left || x1 == x0) {
        val = f0;
    } else {
        val = f0 + (f1 - f0) * (x - x0) / (x1 - x0);
    }
    ss[k + j * nXi] = (float)val;  // 截断成 float32
}

// 4) 色散补偿 + 加窗：ss(float32) → 复数乘法（与 Python float32*complex128 提升一致）
//    dispersion = exp(i*phase), CArray = re + i*im
//    result = ss * dispersion * CArray
__global__ void dispAndWindow(cufftDoubleComplex* out, const float* ss,
                              const float* CArray_re, const float* CArray_im,
                              double coeff1, double coeff2,
                              int rows, int cols) {
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = rows * cols;
    if (idx >= total) return;

    int i = idx % rows;
    int j = idx / rows;

    double l = 2.0 * i / (rows - 1) - 1.0;
    double phase = coeff1 * l * l + coeff2 * l * l * l;
    double c = cos(phase);
    double s = sin(phase);

    double s_val = (double)ss[idx];
    double cre = (double)CArray_re[i];
    double cim = (double)CArray_im[i];

    // 先乘 dispersion: (s*c) + i*(s*s)
    // 再乘 CArray (复数): (a+ib)(c+id) = (ac-bd) + i(ad+bc)
    double a = s_val * c;
    double b = s_val * s;
    out[idx].x = a * cre - b * cim;
    out[idx].y = a * cim + b * cre;
}

// 5) FFT 取模 + 截取深度
__global__ void magnitudeDepth(double* out, const cufftDoubleComplex* fftResult,
                              int rows, int cols, int start, int end) {
    int outRows = end - start + 1;
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = outRows * cols;
    if (idx >= total) return;

    int d = idx % outRows;
    int j = idx / outRows;

    int fftIdx = (start - 1 + d) + j * rows;
    double re = fftResult[fftIdx].x;
    double im = fftResult[fftIdx].y;
    out[idx] = sqrt(re * re + im * im);
}

// ---------------- 主函数 ----------------
int main() {
    printf("=== B-scan CUDA Reconstruction (double precision) ===\n");
    auto now = []() { return std::chrono::high_resolution_clock::now(); };
    auto ms  = [](auto a, auto b) {
        return std::chrono::duration<double, std::milli>(b - a).count();
    };

    // ============ 1. 读文件 (CPU) ============
    auto t_file0 = now();
    FILE* f = fopen("e3_bscan1.dat", "rb");
    if (!f) { printf("Cannot open e3_bscan1.dat\n"); return 1; }
    std::vector<int16_t> raw(pixNum * linNum);
    fread(raw.data(), sizeof(int16_t), pixNum * linNum, f);
    fclose(f);

    // Python 版是 float32，这里也用 float32 读入，计算时转 double
    std::vector<float> h_data(pixNum * linNum);
    for (int i = 0; i < pixNum * linNum; i++) h_data[i] = (float)raw[i];

    auto readBin = [](const char* name, std::vector<float>& buf) {
        FILE* fp = fopen(name, "rb");
        if (!fp) { printf("Cannot open %s\n", name); exit(1); }
        buf.resize(pixNum);
        fread(buf.data(), sizeof(float), pixNum, fp);
        fclose(fp);
    };
    std::vector<float> h_MAmean, h_xi, h_CArray_re, h_CArray_im;
    readBin("MAmean.bin", h_MAmean);
    readBin("xi.bin", h_xi);
    readBin("CArray_re.bin", h_CArray_re);
    readBin("CArray_im.bin", h_CArray_im);
    auto t_file1 = now();

    // ============ 2. CPU 预处理 (排序 + 算平均光谱) ============
    auto t_cpu0 = now();
    std::vector<int> h_ord(pixNum);
    for (int i = 0; i < pixNum; i++) h_ord[i] = i;
    std::sort(h_ord.begin(), h_ord.end(),
              [&](int a, int b) { return h_MAmean[a] < h_MAmean[b]; });

    std::vector<float> h_MAmean_s(pixNum);
    for (int i = 0; i < pixNum; i++) h_MAmean_s[i] = h_MAmean[h_ord[i]];

    std::vector<float> h_mean(pixNum, 0.0f);
    for (int j = 0; j < linNum; j++)
        for (int i = 0; i < pixNum; i++)
            h_mean[i] += h_data[i + j * pixNum];
    for (int i = 0; i < pixNum; i++) h_mean[i] /= linNum;
    auto t_cpu1 = now();

    // ============ 3. GPU 计算 (含传输) ============
    cudaEvent_t gStart, gStop;
    cudaEventCreate(&gStart);
    cudaEventCreate(&gStop);

    float *d_data, *d_mean, *d_MAmean_s, *d_xi, *d_CArray_re, *d_CArray_im, *d_fs, *d_ss;
    cufftDoubleComplex *d_cplx;
    int* d_ord;
    int dataBytes = pixNum * linNum * sizeof(float);

    cudaEventRecord(gStart);

    cudaMalloc(&d_data, dataBytes);
    cudaMalloc(&d_mean, pixNum * sizeof(float));
    cudaMalloc(&d_MAmean_s, pixNum * sizeof(float));
    cudaMalloc(&d_xi, pixNum * sizeof(float));
    cudaMalloc(&d_CArray_re, pixNum * sizeof(float));
    cudaMalloc(&d_CArray_im, pixNum * sizeof(float));
    cudaMalloc(&d_fs, dataBytes);
    cudaMalloc(&d_ss, dataBytes);
    cudaMalloc(&d_cplx, pixNum * linNum * sizeof(cufftDoubleComplex));
    cudaMalloc(&d_ord, pixNum * sizeof(int));

    cudaMemcpy(d_data, h_data.data(), dataBytes, cudaMemcpyHostToDevice);
    cudaMemcpy(d_MAmean_s, h_MAmean_s.data(), pixNum * sizeof(float), cudaMemcpyHostToDevice);
    cudaMemcpy(d_xi, h_xi.data(), pixNum * sizeof(float), cudaMemcpyHostToDevice);
    cudaMemcpy(d_CArray_re, h_CArray_re.data(), pixNum * sizeof(float), cudaMemcpyHostToDevice);
    cudaMemcpy(d_CArray_im, h_CArray_im.data(), pixNum * sizeof(float), cudaMemcpyHostToDevice);
    cudaMemcpy(d_ord, h_ord.data(), pixNum * sizeof(int), cudaMemcpyHostToDevice);
    cudaMemcpy(d_mean, h_mean.data(), pixNum * sizeof(float), cudaMemcpyHostToDevice);

    // 去直流
    {
        int total = pixNum * linNum;
        int block = 256, grid = (total + block - 1) / block;
        subtractMean<<<grid, block>>>(d_data, d_mean, pixNum, linNum);
    }
    // 排序重排
    {
        int total = pixNum * linNum;
        int block = 256, grid = (total + block - 1) / block;
        reorderRows<<<grid, block>>>(d_fs, d_data, d_ord, pixNum, linNum);
    }
    // 插值重采样
    {
        int total = pixNum * linNum;
        int block = 256, grid = (total + block - 1) / block;
        interpResample<<<grid, block>>>(d_ss, d_xi, d_MAmean_s, d_fs,
                                        pixNum, pixNum, linNum);
    }
    // 色散补偿 + 加窗
    {
        int total = pixNum * linNum;
        int block = 256, grid = (total + block - 1) / block;
        dispAndWindow<<<grid, block>>>(d_cplx, d_ss, d_CArray_re, d_CArray_im,
                                       disp_coeff1, disp_coeff2,
                                       pixNum, linNum);
    }
    // cuFFT (double 精度 Z2Z)
    cufftHandle plan;
    int n[1] = { fftLen };
    int inembed[1] = { fftLen };
    int onembed[1] = { fftLen };
    cufftPlanMany(&plan, 1, n, inembed, 1, fftLen, onembed, 1, fftLen,
                  CUFFT_Z2Z, linNum);
    cufftExecZ2Z(plan, d_cplx, d_cplx, CUFFT_FORWARD);

    // 取模 + 截取深度
    int outRows = endPoint - startPoint + 1;
    double* d_out;
    cudaMalloc(&d_out, outRows * linNum * sizeof(double));
    {
        int total = outRows * linNum;
        int block = 256, grid = (total + block - 1) / block;
        magnitudeDepth<<<grid, block>>>(d_out, d_cplx, pixNum, linNum,
                                        startPoint, endPoint);
    }

    // 拷回结果
    std::vector<double> h_out(outRows * linNum);
    cudaMemcpy(h_out.data(), d_out, outRows * linNum * sizeof(double),
               cudaMemcpyDeviceToHost);

    cudaEventRecord(gStop);
    cudaEventSynchronize(gStop);
    float gpuMs = 0.0f;
    cudaEventElapsedTime(&gpuMs, gStart, gStop);

    // ============ 4. CPU 后处理 + 写文件 ============
    auto t_post0 = now();
    std::vector<uint8_t> img(outRows * linNum);
    for (int d = 0; d < outRows; d++) {
        for (int j = 0; j < linNum; j++) {
            double v = h_out[d + j * outRows];
            double lv = log10(v > 1e-10 ? v : 1e-10);
            if (lv < grayScale1) lv = grayScale1;
            if (lv > grayScale2) lv = grayScale2;
            img[d * linNum + j] = (uint8_t)((lv - grayScale1) / (grayScale2 - grayScale1) * 255.0);
        }
    }
    saveBMP("bscan_image_cuda.bmp", img.data(), linNum, outRows);
    auto t_post1 = now();

    // ============ 输出分段计时 ============
    printf("\n--- Timing Breakdown ---\n");
    printf("Read files (CPU):      %8.3f ms\n", ms(t_file0, t_file1));
    printf("CPU preprocess:        %8.3f ms\n", ms(t_cpu0, t_cpu1));
    printf("GPU compute + transfer: %8.3f ms\n", gpuMs);
    printf("CPU postprocess + save:%8.3f ms\n", ms(t_post0, t_post1));
    printf("------------------------\n");
    double total = ms(t_file0, t_file1) + ms(t_cpu0, t_cpu1) + gpuMs + ms(t_post0, t_post1);
    printf("Total:                 %8.3f ms\n", total);

    // --- 清理 ---
    cufftDestroy(plan);
    cudaFree(d_data); cudaFree(d_mean); cudaFree(d_MAmean_s);
    cudaFree(d_xi); cudaFree(d_CArray_re); cudaFree(d_CArray_im); cudaFree(d_fs);
    cudaFree(d_ss); cudaFree(d_cplx); cudaFree(d_ord);
    cudaFree(d_out);
    cudaEventDestroy(gStart);
    cudaEventDestroy(gStop);

    return 0;
}
