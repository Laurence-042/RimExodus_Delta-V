"""Build the north and central prisms' masks without changing sprite geometry.

The user requested deterministic Python processing. The saved original is the
source of truth on reruns; only cyan pixels inside the prism faces are neutralized.
"""

from pathlib import Path
from shutil import copyfile

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
TEXTURES = ROOT / "1.6/Textures/Things/Building/SpaceWeapons"
ART = ROOT / "doc/art"
SOURCE = ART / "ShieldProjector_cyan_reference.png"
TARGET = TEXTURES / "ShieldProjector.png"

if not SOURCE.exists():
    copyfile(TARGET, SOURCE)
source = Image.open(SOURCE).convert("RGBA")
assert source.size == (256, 256)
neutral = source.copy()
mask = Image.new("RGBA", source.size, (0, 0, 0, 255))
region = Image.new("L", source.size)
regions = {
    # Exclude the cyan reflections on the surrounding metal bevel, especially
    # the strip above the north face. Chroma alone also selects that strip.
    "north": [(66, 41), (189, 41), (191, 45), (173, 73), (84, 73), (64, 45)],
    "central": [(75, 103), (181, 103), (185, 111), (196, 116),
                (196, 151), (185, 157), (181, 165), (75, 165),
                (70, 157), (59, 151), (59, 116), (70, 111)],
}
for polygon in regions.values():
    ImageDraw.Draw(region).polygon(polygon, fill=255)
marked = 0
for y in range(source.height):
    for x in range(source.width):
        r, g, b, a = source.getpixel((x, y))
        if region.getpixel((x, y)) and a and min(g, b) - r >= 8:
            # Keep the bright face's existing shading, alpha and black outlines.
            value = max(r, g, b)
            neutral.putpixel((x, y), (value, value, value, a))
            mask.putpixel((x, y), (255, 0, 0, 255))
            marked += 1
assert 10000 < marked < 13000, marked
assert mask.getchannel("R").getbbox()[1] == 41
assert neutral.getchannel("A").tobytes() == source.getchannel("A").tobytes()
for y in range(256):
    for x in range(256):
        marked_pixel = mask.getpixel((x, y))
        assert marked_pixel[1:] == (0, 0, 255)
        assert marked_pixel[0] or source.getpixel((x, y)) == neutral.getpixel((x, y))
neutral.save(TARGET, optimize=True)
mask.save(TEXTURES / "ShieldProjector_m.png", optimize=True)


def tinted(color):
    result = neutral.copy()
    for y in range(256):
        for x in range(256):
            if mask.getpixel((x, y))[0]:
                r, g, b, a = neutral.getpixel((x, y))
                result.putpixel((x, y), (round(r * color[0]), round(g * color[1]),
                                         round(b * color[2]), a))
    return result


states = [("Original", source), ("Kinetic", tinted((.25, .55, 1))),
          ("Electromagnetic", tinted((.7, .35, 1))),
          ("Guard regular", tinted((1, 1, 1))),
          ("Guard emergency", tinted((1, .15, .15)))]
preview = Image.new("RGBA", (1000, 470), (36, 38, 42, 255))
draw = ImageDraw.Draw(preview)
for i, (label, sprite) in enumerate(states):
    x = i * 200
    draw.text((x + 10, 10), label, fill="white")
    preview.alpha_composite(sprite.resize((180, 180), Image.Resampling.NEAREST), (x + 10, 30))
    # Actual 3x3 / 1x1 Def scales at 48 pixels per cell.
    for base_name, base_size, top_size, row in [
        ("ShieldGenerator", 144, 134, 235), ("PointDefense_Base", 62, 58, 405)
    ]:
        base = Image.open(TEXTURES / f"{base_name}.png").convert("RGBA")
        preview.alpha_composite(base.resize((base_size, base_size), Image.Resampling.LANCZOS),
                                (x + (200 - base_size) // 2, row))
        preview.alpha_composite(sprite.resize((top_size, top_size), Image.Resampling.LANCZOS),
                                (x + (200 - top_size) // 2, row + (base_size - top_size) // 2))
preview.convert("RGB").save(ART / "ShieldProjector_prism_preview.png", optimize=True)
print(f"PASS {marked} prism pixels masked; north begins at y=41; alpha and all unmasked pixels unchanged")
