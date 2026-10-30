# Draws the Rewired logo: a NAND gate with two input wires and an output wire.
# The geometry matches Assets/Scripts/Graphics/UI/RewiredLogo.cs (unit square -1..1, y up).
#
#   python3 Tools/Logo/make_logo.py Assets/Build/Icon.png 512
#   python3 Tools/Logo/make_logo.py Docs/logo.png 160
import sys
from PIL import Image, ImageDraw, ImageFilter

BG_TOP = (30, 33, 66)
BG_BOTTOM = (14, 15, 32)
GATE = (87, 94, 230)
OUTLINE = (242, 243, 255)
IN_A = (255, 90, 95)
IN_B = (62, 166, 255)
OUT = (255, 210, 63)

def render(size, background=True):
    ss = 4
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def P(x, y):
        return ((x + 1) / 2 * S, (1 - (y + 1) / 2) * S)

    def u(v):
        return v / 2 * S

    if background:
        grad = Image.new("RGBA", (S, S))
        gd = ImageDraw.Draw(grad)
        for i in range(S):
            t = i / (S - 1)
            c = tuple(int(BG_TOP[k] * (1 - t) + BG_BOTTOM[k] * t) for k in range(3)) + (255,)
            gd.line([(0, i), (S, i)], fill=c)
        mask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
        img.paste(grad, (0, 0), mask)
        # faint grid like the editor's
        grid = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        g = ImageDraw.Draw(grid)
        step = S / 16
        for i in range(1, 16):
            g.line([(i * step, 0), (i * step, S)], fill=(255, 255, 255, 14), width=max(1, S // 512))
            g.line([(0, i * step), (S, i * step)], fill=(255, 255, 255, 14), width=max(1, S // 512))
        img.alpha_composite(Image.composite(grid, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

    wire = 0.075
    def line(a, b, col, w=wire):
        d.line([P(*a), P(*b)], fill=col, width=int(u(w)))
        for p in (a, b):
            r = u(w) / 2
            x, y = P(*p)
            d.ellipse([x - r, y - r, x + r, y + r], fill=col)

    def disc(c, r, col):
        x, y = P(*c)
        d.ellipse([x - u(r), y - u(r), x + u(r), y + u(r)], fill=col)

    def gate(inset, col):
        # D shape: box from x0 to xm, half disc of radius h at xm.
        x0, xm, h = -0.42, 0.02, 0.40
        x0 += inset; h -= inset
        d.rectangle([P(x0, h), P(xm, -h)], fill=col)
        disc((xm, 0), h, col)

    # glow under the wires and gate
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gl = ImageDraw.Draw(glow)
    gl.ellipse([P(-0.6, 0.6), P(0.6, -0.6)], fill=(87, 94, 230, 90))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(S * 0.08)))

    line((-0.80, 0.20), (-0.42, 0.20), IN_A)
    line((-0.80, -0.20), (-0.42, -0.20), IN_B)
    line((0.58, 0.0), (0.80, 0.0), OUT)
    disc((-0.80, 0.20), 0.075, IN_A)
    disc((-0.80, -0.20), 0.075, IN_B)
    disc((0.80, 0.0), 0.075, OUT)

    gate(0.0, OUTLINE)
    gate(0.055, GATE)
    disc((0.505, 0.0), 0.085, OUTLINE)
    disc((0.505, 0.0), 0.035, BG_BOTTOM)

    return img.resize((size, size), Image.LANCZOS)

if __name__ == "__main__":
    out = sys.argv[1]
    size = int(sys.argv[2])
    bg = len(sys.argv) < 4 or sys.argv[3] != "nobg"
    render(size, bg).save(out)
