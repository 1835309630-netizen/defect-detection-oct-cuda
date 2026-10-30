// compare_bmp.cpp
// 对比 MATLAB 标杆输出与 CUDA 输出：逐像素相减，报告最大/平均差值
// 用法：compare_bmp.exe <MATLAB目录> <CUDA目录> [帧数]
// 编译：cl /O2 /EHsc compare_bmp.cpp
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cmath>
#include <string>
#include <vector>

// 读取 8 位灰度 BMP（逐字节解析头部，避免结构体对齐问题）
// 返回像素数据（自顶向下、每行 width 字节）
bool readBMP(const char* path, std::vector<uint8_t>& px, int& w, int& h) {
    FILE* f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "open fail: %s\n", path); return false; }

    unsigned char hdr[54];
    if (fread(hdr, 1, 54, f) != 54) { fprintf(stderr, "header fail\n"); fclose(f); return false; }
    if (hdr[0] != 'B' || hdr[1] != 'M') { fprintf(stderr, "not BMP\n"); fclose(f); return false; }

    w = hdr[18] | (hdr[19] << 8) | (hdr[20] << 16) | ((int32_t)hdr[21] << 24);
    h = hdr[22] | (hdr[23] << 8) | (hdr[24] << 16) | ((int32_t)hdr[25] << 24);
    int bitCount = hdr[28] | (hdr[29] << 8);
    uint32_t offBits = (uint32_t)(hdr[10] | (hdr[11] << 8) | (hdr[12] << 16) | ((uint32_t)hdr[13] << 24));
    if (bitCount != 8 || w <= 0 || h <= 0) { fclose(f); return false; }

    int rowSize = (w + 3) & ~3;
    int dataSize = rowSize * h;
    std::vector<uint8_t> raw(dataSize);
    fseek(f, offBits, SEEK_SET);
    if (fread(raw.data(), 1, dataSize, f) != (size_t)dataSize) { fprintf(stderr, "data fail\n"); fclose(f); return false; }
    fclose(f);

    // BMP 自底向上存储，转成自顶向下
    px.resize((size_t)w * h);
    for (int y = 0; y < h; y++) {
        const uint8_t* src = raw.data() + (size_t)(h - 1 - y) * rowSize;
        for (int x = 0; x < w; x++) px[(size_t)y * w + x] = src[x];
    }
    return true;
}

int main(int argc, char** argv) {
    const char* dirA = (argc > 1) ? argv[1] : "..\\matlab_benchmark";  // MATLAB 标杆
    const char* dirB = (argc > 2) ? argv[2] : "image_e3";              // CUDA 输出
    int frames = (argc > 3) ? atoi(argv[3]) : 512;

    int maxAll = 0, diff1cnt = 0;
    long long sumDiff = 0;
    long long totalPx = 0;
    int worstFrame = -1;
    std::vector<int> over1Frames;   // 最大差值 > 1 的帧

    for (int m = 1; m <= frames; m++) {
        char pa[512], pb[512];
        snprintf(pa, sizeof(pa), "%s\\%d.bmp", dirA, m);
        snprintf(pb, sizeof(pb), "%s\\%d.bmp", dirB, m);

        std::vector<uint8_t> a, b;
        int wa, ha, wb, hb;
        if (!readBMP(pa, a, wa, ha) || !readBMP(pb, b, wb, hb)) {
            printf("frame %d: 读取失败 (%s / %s)\n", m, pa, pb);
            return 1;
        }
        if (wa != wb || ha != hb) {
            printf("frame %d: 尺寸不一致 %dx%d vs %dx%d\n", m, wa, ha, wb, hb);
            return 1;
        }
        int w = wa, h = ha;
        int fmax = 0;
        for (size_t i = 0; i < a.size(); i++) {
            int d = abs((int)a[i] - (int)b[i]);
            if (d > fmax) fmax = d;
            sumDiff += d;
            if (d == 1) diff1cnt++;
        }
        totalPx += (long long)a.size();
        if (fmax > maxAll) { maxAll = fmax; worstFrame = m; }
        if (fmax > 1) over1Frames.push_back(m);
    }

    printf("=== 对比 MATLAB 标杆 与 CUDA 输出 ===\n");
    printf("对比帧数: %d, 每帧 %lld 像素\n", frames, totalPx / frames);
    printf("所有像素最大差值: %d (位于第 %d 帧)\n", maxAll, worstFrame);
    printf("平均绝对差值: %.4f\n", (double)sumDiff / totalPx);
    printf("差值恰为 1 的像素数: %d\n", diff1cnt);
    if (over1Frames.empty()) {
        printf("结论: 全部帧最大差值 <= 1\n");
    } else {
        printf("结论: 有 %d 帧超过阈值 1: ", (int)over1Frames.size());
        for (int i = 0; i < (int)over1Frames.size() && i < 20; i++)
            printf("%d ", over1Frames[i]);
        printf("\n");
    }
    // 退出码可用于自动化：0 = 通过（所有帧最大差值 <= 1），1 = 未通过
    return over1Frames.empty() ? 0 : 2;
}
