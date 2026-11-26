"""逐張比對兩組截圖：perf-probe.mjs 的 MYRIAGENT_PERF_SIDEBAR_SHOTS 改前改後各拍一組。

列出每張是否逐像素相同、不同的像素數與範圍，以及「彩色像素」數（文字的次像素反鋸齒〔ClearType〕在字邊留下彩色：
這個數字大幅變少就是字變成灰階反鋸齒——content-visibility、contain: paint／layout、isolation 讓元素變成獨立的堆疊環境時就會這樣）。
另比對兩邊的 geometry.json（捲動的位置與長度、列數）。全部相同時結束碼 0。

用法：python -X utf8 shot-compare.py <改後的資料夾> <改前的資料夾>
"""
import json
import os
import sys

from PIL import Image, ImageChops

a_dir, b_dir = sys.argv[1], sys.argv[2]


def colorful(img):
    return sum(1 for r, g, b in img.get_flattened_data() if max(r, g, b) - min(r, g, b) > 30 and max(r, g, b) > 60)


same = True
for f in sorted(os.listdir(a_dir)):
    if not f.endswith('.png'):
        continue
    a = Image.open(os.path.join(a_dir, f)).convert('RGB')
    b = Image.open(os.path.join(b_dir, f)).convert('RGB')
    if a.size != b.size:
        print(f'{f:24s} 大小不同 {a.size} {b.size}')
        same = False
        continue
    d = ImageChops.difference(a, b)
    n = sum(1 for p in d.get_flattened_data() if p != (0, 0, 0))
    same = same and n == 0
    print(f"{f:24s} {'相同' if n == 0 else '不同'}  差異像素 {n:6d}  彩色像素 {colorful(a):6d} / {colorful(b):6d}  範圍 {d.getbbox()}")
ga = os.path.join(a_dir, 'geometry.json')
gb = os.path.join(b_dir, 'geometry.json')
if os.path.exists(ga) and os.path.exists(gb):
    strip = lambda g: [x for x in json.load(open(g, encoding='utf-8')) if x.get('name') != 'jump']
    geo = strip(ga) == strip(gb)
    same = same and geo
    print('捲動的位置與長度：' + ('相同' if geo else '不同'))
sys.exit(0 if same else 1)
