# -*- coding: utf-8 -*-
"""对比工具：CUDA 输出 vs MATLAB 标杆，逐像素相减，输出统计 + 差值图。

用法:
    python compare_1000.py

统计:
    - 每帧 BMP 的 max diff / mean diff / 像素数
    - 平均图 mean_all_bsacns.bmp 的对比
"""
import os
import sys
import numpy as np
from PIL import Image

BASE = os.path.dirname(os.path.abspath(__file__))
# 注意：目录名曾经是 1000_matlab_benchmark / 1000_cuda，改名后这里没跟着改，
# 原来会直接报 "[ERR] MATLAB 标杆目录不存在"。现已改为实际目录名。
MATLAB_DIR = os.path.join(BASE, 'matlab_benchmark', 'OCT_Results')
CUDA_DIR = os.path.join(BASE, 'cuda', 'image_1000')
OUT_IMG = os.path.join(BASE, 'comparison_1000.bmp')

# 11 帧文件名（与 bin 同名）
NAMES = ['20260720_182235_430_%04d.bmp' % i for i in range(11)]


def load(path):
    return np.array(Image.open(path)).astype(np.float64)


def main():
    if not os.path.isdir(MATLAB_DIR):
        print('[ERR] MATLAB 标杆目录不存在: %s' % MATLAB_DIR)
        return 1
    if not os.path.isdir(CUDA_DIR):
        print('[ERR] CUDA 输出目录不存在: %s' % CUDA_DIR)
        return 1

    max_all = 0.0
    mean_all = 0.0
    diff_pixels = 0
    total_pixels = 0
    print('%-28s %10s %10s %12s' % ('文件', 'max diff', 'mean diff', '差异>0像素'))
    print('-' * 66)
    for name in NAMES:
        p_m = os.path.join(MATLAB_DIR, name)
        p_c = os.path.join(CUDA_DIR, name)
        if not (os.path.exists(p_m) and os.path.exists(p_c)):
            print('[WARN] 缺少 %s (matlab=%s cuda=%s)' % (name, os.path.exists(p_m), os.path.exists(p_c)))
            continue
        a = load(p_m)
        b = load(p_c)
        d = np.abs(a - b)
        mx = d.max()
        mn = d.mean()
        nz = int(np.sum(d > 0))
        npix = d.size
        max_all = max(max_all, mx)
        mean_all += mn
        diff_pixels += nz
        total_pixels += npix
        print('%-28s %10.1f %10.4f %8d / %d' % (name, mx, mn, nz, npix))

    # 平均图
    p_mm = os.path.join(MATLAB_DIR, 'mean_all_bsacns.bmp')
    p_cm = os.path.join(CUDA_DIR, 'mean_all_bsacns.bmp')
    if os.path.exists(p_mm) and os.path.exists(p_cm):
        a = load(p_mm)
        b = load(p_cm)
        d = np.abs(a - b)
        print('%-28s %10.1f %10.4f %8d / %d' % ('mean_all_bsacns.bmp', d.max(), d.mean(), int(np.sum(d > 0)), d.size))
        max_all = max(max_all, d.max())
        mean_all += d.mean()
        diff_pixels += int(np.sum(d > 0))
        total_pixels += d.size
    else:
        print('[WARN] 平均图缺失')

    print('-' * 66)
    print('汇总: max diff = %.1f, 平均 mean diff = %.4f, 差异像素 %d / %d (%.4f%%)'
          % (max_all, mean_all / (len(NAMES) + 1), diff_pixels, total_pixels,
             diff_pixels / total_pixels * 100))
    print('结论: %s' % ('PASS —— 每个像素差值 <= 1' if max_all <= 1
                        else 'FAIL —— 有像素差值 > 1'))

    # 生成对比图（Python | 差值 | CUDA 三联）
    try:
        p_m0 = os.path.join(MATLAB_DIR, NAMES[0])
        p_c0 = os.path.join(CUDA_DIR, NAMES[0])
        a = load(p_m0)
        b = load(p_c0)
        d = np.abs(a - b)
        d_img = np.clip(d * 2, 0, 255).astype(np.uint8)
        # 竖排三张（MATLAB / 差值 / CUDA）
        h, w = a.shape
        canvas = np.zeros((h * 3 + 20, w), dtype=np.uint8)
        canvas[0:h, :] = np.clip(a, 0, 255).astype(np.uint8)
        canvas[h + 10:2 * h + 10, :] = d_img
        canvas[2 * h + 20:3 * h + 20, :] = np.clip(b, 0, 255).astype(np.uint8)
        Image.fromarray(canvas, 'L').save(OUT_IMG)
        print('对比图已保存: %s' % OUT_IMG)
    except Exception as e:
        print('[WARN] 对比图生成失败: %s' % e)

    return 0 if max_all <= 1 else 2


if __name__ == '__main__':
    sys.exit(main())
