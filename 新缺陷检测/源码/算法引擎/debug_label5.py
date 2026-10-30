"""在第5张图上标出所有面积>300的连通域编号。"""
import sys
from pathlib import Path
import numpy as np
import cv2
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).parent))
import defect_algorithm as da

path = Path(r"C:\Users\lenovo\Desktop\dcs\_现场调试_原图\采集_20260913_123216_991_F1271.tif")
规则 = da.检测规则()

raw = da.读取灰度(path)
display = da.显示映射(raw)
part, safe = da.零件掩膜(display, 12)
score, threshold = da.亮斑分数(display, part, safe, 规则)
binary = np.uint8((score >= threshold) & (safe > 0)) * 255
binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5,5)))
count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)

canvas = Image.fromarray(display).convert("RGB")
draw = ImageDraw.Draw(canvas)

for label in range(1, count):
    ys, xs = np.where(labels == label)
    area = len(ys)
    if area < 300: continue
    x0, x1 = xs.min(), xs.max()+1
    y0, y1 = ys.min(), ys.max()+1
    circ = da.计算圆度(np.uint8(labels==label))
    # 画框：通过的用绿，被卡的用红
    passed = area >= 规则.最小面积 and circ >= 规则.最低圆度
    color = (0,255,0) if passed else (255,0,0)
    draw.rectangle([x0,y0,x1,y1], outline=color, width=3)
    draw.text((x0, y0-15), f"#{label} a={area} c={circ:.2f}", fill=(255,255,0))

out = Path(r"C:\Users\lenovo\Desktop\dcs\_现场调试_结果图\第八次\第5张候选标注.png")
canvas.save(out)
print(f"saved: {out}")
