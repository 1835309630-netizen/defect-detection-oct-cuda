import sys; sys.path.insert(0,'.')
from pathlib import Path
import numpy as np, cv2
import defect_algorithm as da

原图目录 = Path(r'C:\Users\lenovo\Desktop\dcs\_现场调试_原图')
规则 = da.检测规则()
真值 = [1,0,0,0,1,0,1,1,1,1]

print('图号  面积   圆度   亮峰   亮均   峰/均   结果')
for idx, path in enumerate(sorted(原图目录.glob('*.tif'))):
    raw = da.读取灰度(path)
    display = da.显示映射(raw)
    part, safe = da.零件掩膜(display, 12)
    score, threshold = da.亮斑分数(display, part, safe, 规则)
    binary = np.uint8((score >= threshold) & (safe>0))*255
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(5,5)))
    count, labels, _, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    for label in range(1,count):
        ys,xs = np.where(labels==label)
        area = len(ys)
        if area < 规则.最小面积: continue
        circ = da.计算圆度(np.uint8(labels==label))
        if circ < 规则.最低圆度: continue
        y0,y1,x0,x1 = ys.min(),ys.max()+1,xs.min(),xs.max()+1
        aspect = max(x1-x0,y1-y0)/max(1,min(x1-x0,y1-y0))
        if aspect > 规则.最大长宽比: continue
        comp = score[y0:y1,x0:x1][ys-y0,xs-x0]
        peak, avg = float(comp.max()), float(comp.mean())
        ratio = peak/avg if avg>0 else 0
        print(f'第{idx+1}张  {area:<6}{circ:<6.2f}{peak:<7.3f}{avg:<7.3f}{ratio:<7.2f}  真值{真值[idx]}')
