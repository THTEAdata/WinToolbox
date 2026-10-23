"""生成 WinToolbox 应用图标（assets/app.ico）。

用法：python scripts/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def lerp(a: int, b: int, t: float) -> int:
    return int(round(a + (b - a) * t))


def main() -> None:
    # 对角渐变：#4C8DFF -> #22C3A6
    top, bottom = (76, 141, 255), (34, 195, 166)
    grad = Image.new("RGB", (SIZE, SIZE))
    px = grad.load()
    for y in range(SIZE):
        for x in range(SIZE):
            t = (x + y) / (2 * (SIZE - 1))
            px[x, y] = (lerp(top[0], bottom[0], t), lerp(top[1], bottom[1], t), lerp(top[2], bottom[2], t))

    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(grad, (0, 0), rounded_mask(SIZE, 232))

    draw = ImageDraw.Draw(icon)
    white = (255, 255, 255, 255)

    # 箱体
    body = (200, 430, 824, 790)
    draw.rounded_rectangle(body, radius=54, fill=white)
    # 箱盖分割线
    draw.rounded_rectangle((200, 520, 824, 560), radius=8, fill=None)
    draw.line((200, 545, 824, 545), fill=(76, 141, 255, 120), width=14)
    # 提手
    draw.rounded_rectangle((392, 300, 632, 470), radius=70, fill=None, outline=white, width=54)
    # 锁扣
    draw.rounded_rectangle((470, 500, 554, 592), radius=20, fill=(76, 141, 255, 255))
    # 螺丝孔装饰
    for cx in (290, 734):
        draw.ellipse((cx - 26, 620 - 26, cx + 26, 620 + 26), fill=(76, 141, 255, 90))

    out = Path(__file__).resolve().parent.parent / "assets" / "app.ico"
    out.parent.mkdir(parents=True, exist_ok=True)
    icon.save(out, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"icon written: {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
