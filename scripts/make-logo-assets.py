# Modulix logo varlıklarını kaynak görselden üretir (docs/design-system/README.md -> Marka).
# Kullanım (depo kökünden): python3 scripts/make-logo-assets.py [çıktı dizini] [kaynak png]
# Gerekenler: pillow, numpy. Beyaz zemin şeffaflaştırılır; koyu tema sürümleri yazıyı/işareti açık tona çeker.
import os
import sys
from PIL import Image
import numpy as np

SRC = sys.argv[2] if len(sys.argv) > 2 else 'docs/design-system/assets/modulix-logo-source.png'
OUT = sys.argv[1] if len(sys.argv) > 1 else 'src/Modulix.Web/wwwroot/img'

a = np.asarray(Image.open(SRC).convert('RGB')).astype(np.float64) / 255.0

# Beyaz zeminden "renk -> alfa": kenarlar yumuşak kalır, JPEG gürültüsü (alfa < 0.06) atılır.
alpha = np.clip(1.0 - a.min(axis=2), 0, 1)
alpha = np.where(alpha < 0.06, 0.0, alpha)
safe = np.maximum(alpha, 1e-6)[..., None]
rgb = np.clip((a - (1.0 - alpha[..., None])) / safe, 0, 1)
rgba = np.dstack([rgb, alpha])

def crop(box, pad=0):
    x0, y0, x1, y1 = box
    return rgba[y0 - pad:y1 + 1 + pad, x0 - pad:x1 + 1 + pad].copy()

def save(arr, name, max_w=None, max_h=None):
    img = Image.fromarray((arr * 255 + 0.5).astype(np.uint8), 'RGBA')
    if max_w and img.width > max_w:
        img = img.resize((max_w, round(img.height * max_w / img.width)), Image.LANCZOS)
    if max_h and img.height > max_h:
        img = img.resize((round(img.width * max_h / img.height), max_h), Image.LANCZOS)
    img.save(f'{OUT}/{name}', optimize=True)
    print(name, img.size)
    return img

# Bölgeler (özgün 2172x724 görselde)
MARK = (158, 163, 693, 591)
FULL = (158, 163, 2018, 591)         # işaret + yazı + slogan
WORD = (158, 163, 2018, 452 + 0)     # işaret + yazı (slogansız); yazı alt sınırı 452

def lum(c): return 0.2126 * c[..., 0] + 0.7152 * c[..., 1] + 0.0722 * c[..., 2]

def to_dark(arr, mark_cols):
    """Koyu tema: lacivert yazı/slogan açık tona, işaretin lacivert kısmı açık çelik maviye çekilir; turkuaz korunur."""
    out = arr.copy()
    c = out[..., :3]
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    teal = (g > 0.45) & (r < 0.35)                      # turkuaz ve parlak mavi tonlar
    dark = (~teal) & (lum(c) < 0.42)
    col = np.arange(arr.shape[1])[None, :, None]
    is_mark = col < mark_cols
    # Yazı: neredeyse beyaz; slogan (gri-mavi) açık gri-mavi
    text_light = np.array([0.93, 0.95, 0.98])
    mark_light = np.array([0.43, 0.61, 0.84])           # #6E9BD6
    t = np.clip((0.42 - lum(c)) / 0.42, 0, 1)[..., None]
    target = np.where(is_mark, mark_light, text_light)
    mixed = c * (1 - t) + target * t
    out[..., :3] = np.where(dark[..., None], mixed, c)
    # Slogan (orta ton gri-mavi): açık gri-mavi
    mid = (~teal) & (lum(c) >= 0.42) & (lum(c) < 0.62) & (~is_mark[..., 0])
    out[..., :3] = np.where(mid[..., None], np.array([0.72, 0.78, 0.86]), out[..., :3])
    return out

OUT_W = 960
full = crop(FULL, 6)
# Slogansız sürüm: yazı bloğu (y 198-452) işaretin (y 163-591) dikey ortasına taşınır.
word = crop(FULL, 6)
TX0 = 740 - FULL[0] + 6                      # yazı bölgesinin başladığı sütun (kırpılmış koordinat)
tb0, tb1 = 198 - FULL[1] + 6, 452 - FULL[1] + 6
mark_h = MARK[3] - MARK[1]
shift = ((mark_h - (452 - 198)) // 2) - (198 - 163)
text = word[tb0:tb1 + 1, TX0:].copy()
word[:, TX0:] = 0
word[tb0 + shift:tb1 + 1 + shift, TX0:] = text
mark = crop(MARK, 6)
mark_cols = (MARK[2] - MARK[0]) + 12

save(full, 'modulix-logo.png', max_w=OUT_W)
save(to_dark(full, mark_cols), 'modulix-logo-dark.png', max_w=OUT_W)
save(word, 'modulix-logo-wordmark.png', max_w=OUT_W)
save(to_dark(word, mark_cols), 'modulix-logo-wordmark-dark.png', max_w=OUT_W)
mk = save(mark, 'modulix-mark.png', max_h=256)
mkd = save(to_dark(mark, mark_cols), 'modulix-mark-dark.png', max_h=256)

# Favicon / ikon: kare tuval, işaret ortada
def square(arr, size, pad_ratio):
    img = Image.fromarray((arr * 255 + 0.5).astype(np.uint8), 'RGBA')
    inner = int(size * (1 - 2 * pad_ratio))
    img.thumbnail((inner, inner), Image.LANCZOS)
    canvas = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    canvas.paste(img, ((size - img.width) // 2, (size - img.height) // 2), img)
    return canvas

ROOT = os.path.dirname(os.path.abspath(OUT))   # favicon ve ikon wwwroot/ altındadır
square(mark, 64, 0.04).save(f'{ROOT}/favicon.png', optimize=True)
# icon-192: beyaz zeminli (PWA/ana ekran), işaret ortada
ic = square(mark, 192, 0.14)
bg = Image.new('RGBA', (192, 192), (255, 255, 255, 255))
bg.alpha_composite(ic)
bg.convert('RGB').save(f'{ROOT}/icon-192.png', optimize=True)
print('favicon, icon-192 ok')
