"""调试：只打印面积>300的候选，看被哪条规则卡了。"""
import sys
from pathlib import Path
import numpy as np
import cv2

sys.path.insert(0, str(Path(__file__).parent))
import defect_algorithm as da

原图目录 = Path(r"C:\Users\lenovo\Desktop\dcs\_现场调试_原图")
规则 = da.检测规则()
edge_margin = 12
真值 = [1,0,0,0,1,0,1,1,1,1]

for idx, path in enumerate(sorted(原图目录.glob("*.tif"))):
    raw = da.读取灰度(path)
    display = da.显示映射(raw)
    part, safe = da.零件掩膜(display, edge_margin)
    score, threshold = da.亮斑分数(display, part, safe, 规则)
    binary = np.uint8((score >= threshold) & (safe > 0)) * 255
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5,5)))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)

    print(f"\n=== 第{idx+1}张: {path.name[-20:]}  真值{真值[idx]}  阈值={threshold:.4f} ===")
    print(f"{'#':<5}{'面积':<7}{'圆度':<7}{'长宽比':<7}{'亮峰':<8}{'亮均':<8}{'暗纹':<7}结果")

    for label in range(1, count):
        ys, xs = np.where(labels == label)
        if len(ys) == 0: continue
        area = len(ys)
        if area < 300: continue  # 只看大块
        y0,y1,x0,x1 = ys.min(), ys.max()+1, xs.min(), xs.max()+1
        circ = da.计算圆度(np.uint8(labels==label))
        aspect = max(x1-x0, y1-y0) / max(1, min(x1-x0, y1-y0))
        gray = display[ys, xs].astype(np.float32)
        gmean = gray.mean()
        dark = np.mean(gray < gmean*0.65)
        comp = score[y0:y1, x0:x1][ys-y0, xs-x0]
        peak = comp.max(); avg = comp.mean()

        reasons = []
        if area < 规则.最小面积: reasons.append(f"面积<{规则.最小面积}")
        if circ < 规则.最低圆度: reasons.append(f"圆度<{规则.最低圆度}")
        if aspect > 规则.最大长宽比: reasons.append(f"长宽比>{规则.最大长宽比}")
        if dark > 规则.最大内部暗纹比例: reasons.append(f"暗纹>{规则.最大内部暗纹比例}")
        status = "✓通过" if not reasons else "✗ "+",".join(reasons)
        print(f"#{label:<4}{area:<7}{circ:<7.2f}{aspect:<7.2f}{peak:<8.3f}{avg:<8.3f}{dark:<7.2f}{status}")
