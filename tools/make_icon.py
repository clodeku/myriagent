"""產生 app 圖示 src/Myriagent/app.ico（16～256 px 多尺寸）與網頁的 favicon src/Myriagent/Web/favicon.ico。

用法：python -I tools/make_icon.py
需要 Pillow。圖案：圓角方形、珊瑚橘到紫到青綠的斜向漸層（代表 Claude 與 Codex 兩種代理），
中間是白色的「〈 〉」對話括號與一顆小星芒。以 4 倍超取樣繪製再縮小，邊緣平滑。
輸出固定（不依賴亂數與系統字型），重跑會得到同一個檔案。
favicon 是同一張圖的小尺寸子集（分頁、書籤用；桌面與瀏覽器版共用 Web 資料夾），每個尺寸與 app.ico 的同尺寸逐位元組相同
（tests/Myriagent.Tests/UI/WebFaviconTests.cs 把關）。
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Myriagent", "app.ico")
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
FAVICON_OUT = os.path.join(ROOT, "src", "Myriagent", "Web", "favicon.ico")
FAVICON_SIZES = [16, 24, 32, 48, 64]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def gradient(size):
    stops = [(0.0, (240, 128, 92)), (0.52, (156, 102, 240)), (1.0, (25, 195, 166))]
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x * 0.55 + y * 0.45) / (size - 1)
            for i in range(len(stops) - 1):
                t0, c0 = stops[i]
                t1, c1 = stops[i + 1]
                if t <= t1:
                    px[x, y] = lerp(c0, c1, (t - t0) / (t1 - t0)) + (255,)
                    break
    return img


def render(size):
    s = size * 4
    base = gradient(s)
    mask = Image.new("L", (s, s), 0)
    radius = int(s * 0.23)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, s - 1, s - 1), radius=radius, fill=255)

    # 上方淡淡的高光
    shine = Image.new("RGBA", (s, s), (255, 255, 255, 0))
    ImageDraw.Draw(shine).ellipse((-s * 0.3, -s * 0.95, s * 1.3, s * 0.42), fill=(255, 255, 255, 38))
    base = Image.alpha_composite(base, shine)

    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    w = max(4, int(s * 0.085))
    cy = s * 0.5
    # 〈
    d.line([(s * 0.40, s * 0.30), (s * 0.24, cy), (s * 0.40, s * 0.70)], fill=(255, 255, 255, 255), width=w, joint="curve")
    # 〉
    d.line([(s * 0.60, s * 0.30), (s * 0.76, cy), (s * 0.60, s * 0.70)], fill=(255, 255, 255, 255), width=w, joint="curve")
    for x, y in [(s * 0.40, s * 0.30), (s * 0.40, s * 0.70), (s * 0.60, s * 0.30), (s * 0.60, s * 0.70), (s * 0.24, cy), (s * 0.76, cy)]:
        d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=(255, 255, 255, 255))
    # 中間的星芒
    r = s * 0.085
    d.polygon([(s * 0.5, cy - r * 1.6), (s * 0.5 + r * 0.42, cy - r * 0.42), (s * 0.5 + r * 1.6, cy), (s * 0.5 + r * 0.42, cy + r * 0.42),
               (s * 0.5, cy + r * 1.6), (s * 0.5 - r * 0.42, cy + r * 0.42), (s * 0.5 - r * 1.6, cy), (s * 0.5 - r * 0.42, cy - r * 0.42)],
              fill=(255, 255, 255, 255))

    shadow = glyph.copy().filter(ImageFilter.GaussianBlur(s * 0.02))
    shadow_alpha = shadow.split()[3].point(lambda a: int(a * 0.35))
    shadow = Image.merge("RGBA", (Image.new("L", (s, s), 40),) * 3 + (shadow_alpha,))
    composed = Image.alpha_composite(base, shadow)
    composed = Image.alpha_composite(composed, glyph)
    composed.putalpha(mask)
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


if __name__ == "__main__":
    sys.exit(main())
