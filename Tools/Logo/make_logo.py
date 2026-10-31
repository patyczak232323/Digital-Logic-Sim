# Draws the Rewired logo: a chip with a gold R on it.
# The geometry matches Assets/Scripts/Graphics/UI/RewiredLogo.cs (unit square -1..1, y up).
#
#   python3 Tools/Logo/make_logo.py Assets/Build/Icon.png 512
#   python3 Tools/Logo/make_logo.py Docs/logo.png 160
import re
import sys
from PIL import Image, ImageDraw, ImageFilter

BG_TOP = (236, 239, 249)
BG_BOTTOM = (196, 203, 226)
BODY = (30, 33, 50)
BORDER = (72, 108, 233)
PIN = (168, 175, 198)
DOT = (74, 79, 104)
LETTER = (255, 196, 0)

BODY_HALF = 0.55
BORDER_WIDTH = 0.035
PIN_OFFSETS = (-0.39, -0.13, 0.13, 0.39)
PIN_WIDTH = 0.12
PIN_LENGTH = 0.12

# The R as square pixels on a 4 x 6 grid (top row first), drawn in the box R_LEFT..R_RIGHT,
# R_BOTTOM..R_TOP. The pixels match the pixel font of the REWIRED title.
R_PIXELS = [
    "###.",
    "#..#",
    "#..#",
    "###.",
    "#.#.",
    "#..#",
]
R_LEFT, R_RIGHT = -0.24, 0.24
R_BOTTOM, R_TOP = -0.36, 0.36


def r_boxes():
    """The R as boxes (x0, y0, x1, y1), one per horizontal run of pixels."""
    cols, rows = len(R_PIXELS[0]), len(R_PIXELS)
    cw, rh = (R_RIGHT - R_LEFT) / cols, (R_TOP - R_BOTTOM) / rows
    boxes = []
    for r, line in enumerate(R_PIXELS):
        y1 = R_TOP - r * rh
        for m in re.finditer("#+", line):
            boxes.append((R_LEFT + m.start() * cw, y1 - rh, R_LEFT + m.end() * cw, y1))
    return boxes


def render(size, background=True):
    ss = 4
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    def P(x, y):
        return ((x + 1) / 2 * S, (1 - (y + 1) / 2) * S)

    def u(v):
        return v / 2 * S

    def box(d, x0, y0, x1, y1, col):
        (a, b), (c, e) = P(x0, y1), P(x1, y0)
        d.rectangle([a, b, c, e], fill=col)

    def disc(d, c, r, col):
        x, y = P(*c)
        d.ellipse([x - u(r), y - u(r), x + u(r), y + u(r)], fill=col)

    if background:
        grad = Image.new("RGBA", (S, S))
        gd = ImageDraw.Draw(grad)
        for i in range(S):
            t = i / (S - 1)
            gd.line([(0, i), (S, i)], fill=tuple(int(BG_TOP[k] * (1 - t) + BG_BOTTOM[k] * t) for k in range(3)) + (255,))
        mask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
        img.paste(grad, (0, 0), mask)

        shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        box(ImageDraw.Draw(shadow), -0.6, -0.66, 0.6, 0.54, (20, 24, 50, 110))
        img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(S * 0.03)))

    d = ImageDraw.Draw(img)
    h = BODY_HALF
    for t in PIN_OFFSETS:
        w = PIN_WIDTH / 2
        box(d, -h - PIN_LENGTH, t - w, -h, t + w, PIN)
        box(d, h, t - w, h + PIN_LENGTH, t + w, PIN)
        box(d, t - w, h, t + w, h + PIN_LENGTH, PIN)
        box(d, t - w, -h - PIN_LENGTH, t + w, -h, PIN)

    box(d, -h, -h, h, h, BORDER)
    i = h - BORDER_WIDTH
    box(d, -i, -i, i, i, BODY)
    disc(d, (-0.38, 0.38), 0.045, DOT)

    for b in r_boxes():
        box(d, *b, LETTER)

    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = sys.argv[1]
    size = int(sys.argv[2])
    bg = len(sys.argv) < 4 or sys.argv[3] != "nobg"
    render(size, bg).save(out)
