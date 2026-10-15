# Генерация иконки NOVA TUNE (многоразмерный .ico и PNG для README).
# Запуск: python3 build/make_icon.py <выходная папка>
import sys, math
from PIL import Image, ImageDraw, ImageFilter

def render(size):
    s = size * 4  # суперсэмплинг для гладких краёв
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # Фон: скруглённый квадрат с диагональным градиентом синий → фиолетовый.
    grad = Image.new("RGBA", (s, s))
    px = grad.load()
    c1, c2 = (64, 112, 255), (128, 96, 255)
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2 * s)
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (s, s), 0)
    r = int(s * 0.23)
    m = int(s * 0.04)
    ImageDraw.Draw(mask).rounded_rectangle([m, m, s - m, s - m], radius=r, fill=255)
    img.paste(grad, (0, 0), mask)
    # Стеклянный блик сверху.
    gloss = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(gloss).ellipse([-s * 0.2, -s * 0.75, s * 1.2, s * 0.42], fill=(255, 255, 255, 46))
    gloss_mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(gloss_mask).rounded_rectangle([m, m, s - m, s - m], radius=r, fill=255)
    img.paste(gloss, (0, 0), Image.composite(gloss.split()[3], Image.new("L", (s, s), 0), gloss_mask))
    # Тонкая светлая обводка.
    ImageDraw.Draw(img).rounded_rectangle([m, m, s - m, s - m], radius=r, outline=(255, 255, 255, 90), width=max(2, s // 64))
    # Знак: буква N из скруглённых линий.
    d = ImageDraw.Draw(img)
    w = max(4, int(s * 0.085))
    x0, x1 = s * 0.31, s * 0.69
    y0, y1 = s * 0.29, s * 0.71
    for a, b in [((x0, y1), (x0, y0)), ((x0, y0), (x1, y1)), ((x1, y1), (x1, y0))]:
        d.line([a, b], fill=(255, 255, 255, 245), width=w)
    for p in [(x0, y0), (x0, y1), (x1, y0), (x1, y1)]:
        d.ellipse([p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2], fill=(255, 255, 255, 245))
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
big = render(256)
big.save(f"{out}/novatune.png")
big.save(f"{out}/novatune.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
render(64).save(f"{out}/novatune-64.png")
print("ok")
