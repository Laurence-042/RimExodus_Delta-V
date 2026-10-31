"""Build the two CutoutComplex mask channels for the existing gun top sprites.

Red marks the barrel (color); green marks the cooling grilles or body (colorTwo).
The painted faces in the source remain untouched, including their black outlines.
"""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
TEXTURES = ROOT / "1.6/Textures/Things/Building/SpaceWeapons"
PREVIEW = ROOT / "doc/art/WeaponHeat_mask_alignment_preview.png"
REGIONS = {
    "RapidTurret_Top": {
        "size": (1254, 1254),
        "barrel": [
            [(803, 576), (874, 576), (884, 567), (1095, 567), (1110, 578),
             (1146, 578), (1153, 569), (1218, 569), (1233, 583), (1233, 654),
             (1218, 669), (1153, 669), (1146, 659), (1110, 659), (1095, 672),
             (884, 672), (874, 664), (803, 664)],
        ],
        "body": [
            [(528, 411), (642, 411), (657, 425), (657, 480),
             (629, 501), (528, 501), (517, 490), (517, 442)],
            [(528, 732), (629, 732), (657, 756), (657, 811),
             (640, 828), (528, 828), (517, 817), (517, 752)],
        ],
    },
    "CannonTurret_Top": {
        "size": (1440, 1440),
        "barrel": [
            [(1007, 648), (1137, 648), (1147, 667), (1219, 667),
             (1228, 646), (1379, 646), (1395, 661), (1395, 800),
             (1379, 816), (1228, 816), (1219, 796), (1147, 796),
             (1137, 815), (1007, 815)],
        ],
        "body": [
            [(457, 565), (530, 565), (530, 853), (511, 879),
             (477, 879), (457, 855)],
            [(617, 601), (844, 601), (905, 672), (905, 786),
             (853, 859), (670, 859), (616, 819)],
        ],
    },
}


def polygon_layer(size, polygons):
    layer = Image.new("L", size)
    drawing = ImageDraw.Draw(layer)
    for polygon in polygons:
        drawing.polygon(polygon, fill=255)
    return layer


def build_mask(name, regions):
    source = Image.open(TEXTURES / f"{name}.png").convert("RGBA")
    if source.size != regions["size"]:
        raise ValueError(f"{name}: expected {regions['size']}, got {source.size}")
    red_region = polygon_layer(source.size, regions["barrel"])
    green_region = polygon_layer(source.size, regions["body"])
    red = Image.new("L", source.size)
    green = Image.new("L", source.size)
    src = source.load()
    r_out, g_out = red.load(), green.load()
    r_in, g_in = red_region.load(), green_region.load()
    for y in range(source.height):
        for x in range(source.width):
            r, g, b, alpha = src[x, y]
            if alpha < 16:
                continue
            if r_in[x, y]:
                r_out[x, y] = alpha
            if g_in[x, y] and max(r, g, b) - min(r, g, b) <= 18 and max(r, g, b) >= 35:
                g_out[x, y] = alpha
    if red.getbbox() is None or green.getbbox() is None:
        raise ValueError(f"{name}: missing red or green region")
    overlap = sum(1 for rv, gv in zip(red.tobytes(), green.tobytes()) if rv and gv)
    if overlap:
        raise ValueError(f"{name}: red and green masks overlap at {overlap} pixels")
    mask = Image.merge("RGBA", (red, green, Image.new("L", source.size), Image.new("L", source.size, 255)))
    mask.save(TEXTURES / f"{name}_m.png", optimize=True)
    return source, red, green


def preview_panel(source, red, green):
    panel = Image.new("RGBA", source.size, (35, 36, 38, 255))
    panel.alpha_composite(source)
    overlay = Image.new("RGBA", source.size)
    pixels = overlay.load()
    red_pixels, green_pixels = red.load(), green.load()
    for y in range(source.height):
        for x in range(source.width):
            rv, gv = red_pixels[x, y], green_pixels[x, y]
            if rv or gv:
                pixels[x, y] = (230 if rv else 30, 40 if rv else 220, 40, 145)
    panel.alpha_composite(overlay)
    return panel.resize((650, 650), Image.Resampling.LANCZOS)


panels = [preview_panel(*build_mask(name, regions)) for name, regions in REGIONS.items()]
preview = Image.new("RGBA", (1300, 650), (35, 36, 38, 255))
for index, panel in enumerate(panels):
    preview.alpha_composite(panel, (index * 650, 0))
preview.convert("RGB").save(PREVIEW, optimize=True)
