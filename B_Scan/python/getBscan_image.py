# -*- coding: utf-8 -*-
"""
单帧 Bscan 数据处理程序（Python 版）
读取 OCT 原始干涉数据，经过光谱标校（波长-波数重采样）、
色散补偿、加窗、FFT 后生成一张 Bscan 灰度图像。
"""
import os
import time
import numpy as np
from scipy.io import loadmat
from PIL import Image

# ------------------------------------------------------------
# Config
# ------------------------------------------------------------
dataFile        = 'e3_bscan1.dat'         # 单帧 Bscan 原始数据文件（由 extract_bscan.py 生成）
correctionFile  = 'e3_imfirst.mat'      # 光谱标校参数文件（给定）
outFile         = 'bscan_image_python.bmp'  # 输出 Bscan 图像文件名
pixNum          = 2048                  # A-line 像素点数
linNum          = 256                   # 每帧 A-line 个数（横向扫描线数）
startPoint      = 1                     # 深度方向显示起始点（MATLAB 下标，从 1 开始）
endPoint        = 512                   # 深度方向显示终止点
bytesLen        = 2                     # 相机数据字节（给定）
fftLen          = 2048                  # FFT 点数
disp_coeff1     = -2
disp_coeff2     = 1                     # 二三阶色散补偿系数（给定）

grayScale1      = 1.5                   # 对数强度图的灰度截断下限
grayScale2      = 4.5                   # 对数强度图的灰度截断上限
# ------------------------------------------------------------

t0 = time.perf_counter()

# ------------------------------------------------------------
# 1. 读取文件
# ------------------------------------------------------------
c_ = loadmat(correctionFile)
cal = c_['CalStru'] if 'CalStru' in c_ else c_
MAmean = np.asarray(cal['MAmean']).ravel()
xi     = np.asarray(cal['xi']).ravel()
CArray = np.asarray(cal['CArray']).ravel()

if os.path.getsize(dataFile) != pixNum * linNum * bytesLen:
    raise ValueError(f'{dataFile} 大小不符')
C = np.fromfile(dataFile, dtype=np.int16).reshape(pixNum, linNum, order='F').astype(np.float32)
t1 = time.perf_counter()

# ------------------------------------------------------------
# 2. 计算 (CPU)
# ------------------------------------------------------------
l_ = np.linspace(-1, 1, pixNum)
dispersion = np.exp(1j * (disp_coeff1 * l_**2 + disp_coeff2 * l_**3))

subData = C.mean(axis=1, keepdims=True)
fringe  = C - subData

ord_ = np.argsort(MAmean)
MAmean_s = MAmean[ord_]
fringe_s = fringe[ord_, :]
ss = np.empty((pixNum, linNum), dtype=np.float32)
for j in range(linNum):
    ss[:, j] = np.interp(xi, MAmean_s, fringe_s[:, j])

ss = ss * dispersion[:, None]
ss = ss * CArray[:, None]
frq = np.abs(np.fft.fft(ss, n=fftLen, axis=0))
im = np.log10(frq[startPoint - 1:endPoint, :])
t2 = time.perf_counter()

# ------------------------------------------------------------
# 3. 后处理 + 写文件
# ------------------------------------------------------------
im = np.clip(im, grayScale1, grayScale2)
im = (im - grayScale1) / (grayScale2 - grayScale1) * 255
im = im.astype(np.uint8)
Image.fromarray(im, mode='L').save(outFile)
t3 = time.perf_counter()

# ------------------------------------------------------------
# 输出分段计时
# ------------------------------------------------------------
print(f'# Bscan image saved to: {outFile}')
print(f'--- Timing Breakdown ---')
print(f'Read files:       {(t1-t0)*1000:8.3f} ms')
print(f'Compute (CPU):    {(t2-t1)*1000:8.3f} ms')
print(f'Postprocess+save:{(t3-t2)*1000:8.3f} ms')
print(f'Total:            {(t3-t0)*1000:8.3f} ms')
