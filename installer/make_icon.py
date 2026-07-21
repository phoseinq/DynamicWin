"""Generate installer/halo.ico — a dark-glass rounded square with a glowing halo ring.
Supersampled 4x then downscaled to each icon size. ponytail: one script, stdlib+PIL only."""
from PIL import Image, ImageDraw, ImageFilter

S = 512                       # supersample canvas
R = int(S * 0.225)            # corner radius
cx = cy = S / 2

# --- dark glass rounded-square background (vertical gradient) ---
grad = Image.new("RGB", (1, S))
top, bot = (28, 30, 38), (12, 13, 17)
for y in range(S):
    t = y / (S - 1)
    grad.putpixel((0, y), tuple(round(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
bg = grad.resize((S, S))

# manual rounded-rect mask — PIL's rounded_rectangle segfaults on Python 3.14 (draw_corners AV)
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rectangle([R, 0, S - 1 - R, S - 1], fill=255)
md.rectangle([0, R, S - 1, S - 1 - R], fill=255)
d2 = 2 * R
for ex, ey in [(0, 0), (S - 1 - d2, 0), (0, S - 1 - d2), (S - 1 - d2, S - 1 - d2)]:
    md.ellipse([ex, ey, ex + d2, ey + d2], fill=255)
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
img.paste(bg, (0, 0), mask)

# --- the halo ring: crisp ring + a soft outer glow ---
ro = int(S * 0.30)            # ring outer radius
w = int(S * 0.070)            # stroke width
ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(ring).ellipse([cx - ro, cy - ro, cx + ro, cy + ro],
                             outline=(214, 232, 255, 255), width=w)

glow = ring.filter(ImageFilter.GaussianBlur(S * 0.045))
img.alpha_composite(Image.composite(  # clip glow to the card so it doesn't bleed past corners
    glow, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))
img.alpha_composite(ring)

# subtle top glass highlight
hi = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(hi).ellipse([S * 0.12, -S * 0.42, S * 0.88, S * 0.30], fill=(255, 255, 255, 26))
img.alpha_composite(Image.composite(hi, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

sizes = [256, 128, 64, 48, 32, 16]
base = img.resize((256, 256), Image.LANCZOS)
base.save("installer/halo.ico", sizes=[(s, s) for s in sizes])
base.save("installer/halo.png")
print("wrote installer/halo.ico + halo.png")
