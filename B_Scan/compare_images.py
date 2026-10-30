# -*- coding: utf-8 -*-
"""
两张 BMP 图像对比工具
用法: python compare_images.py image1.bmp image2.bmp
输出: diff_map.bmp (差值图) + comparison.bmp (并排对比图)
"""
import sys
import numpy as np
from PIL import Image

def main():
    if len(sys.argv) != 3:
        print("用法: python compare_images.py image1.bmp image2.bmp")
        sys.exit(1)

    path1, path2 = sys.argv[1], sys.argv[2]
    img1 = np.array(Image.open(path1)).astype(float)
    img2 = np.array(Image.open(path2)).astype(float)

    if img1.shape != img2.shape:
        print(f"尺寸不一致: {path1}={img1.shape}, {path2}={img2.shape}")
        sys.exit(1)

    # 差值统计
    diff = np.abs(img1 - img2)
    over1 = int(np.sum(diff > 1))
    print(f"图像尺寸: {img1.shape}")
    print(f"最大差异: {diff.max():.1f} / 255")
    print(f"平均差异: {diff.mean():.2f} / 255")
    print(f"差异<=1的像素: {np.sum(diff <= 1) / diff.size * 100:.4f}%")
    print(f"差异>1的像素:  {over1}")
    print(f"差异<=2的像素: {np.sum(diff <= 2) / diff.size * 100:.1f}%")
    print(f"差异<=5的像素: {np.sum(diff <= 5) / diff.size * 100:.1f}%")
    print(f"差异>10的像素: {np.sum(diff > 10) / diff.size * 100:.1f}%")

    # 差值图: 绝对值, 放大对比度
    diff_vis = np.clip(diff * 2.0, 0, 255).astype(np.uint8)
    Image.fromarray(diff_vis, mode='L').save('diff_map.bmp')
    print("已保存: diff_map.bmp (差值图, 越亮差异越大)")

    # 并排对比图: 图1 | 差值 | 图2
    gap = np.full((img1.shape[0], 10), 128, dtype=np.uint8)
    side = np.hstack([
        np.array(Image.open(path1)),
        gap,
        diff_vis,
        gap,
        np.array(Image.open(path2))
    ])
    Image.fromarray(side, mode='L').save('comparison.bmp')
    print("已保存: comparison.bmp (并排对比)")

    # 判定：要求每个像素差值 <= 1
    if over1 == 0:
        print("结论: PASS —— 每个像素差值 <= 1")
    else:
        print(f"结论: FAIL —— 有 {over1} 个像素差值 > 1")
    # 退出码可用于自动化: 0 = PASS, 2 = FAIL
    sys.exit(0 if over1 == 0 else 2)

if __name__ == '__main__':
    main()
