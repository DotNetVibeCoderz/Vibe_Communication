"""Draws the package icon: one cord patched across a patch bay.

The samples' theme calls itself a patch bay, so the icon is the thing itself — a cobalt face plate,
a bank of jacks, and a single cord patched corner to corner. The sag is a real catenary rather than
an arc, because that is what tells you at a glance it is a cable and not a swoosh, and the cord
hanging in front of the jacks it crosses is what tells you it is a cord and not a line. Two blues
and a white: no gradient anywhere, so the whole thing survives being drawn at sixteen pixels.

Earlier passes put the jacks in a row above the cord. Don't: a row of dots over a hanging curve is
a face, and nothing else can be seen once that has been seen.

    python build/icon.py

Writes icon.png beside the solution — the one the packages carry — and build/icon-preview.png, which
is the same icon at the sizes a gallery and an IDE really use, on both the backgrounds they use.
"""

import math
import os

from PIL import Image, ImageDraw

# The theme's cobalt (samples/Shared/VoipNet.Samples.Theme/PatchBayTheme.axaml), the deeper cobalt
# the drilled jacks and the cord's shadow are cut in, and the nickel of a plug sleeve.
COBALT = (47, 91, 255)
JACK = (34, 67, 211)   # drilled, but only just: the cord has to win at small sizes
DEEP = (20, 42, 143)   # the cord's shadow, which has to read against both of those
SLEEVE = (244, 246, 251)

SIZE = 512
SCALE = 8

# Everything below is in sixty-fourths of the tile.
BANK = [13.5, 26.5, 39.5, 52.5]  # the jack grid, four by four
JACK_R = 2.8
NEAR = (BANK[0], BANK[0])  # the two the cord is patched into
FAR = (BANK[3], BANK[3])
SAG = 12.0
CORD_W = 5.3
PLUG_R = 5.0


def catenary(p0, p1, sag, steps=1600):
    """The cord's path: where a cable of its own weight hangs between two jacks, tilt and all."""
    (x0, y0), (x1, y1) = p0, p1
    half = (x1 - x0) / 2
    low, high = 0.01, 1000.0
    for _ in range(80):  # the shape parameter that gives exactly this much sag
        mid = (low + high) / 2
        if mid * (math.cosh(half / mid) - 1) > sag:
            low = mid
        else:
            high = mid
    a = (low + high) / 2
    centre = (x0 + x1) / 2
    drop = a * math.cosh(half / a)
    out = []
    for i in range(steps + 1):
        t = i / steps
        x = x0 + (x1 - x0) * t
        out.append((x, y0 + (y1 - y0) * t + drop - a * math.cosh((x - centre) / a)))
    return out


def stroke(draw, points, width, fill, dy=0.0):
    """Strokes a path by stamping a disc along it: round ends, and no scalloping on the curve."""
    r = width / 2
    for x, y in points:
        draw.ellipse((x - r, y + dy - r, x + r, y + dy + r), fill=fill)


def draw_icon(size):
    s = size * SCALE
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    u = s / 64.0  # one unit = a sixty-fourth of the tile

    # The face plate and the jacks drilled through it.
    draw.rounded_rectangle((0, 0, s - 1, s - 1), radius=12 * u, fill=COBALT)
    for y in BANK:
        for x in BANK:
            draw.ellipse(((x - JACK_R) * u, (y - JACK_R) * u,
                          (x + JACK_R) * u, (y + JACK_R) * u), fill=JACK)

    # The cord. Its shadow is flat and offset, the way a silkscreen layer is: it lifts the cord off
    # the plate, and off the jacks it crosses, without a gradient anywhere in the icon.
    path = catenary((NEAR[0] * u, NEAR[1] * u), (FAR[0] * u, FAR[1] * u), SAG * u)
    stroke(draw, path, CORD_W * u, DEEP, dy=1.4 * u)
    stroke(draw, path, CORD_W * u, SLEEVE)

    # The two plugs, seated. The sleeve covers the jack it is in, so there is no hole left to draw.
    for x, y in (NEAR, FAR):
        draw.ellipse(((x - PLUG_R) * u, (y + 1.4 - PLUG_R) * u,
                      (x + PLUG_R) * u, (y + 1.4 + PLUG_R) * u), fill=DEEP)
        draw.ellipse(((x - PLUG_R) * u, (y - PLUG_R) * u,
                      (x + PLUG_R) * u, (y + PLUG_R) * u), fill=SLEEVE)
        draw.ellipse(((x - 1.8) * u, (y - 1.8) * u,
                      (x + 1.8) * u, (y + 1.8) * u), fill=DEEP)

    return image.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    icon = draw_icon(SIZE)
    path = os.path.join(root, "icon.png")
    icon.save(path)
    print("wrote", path, icon.size)

    # The sizes a gallery and an IDE draw it at, on nuget.org's white and a dark IDE's grey.
    sizes = [256, 128, 64, 48, 32, 16]
    width = sum(sizes) + 24 * (len(sizes) + 1)
    sheet = Image.new("RGBA", (width, 560), (255, 255, 255, 255))
    ImageDraw.Draw(sheet).rectangle((0, 290, width, 560), fill=(24, 26, 30, 255))
    x = 24
    for n in sizes:
        sheet.alpha_composite(draw_icon(n), (x, 24))
        sheet.alpha_composite(draw_icon(n), (x, 320))
        x += n + 24

    preview = os.path.join(root, "build", "icon-preview.png")
    sheet.save(preview)
    print("wrote", preview)


if __name__ == "__main__":
    main()
