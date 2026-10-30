# -*- coding: utf-8 -*-
"""
把 e3_imfirst.mat 里的标定参数导出成 CUDA 程序读取的 4 个 .bin 文件。

用法:
    python export_calib_bins.py [mat文件] [输出目录]
默认: 在 ..\\python\\e3_imfirst.mat 读取, 输出到本脚本所在目录。

.bin 用 float64 写出。bscan_cuda.exe 会按文件大小自动识别:
    2048*8 字节 -> float64  (推荐, 与 Python 标杆逐字节一致)
    2048*4 字节 -> float32  (旧文件, 与 Python 标杆有 172/131072 个像素差 1)
"""
import os
import sys
import numpy as np
from scipy.io import loadmat


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    mat_file = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, '..', 'python', 'e3_imfirst.mat')
    out_dir = sys.argv[2] if len(sys.argv) > 2 else here

    c_ = loadmat(mat_file)
    cal = c_['CalStru'] if 'CalStru' in c_ else c_
    MAmean = np.asarray(cal['MAmean']).ravel()
    xi = np.asarray(cal['xi']).ravel()
    CArray = np.asarray(cal['CArray']).ravel()

    print(f'MAmean : {MAmean.dtype} {MAmean.shape}')
    print(f'xi     : {xi.dtype} {xi.shape}')
    print(f'CArray : {CArray.dtype} {CArray.shape} complex={np.iscomplexobj(CArray)}')

    for name, arr in (('MAmean.bin', MAmean.real),
                      ('xi.bin', xi.real),
                      ('CArray_re.bin', CArray.real),
                      ('CArray_im.bin', CArray.imag)):
        path = os.path.join(out_dir, name)
        np.ascontiguousarray(arr, dtype='<f8').tofile(path)
        print(f'  wrote {name:16s} {os.path.getsize(path)} bytes')


if __name__ == '__main__':
    main()
