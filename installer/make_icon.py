"""Generate installer/halo.ico — a dark-glass rounded square with a glowing halo ring.
Supersampled 4x then downscaled to each icon size. ponytail: one script, stdlib+PIL only.

The .ico is written here rather than by Pillow's own writer, which emits every frame as a
PNG-compressed entry. Windows accepts that for 256x256 and effectively does not for the small sizes:
a fully PNG icon left the signed setup .exe with no icon at all in Explorer and in browser download
lists. 16..128 are therefore classic BMP/DIB entries and only 256 stays PNG, which is what keeps the
file from being a megabyte.
"""
import struct
from io import BytesIO

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

def dib(im):
    """One 32bpp BMP icon image: BITMAPINFOHEADER with a doubled height, bottom-up BGRA rows, then the
    1bpp AND mask. The mask is all zeros — the alpha channel is what shapes the icon — but the header
    must still claim it or the bottom half of the icon is read as transparent."""
    w, h = im.size
    # one tobytes() and then plain slicing. Per-pixel access, ImageOps.flip and Image.split all
    # segfault on the Pillow/CPython pair here — same class of defect as the rounded_rectangle note
    # above — and this path touches none of them, so it does not matter which Python runs the script.
    raw = im.tobytes()                                  # RGBA, top-down
    stride = w * 4
    xor = bytearray(b''.join(raw[y * stride:(y + 1) * stride] for y in range(h - 1, -1, -1)))
    xor[0::4], xor[2::4] = xor[2::4], xor[0::4]         # RGBA -> BGRA
    xor = bytes(xor)
    mask = bytes(((w + 31) // 32) * 4 * h)
    head = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return head + xor + mask


def write_ico(path, source, sizes):
    frames = []
    for s in sizes:
        # resized from the 512 supersample every time, not from the 256 frame: 16 and 32 are the sizes
        # a download list actually shows, and a second downscale is where their detail went
        im = source.resize((s, s), Image.LANCZOS)
        if s >= 256:
            buf = BytesIO()
            im.save(buf, 'PNG')
            frames.append((s, buf.getvalue()))
        else:
            frames.append((s, dib(im)))

    off = 6 + 16 * len(frames)
    entries = b''
    for s, data in frames:
        byte = 0 if s >= 256 else s          # 256 is stored as 0 in a one-byte field
        entries += struct.pack('<BBBBHHII', byte, byte, 0, 0, 1, 32, len(data), off)
        off += len(data)

    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, len(frames)) + entries)
        for _, data in frames:
            f.write(data)


sizes = [16, 32, 48, 64, 128, 256]
write_ico("installer/halo.ico", img, sizes)
write_ico("src/Halo.App/Assets/halo.ico", img, sizes)
img.resize((256, 256), Image.LANCZOS).save("installer/halo.png")

# WizardSmallImageFile — the mark in the corner of every wizard page. 24-bit BMP flattened onto white,
# because Inno's wizard images are bitmaps and the transparent corners of a dark rounded square would
# otherwise come through as black notches.
card = Image.new("RGB", (138, 140), (255, 255, 255))
logo = img.resize((120, 120), Image.LANCZOS)
card.paste(logo, (9, 10), logo)
card.save("installer/wizard-small.bmp")

print("wrote installer/halo.ico, src/Halo.App/Assets/halo.ico, halo.png + wizard-small.bmp")
