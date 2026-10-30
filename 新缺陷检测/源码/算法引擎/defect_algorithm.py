"""白色亮斑缺陷定位器。

在灰度喷漆金属表面上，找出"孤立、成块、比周围亮很多、内部全白无黑纹"的白色亮斑，
画框返回。C# 生产端通过 检测引擎.py 以子进程方式调用本模块。
"""

from __future__ import annotations

import argparse
import csv
import os
import tempfile
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw


支持格式 = {".tif", ".tiff", ".png", ".jpg", ".jpeg", ".bmp"}


@dataclass(frozen=True)
class 候选:
    x: int
    y: int
    width: int
    height: int
    area: int
    灰度异常强度: float
    峰值灰度差: float
    平均灰度差: float
    合并数量: int = 1

    @property
    def center(self) -> tuple[int, int]:
        return self.x + self.width // 2, self.y + self.height // 2


@dataclass(frozen=True)
class 零件信息:
    part_type: str
    center_x: float
    center_y: float
    rotation_deg: float
    template_name: str
    template_score: float
    accepted: bool = True
    status: str = "已识别"


@dataclass(frozen=True)
class 检测规则:
    """白色亮块的可解释规则；现场按实际图重新标定。"""

    # 亮块面积范围（像素）：要"较大"，太小的针尖不要
    最小面积: int = 400
    最大面积: int = 5000
    # 圆度放宽
    最低圆度: float = 0.35
    # 长宽比上限：树枝纹是细长的，真白斑是块
    最大长宽比: float = 2.5
    # 局部背景差阈值
    最低亮差比例: float = 5.0
    绝对最低亮差: float = 0.035
    # 内部均匀性
    最大内部暗纹比例: float = 0.10
    # 最多输出多少个（不限制时设大）
    最多输出: int = 10


默认规则 = 检测规则()


def 读取灰度(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        raw = np.asarray(image, dtype=np.float32)
    if raw.ndim == 3:
        raw = raw[..., :3].mean(axis=2)
    return raw


def 显示映射(raw: np.ndarray) -> np.ndarray:
    finite = np.isfinite(raw)
    if not finite.any():
        return np.zeros(raw.shape, dtype=np.uint8)
    low, high = np.percentile(raw[finite], (0.5, 99.5))
    if high <= low:
        high = low + 1.0
    return np.uint8(np.round(np.clip((raw - low) / (high - low), 0, 1) * 255))


def 最大连通域(binary: np.ndarray) -> np.ndarray:
    count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    if count <= 1:
        return binary
    label = int(np.argmax(stats[1:, cv2.CC_STAT_AREA])) + 1
    return np.uint8(labels == label) * 255


def 零件掩膜(display: np.ndarray, edge_margin: int) -> tuple[np.ndarray, np.ndarray]:
    """Otsu 分出亮的零件本体，再腐蚀出安全表面区。"""
    blurred = cv2.GaussianBlur(display, (0, 0), sigmaX=2.0)
    _, binary = cv2.threshold(blurred, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    binary = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)))
    part = 最大连通域(binary)
    part = cv2.morphologyEx(part, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (31, 31)))
    distance = cv2.distanceTransform(part, cv2.DIST_L2, 5)
    safe = np.uint8(distance >= edge_margin) * 255
    return part, safe


def 零件中心(part: np.ndarray) -> tuple[float, float]:
    moments = cv2.moments(part, binaryImage=True)
    if moments["m00"] <= 0:
        return part.shape[1] / 2, part.shape[0] / 2
    return moments["m10"] / moments["m00"], moments["m01"] / moments["m00"]


def 默认模板目录() -> Path:
    """保留接口以兼容旧调用；本版本不再使用模板。"""
    base = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
    return base / "模板"


def 局部背景(image: np.ndarray, weights: np.ndarray, sigma: float) -> np.ndarray:
    numerator = cv2.GaussianBlur(image * weights, (0, 0), sigmaX=sigma, sigmaY=sigma)
    denominator = cv2.GaussianBlur(weights, (0, 0), sigmaX=sigma, sigmaY=sigma)
    return numerator / np.maximum(denominator, 1e-4)


def 亮斑分数(display: np.ndarray, part: np.ndarray, safe: np.ndarray, rules: 检测规则 = 默认规则) -> tuple[np.ndarray, float]:
    """比局部背景亮的程度（旧版是暗，这里极性反转）。"""
    image = display.astype(np.float32) / 255.0
    weights = part.astype(np.float32) / 255.0
    scales = [np.maximum(image - 局部背景(image, weights, sigma), 0.0) for sigma in (4.0, 9.0, 18.0)]
    score = np.maximum.reduce(scales)
    score[safe == 0] = 0
    valid = score[safe > 0]
    if valid.size == 0:
        return score, 1.0
    median = float(np.median(valid))
    mad = float(np.median(np.abs(valid - median)))
    threshold = max(rules.绝对最低亮差, median + rules.最低亮差比例 * max(1.4826 * mad, 1.0 / 255.0))
    return score, threshold


def 计算圆度(mask_component: np.ndarray) -> float:
    """4πA/P^2，越接近1越圆；坑洼/喷射状轮廓周长很长，圆度低。"""
    contours, _ = cv2.findContours(mask_component.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return 0.0
    c = max(contours, key=cv2.contourArea)
    perimeter = cv2.arcLength(c, closed=True)
    area = cv2.contourArea(c)
    if perimeter <= 0:
        return 0.0
    return float(4 * np.pi * area / (perimeter * perimeter))


def _筛选亮块(labels, score, display, rules) -> list[tuple[float, 候选]]:
    """对每个连通域：面积够大、成块、内部全白无黑纹。"""
    scored: list[tuple[float, 候选]] = []
    max_label = int(labels.max())
    for label in range(1, max_label + 1):
        ys, xs = np.where(labels == label)
        if len(ys) == 0:
            continue
        y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
        area = int(len(ys))
        if area < rules.最小面积 or area > rules.最大面积:
            continue
        circularity = 计算圆度(np.uint8(labels == label))
        if circularity < rules.最低圆度:
            continue
        # 长宽比：树枝纹细长，真白斑是块
        aspect = max(x1 - x0, y1 - y0) / max(1, min(x1 - x0, y1 - y0))
        if aspect > rules.最大长宽比:
            continue

        # 内部均匀性：在原始显示灰度上看连通域内部有没有黑纹
        gray = display[ys, xs].astype(np.float32)
        gmean = float(gray.mean())
        # 比区域均值暗 35% 以上的像素视为"黑纹"
        dark_ratio = float(np.mean(gray < gmean * 0.65))
        if dark_ratio > rules.最大内部暗纹比例:
            continue

        comp = score[y0:y1, x0:x1][ys - y0, xs - x0]
        peak = float(comp.max())
        avg = float(comp.mean())
        scored.append((avg, 候选(int(xs.min()), int(ys.min()),
                                int(x1 - x0), int(y1 - y0), area,
                                100 * avg, peak, avg)))
    return scored


def 提取最明显亮斑(
    display: np.ndarray,
    part: np.ndarray,
    safe: np.ndarray,
    score: np.ndarray,
    threshold: float,
    rules: 检测规则 = 默认规则,
) -> list[候选]:
    """找出所有成块、突兀、内部全白的亮块，按亮差排序。"""
    binary = np.uint8((score >= threshold) & (safe > 0)) * 255
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)

    scored = _筛选亮块(labels, score, display, rules)
    scored.sort(key=lambda item: item[0], reverse=True)
    return [c for _, c in scored[:rules.最多输出]]


def 生成标注图(display: np.ndarray, candidates: list[候选], box_size: int = 96) -> Image.Image:
    canvas = Image.fromarray(display, mode="L").convert("RGB")
    draw = ImageDraw.Draw(canvas)
    half = box_size // 2
    for candidate in candidates:
        cx, cy = candidate.center
        box = (max(0, cx-half), max(0, cy-half),
               min(canvas.width-1, cx+half), min(canvas.height-1, cy+half))
        draw.rectangle(box, outline=(255, 30, 30), width=6)
    return canvas


def 检测数组(
    raw: np.ndarray,
    template_dir: Path | None = None,
    box_size: int = 96,
    edge_margin: int = 12,
    rules: 检测规则 = 默认规则,
) -> tuple[Image.Image, 零件信息, list[候选]]:
    """单通道灰度图检测入口。template_dir 保留兼容但不再使用。"""
    if raw.ndim != 2:
        raise ValueError("检测输入必须是单通道灰度图")
    if raw.size == 0 or min(raw.shape) < 4 or not np.isfinite(raw).all():
        raise ValueError("检测输入为空、尺寸过小或包含 NaN/Inf")

    display = 显示映射(np.asarray(raw, dtype=np.float32))
    part, safe = 零件掩膜(display, edge_margin)
    if not np.any(part):
        info = 零件信息("未分类", raw.shape[1]/2, raw.shape[0]/2, 0, "", 0, False, "未找到零件")
        return 生成标注图(display, [], box_size), info, []

    cx, cy = 零件中心(part)
    score, threshold = 亮斑分数(display, part, safe, rules)
    candidates = 提取最明显亮斑(display, part, safe, score, threshold, rules)
    info = 零件信息("白色亮斑", cx, cy, 0.0, "", 1.0, True,
                    f"检出 {len(candidates)} 处亮圆斑" if candidates else "未检出亮圆斑")
    return 生成标注图(display, candidates, box_size), info, candidates


def 保存结果总览(images: list[tuple[Path, str]], output: Path) -> None:
    if not images:
        return
    columns, tile_width, tile_height, label_height = 3, 408, 342, 28
    rows = (len(images) + columns - 1) // columns
    canvas = Image.new("RGB", (columns * tile_width, rows * (tile_height + label_height)), "white")
    draw = ImageDraw.Draw(canvas)
    for index, (path, part_type) in enumerate(images):
        with Image.open(path) as source:
            preview = source.convert("RGB")
            preview.thumbnail((tile_width, tile_height), Image.Resampling.LANCZOS)
        left = index % columns * tile_width
        top = index // columns * (tile_height + label_height)
        canvas.paste(preview, (left + (tile_width-preview.width)//2, top + (tile_height-preview.height)//2))
        draw.text((left + 6, top + tile_height + 7), part_type, fill="black")
    canvas.save(output, quality=94)


def 生成模板(paths: list[Path], output: Path) -> dict:
    """本版本不再需要模板。保留接口以兼容命令行，直接提示。"""
    return {"Ok": False, "Message": "本版本不再使用模板分类，此功能已停用。"}


def main() -> None:
    parser = argparse.ArgumentParser(description="检测喷漆表面上最明显的白色亮圆斑")
    parser.add_argument("input", type=Path, help="单张图或图像文件夹")
    parser.add_argument("--output", type=Path, default=Path("检测结果"))
    parser.add_argument("--box-size", type=int, default=96)
    parser.add_argument("--edge-margin", type=int, default=12)
    args = parser.parse_args()
    files = [args.input] if args.input.is_file() else sorted(
        p for p in args.input.iterdir() if p.suffix.lower() in 支持格式)
    if not files:
        raise SystemExit(f"没有找到支持的图像：{args.input}")
    args.output.mkdir(parents=True, exist_ok=True)
    rows = []
    for path in files:
        raw = 读取灰度(path)
        annotated, part_info, candidates = 检测数组(raw, box_size=args.box_size, edge_margin=args.edge_margin)
        display = 显示映射(raw)
        if not candidates:
            result_path = args.output / f"{path.stem}_未检出.png"
            Image.fromarray(display, mode="L").save(result_path)
            print(f"{path.name}: 未检出")
            rows.append({"文件": path.name, "亮斑数量": 0, "状态": "未检出"})
            continue
        result_path = args.output / f"{path.stem}_亮斑.png"
        annotated.save(result_path)
        print(f"{path.name}: 检出 {len(candidates)} 处")
        rows.append({"文件": path.name, "亮斑数量": len(candidates),
                     "亮斑中心": "; ".join(f"{c.center[0]}:{c.center[1]}" for c in candidates),
                     "状态": "检出"})
    with (args.output / "检测结果.csv").open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=("文件", "亮斑数量", "亮斑中心", "状态"))
        writer.writeheader()
        writer.writerows(rows)


if __name__ == "__main__":
    main()
