import sys; sys.path.insert(0,'.')
from pathlib import Path
import numpy as np, cv2
import defect_algorithm as da

原图目录 = Path(r'C:\Users\lenovo\Desktop\dcs\_现场调试_原图')
规则 = da.检测规则()

all_avg = []
for path in sorted(原图目录.glob('*.tif')):
    raw = da.读取灰度(path)
    display = da.显示映射(raw)
    part, safe = da.零件掩膜(display, 12)
    score, threshold = da.亮斑分数(display, part, safe, 规则)
    binary = np.uint8((score >= threshold) & (safe>0))*255
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(5,5)))
    count, labels, _, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    for label in range(1,count):
        ys,xs = np.where(labels==label)
        if len(ys)<300: continue
        y0,y1,x0,x1 = ys.min(),ys.max()+1,xs.min(),xs.max()+1
        comp = score[y0:y1,x0:x1][ys-y0,xs-x0]
        all_avg.append((float(comp.mean()), len(ys), path.name[-18:]))

all_avg.sort(reverse=True)
print('亮均   面积   来自图')
for avg, area, name in all_avg:
    print(f'{avg:.3f}  {area:<6}{name}')
print(f'\n范围: {min(a[0] for a in all_avg):.3f} ~ {max(a[0] for a in all_avg):.3f}')
