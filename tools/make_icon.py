"""產生 app 圖示 src/Myriagent/app.ico（16～256 px 多尺寸）與網頁的 favicon src/Myriagent/Web/favicon.ico。

用法：python tools/make_icon.py [預覽.png]（Pillow 裝在使用者的 site-packages 時不能加 -I）
需要 Pillow。圖案是品牌「萬象星群」（2026-10-31 使用者選定，同網頁標題列 Web/icons.js 的 BRAND_SVG）：
深藍的圓角方形上，光點排成缺口朝右的 C（萬個代理同時工作），中心是 AI 星芒；光點從 C 的下端到上端越來越亮
（同標題列靜止時亮光停的位置）。越小的尺寸光點越少越大（16～24 px 一圈、32～48 px 兩圈、64 px 起三圈），縮小才不會糊成一圈。
以 4 倍超取樣繪製再縮小，邊緣平滑。輸出固定（不依賴亂數與系統字型），重跑會得到同一個檔案。
favicon 是同一張圖的小尺寸子集（分頁、書籤用；桌面與瀏覽器版共用 Web 資料夾），每個尺寸與 app.ico 的同尺寸逐位元組相同
（tests/Myriagent.Tests/UI/WebFaviconTests.cs 把關）。
"""
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Myriagent", "app.ico")
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
FAVICON_OUT = os.path.join(ROOT, "src", "Myriagent", "Web", "favicon.ico")
FAVICON_SIZES = [16, 24, 32, 48, 64]

# 以 24 為邊長的座標：[(圈的半徑, 間隔數, 光點半徑, 顏色)]，C 從 40° 順時針排到 320°（缺口朝右；16～24 px 缺口開到 ±55° 才看得出是 C）
RINGS_SMALL = [(7.6, 7, 1.45, (56, 189, 248))]
RINGS_MID = [(8.4, 11, 1.0, (59, 130, 246)), (5.9, 7, 0.9, (56, 200, 245))]
RINGS_LARGE = [(8.9, 18, 0.7, (59, 130, 246)), (7.1, 14, 0.82, (56, 189, 248)), (5.3, 10, 0.92, (125, 249, 255))]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def tile(s):
    """深藍的斜向漸層，中心一團淡淡的藍光"""
    img = Image.new("RGBA", (s, s))
    px = img.load()
    top, bottom = (20, 44, 98), (5, 11, 32)
    c = (s - 1) / 2
    for y in range(s):
        for x in range(s):
            base = lerp(top, bottom, (x * 0.4 + y * 0.6) / (s - 1))
            d = math.hypot(x - c, y - c) / (s * 0.5)
            glow = max(0.0, 1 - d / 0.62) ** 2 * 0.32
            px[x, y] = lerp(base, (40, 120, 230), glow) + (255,)
    return img


def spark_points(cx, cy, r, n=96):
    # 四角星芒（類似星形線，邊往內彎）
    pts = []
    for i in range(n):
        t = 2 * math.pi * i / n
        pts.append((cx + r * math.copysign(abs(math.cos(t)) ** 2.6, math.cos(t)),
                    cy + r * math.copysign(abs(math.sin(t)) ** 2.6, math.sin(t))))
    return pts


def render(size):
    s = size * 4
    k = s / 24
    rings = RINGS_SMALL if size <= 24 else RINGS_MID if size <= 48 else RINGS_LARGE
    spark_r = (4.2 if size <= 24 else 3.6) * k

    base = tile(s)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.23), fill=255)

    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    for r, n, dot, color in rings:
        for i in range(n + 1):
            t = i / n
            gap = 55 if size <= 24 else 40
            a = math.radians(gap + (360 - 2 * gap) * t)
            x, y = s / 2 + r * k * math.cos(a), s / 2 + r * k * math.sin(a)
            # C 的下端暗、上端亮（小尺寸只差一點，保住對比）
            alpha = int(255 * ((0.75 + 0.25 * t) if size <= 24 else (0.5 + 0.5 * t)))
            rr = dot * k
            d.ellipse((x - rr, y - rr, x + rr, y + rr), fill=color + (alpha,))

    # 星芒：白→青→藍的放射漸層，以星形為遮罩
    star_mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(star_mask).polygon(spark_points(s / 2, s / 2, spark_r), fill=255)
    star = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    spx = star.load()
    c = (s - 1) / 2
    for y in range(int(c - spark_r) - 1, int(c + spark_r) + 2):
        for x in range(int(c - spark_r) - 1, int(c + spark_r) + 2):
            t = min(1.0, math.hypot(x - c, y - c) / spark_r)
            col = lerp((240, 254, 255), (125, 249, 255), t / 0.45) if t < 0.45 else lerp((125, 249, 255), (59, 130, 246), (t - 0.45) / 0.55)
            spx[x, y] = col + (255,)
    star.putalpha(star_mask)
    glyph = Image.alpha_composite(glyph, star)

    # 光暈：模糊的副本疊在下面
    glow = glyph.filter(ImageFilter.GaussianBlur(s * (0.035 if size > 24 else 0.02)))
    glow.putalpha(glow.split()[3].point(lambda v: int(v * 0.8)))
    composed = Image.alpha_composite(base, glow)
    composed = Image.alpha_composite(composed, glyph)
    composed.putalpha(ImageChops.multiply(composed.split()[3], mask))
    return composed.resize((size, size), Image.LANCZOS)


def save_ico(path, images):
    # 每個尺寸各自繪製後交給 append_images（不讓 Pillow 從最大的那張縮）
    sizes = sorted(images)
    images[sizes[-1]].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=[images[s] for s in sizes[:-1]])
    print("已產生", path, os.path.getsize(path), "bytes")


def main():
    images = {size: render(size) for size in SIZES}
    save_ico(OUT, images)
    save_ico(FAVICON_OUT, {size: images[size] for size in FAVICON_SIZES})
    if len(sys.argv) > 1:
        # 預覽：各尺寸並排存成一張 PNG（不進 repo）
        sheet = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES) + 10, 276), (245, 247, 250, 255))
        x = 10
        for size in SIZES:
            sheet.alpha_composite(images[size], (x, 10))
            x += size + 10
        sheet.save(sys.argv[1])


if __name__ == "__main__":
    sys.exit(main())
