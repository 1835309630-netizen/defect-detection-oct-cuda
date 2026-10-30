"""V2 生产端的单帧预处理与缺陷检测入口。标准输出只返回一条 JSON。"""

from __future__ import annotations

import argparse
import json
import sys
import traceback
from datetime import datetime
from pathlib import Path

import numpy as np
from PIL import Image

from defect_algorithm import 检测数组, 默认模板目录, 生成模板


# 无论由 Python 直接运行还是由 PyInstaller 封装，管道输出统一使用 UTF-8。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


def 读取单通道(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        raw = np.asarray(image)
    if raw.ndim != 2:
        raise ValueError(f"{path.name} 不是单通道灰度图")
    return raw.astype(np.float32, copy=False)


def 预处理(
    raw: np.ndarray,
    background: np.ndarray | None,
    flat: np.ndarray | None,
) -> tuple[np.ndarray, str]:
    for name, image in (("背景", background), ("平场", flat)):
        if image is not None and image.shape != raw.shape:
            raise ValueError(f"图像尺寸不一致：采集帧={raw.shape[::-1]}，{name}={image.shape[::-1]}")
    if not np.isfinite(raw).all() or any(
        image is not None and not np.isfinite(image).all() for image in (background, flat)
    ):
        raise ValueError("采集帧、背景或平场中包含 NaN/Inf")

    signal = np.maximum(raw - background, 0.0) if background is not None else raw.copy()
    if flat is None:
        mode = "背景扣除" if background is not None else "原始帧直检"
        # 算法始终保留 float32；不在预处理结束时提前取整回 Mono10。
        return signal.astype(np.float32, copy=False), mode

    # 同时选择背景和平场时采用 (原图-背景)/(平场-背景)；只选平场时采用 原图/平场。
    denominator = flat - background if background is not None else flat
    epsilon = max(float(np.percentile(np.abs(denominator), 99)) * 1e-6, 1e-6)
    valid = denominator > epsilon
    valid_ratio = float(np.mean(valid))
    if valid_ratio < 0.95:
        raise ValueError(f"平场分母仅 {valid_ratio:.1%} 像素有效，请检查标定图是否选反或曝光不匹配")

    scale = float(np.median(denominator[valid]))
    corrected = np.zeros_like(raw, dtype=np.float32)
    corrected[valid] = signal[valid] * scale / denominator[valid]
    mode = "背景+平场校正" if background is not None else "平场校正"
    return corrected.astype(np.float32, copy=False), mode


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raw", type=Path)
    parser.add_argument("--background", type=Path)
    parser.add_argument("--flat", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--template-dir", type=Path, default=默认模板目录())
    parser.add_argument("--build-template", nargs="+", type=Path, metavar="图片")
    parser.add_argument("--result-root", type=Path, help="结果帧根目录，按日期/零件类型分类")
    parser.add_argument("--captured-at", type=datetime.fromisoformat, help="采集时间，ISO 8601格式")
    args = parser.parse_args()

    if args.build_template:
        if args.raw or args.background or args.flat:
            parser.error("模板生成与单帧检测参数不能混用")
        print(json.dumps(生成模板(args.build_template, args.output), ensure_ascii=False))
        return
    if args.raw is None:
        parser.error("检测必须指定 --raw")

    args.output.mkdir(parents=True, exist_ok=True)
    corrected_path = args.output / "02_校正帧.tif"
    result_path = args.output / "03_检测结果.tif"

    raw = 读取单通道(args.raw)
    background = 读取单通道(args.background) if args.background else None
    flat = 读取单通道(args.flat) if args.flat else None
    corrected, preprocess_mode = 预处理(raw, background, flat)
    if background is None and flat is None:
        corrected_result_path = args.raw.resolve()
    else:
        # 校正帧是派生科学数据而非相机原始帧，使用 32 位浮点 TIFF 保留小数精度。
        # 原始帧仍由生产端按相机 Mono10 数据原样保存到 16 位 TIFF 容器。
        Image.fromarray(corrected).save(corrected_path, format="TIFF", compression="raw")
        corrected_result_path = corrected_path.resolve()

    annotated, part, candidates = 检测数组(
        corrected,
        args.template_dir,
    )
    if args.result_root:
        captured_at = args.captured_at or datetime.now()
        # 类型来自模板文件名；仍过滤Windows路径字符，确保不会越出结果根目录。
        kind = "".join("_" if char in '<>:"/\\|?*' or ord(char) < 32 else char for char in part.part_type).strip(" .") or "未分类"
        if kind.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}:
            kind = "_" + kind
        directory = args.result_root / captured_at.strftime("%Y-%m-%d") / kind
        directory.mkdir(parents=True, exist_ok=True)
        stem = captured_at.strftime("%Y%m%d_%H%M%S_%f") + "_" + kind
        index = 1
        while True:
            result_path = directory / (stem + (f"_{index}" if index > 1 else "") + ".tif")
            try:
                handle = result_path.open("xb")  # 同时刻重试也不能覆盖已有结果。
                break
            except FileExistsError:
                index += 1
        with handle:
            annotated.save(handle, format="TIFF", compression="raw")
    else:
        annotated.save(result_path, format="TIFF", compression="raw")
    status = part.status if not part.accepted else (
        f"检出缺陷 {len(candidates)} 处" if candidates else "未检出缺陷"
    )
    print(json.dumps({
        "Ok": True,
        "Status": status,
        "PartType": part.part_type,
        "DefectCount": len(candidates),
        "CorrectedPath": str(corrected_result_path),
        "ResultPath": str(result_path.resolve()),
        "PreprocessMode": preprocess_mode,
        "Error": None,
        "DefectCenters": [list(candidate.center) for candidate in candidates],
        "TemplateScore": part.template_score,
        "TemplateName": part.template_name,
        "RecognitionStatus": part.status,
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        raise SystemExit(1) from error
