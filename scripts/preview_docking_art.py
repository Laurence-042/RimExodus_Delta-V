"""Render QA previews from the actual Def drawSize; never modifies production textures."""
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageOps

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "1.6/Textures/Things/Building/Docking"
OUT = ROOT / "doc/art/Docking_four_directions.png"
defs = ET.parse(ROOT / "1.6/Defs/ThingDefs_Buildings/DeltaV_Docking.xml").getroot()
passage = next(d for d in defs.findall("ThingDef") if d.findtext("defName") == "DeltaV_DockingPassage")
sx, sy = map(float, passage.findtext("graphicData/drawSize").strip("()").split(","))
leaf = Image.open(ART / "DockingPort_Mover.png").convert("RGBA")
deck = Image.open(ART / "DockingChannel.png").convert("RGBA")
assert leaf.width == leaf.height and deck.width == deck.height
for im, name in ((leaf, "door"), (deck, "channel")):
    alpha_min, alpha_max = im.getchannel("A").getextrema()
    # Cutout treats near-opaque generated alpha (253/254) as solid material.
    assert alpha_min == 0 and alpha_max >= 250, (name, alpha_min, alpha_max)
    bbox = im.getchannel("A").point(lambda a: 255 if a >= 128 else 0).getbbox()
    print(name, im.size, "visible alpha bbox", bbox)

# Building_Door.DrawMovers uses def.size, not graphicData.drawSize. Vanilla's
# 64px mover occupies y=10..54 with 3px black horizontal edges; margins belong
# in the texture. Allow one native pixel for generated/antialiased boundaries.
door_bounds = leaf.getchannel("A").point(lambda a: 255 if a >= 128 else 0).getbbox()
normalized = tuple(v * 64 / leaf.width for v in door_bounds)
assert abs(normalized[1] - 10) <= 1 and abs(normalized[3] - 54) <= 1, normalized
assert abs(normalized[0]) <= 1 and abs(normalized[2] - 32) <= 1, normalized
sample_x = round(leaf.width * 10 / 64)
for edge, step in ((door_bounds[1], 1), (door_bounds[3] - 1, -1)):
    thickness = 0
    while 0 <= edge + step * thickness < leaf.height:
        r, g, b, a = leaf.getpixel((sample_x, edge + step * thickness))
        if a < 128 or max(r, g, b) >= 24:
            break
        thickness += 1
    native_thickness = thickness * 64 / leaf.height
    assert 2.5 <= native_thickness <= 3.5, native_thickness
    print("door black edge at 64px/tile", round(native_thickness, 2))

def render(tile, opened):
    im = Image.new("RGBA", (tile * 7, tile * 7), (21, 26, 33, 255))
    draw = ImageDraw.Draw(im)
    for x in range(0, im.width, tile):
        draw.line((x, 0, x, im.height), fill=(33, 40, 48))
        draw.line((0, x, im.width, x), fill=(33, 40, 48))
    def paste(sprite, x, y):
        im.alpha_composite(sprite, (round(x - sprite.width / 2), round(y - sprite.height / 2)))
    center = tile * 3.5
    # Port at (0,0), three north-facing passage cells, target hull four cells north.
    py = tile * 5.5
    tile_deck = deck.resize((round(tile * sx), round(tile * sy)), Image.Resampling.LANCZOS)
    for i in range(1, 4):
        paste(tile_deck, center, py - tile * i)
    for x in (2, 4):
        draw.rectangle((x * tile, 5 * tile, (x + 1) * tile, 6 * tile), fill=(91, 94, 97), outline=(10, 12, 15), width=3)
    draw.rectangle((2 * tile, tile, 5 * tile, 2 * tile), fill=(91, 94, 97), outline=(10, 12, 15), width=3)
    draw.rectangle((3 * tile, 5 * tile, 4 * tile, 6 * tile), fill=(69, 73, 78))
    mover = leaf.resize((tile, tile), Image.Resampling.LANCZOS)
    target = Image.new("RGBA", (tile, tile))
    target.alpha_composite(mover)
    target.alpha_composite(ImageOps.mirror(mover))
    paste(target.rotate(180), center, tile * 1.5)
    offset = 0.45 * tile if opened else 0
    paste(mover, center - offset, py)
    paste(ImageOps.mirror(mover), center + offset, py)
    return im

tile = 48
panel = tile * 7
sheet = Image.new("RGBA", (panel * 4, (panel + 30) * 2), (21, 26, 33, 255))
draw = ImageDraw.Draw(sheet)
for row, opened in enumerate((False, True)):
    scene = render(tile, opened)
    for col, direction in enumerate(("N", "E", "S", "W")):
        sheet.alpha_composite(scene.rotate(-col * 90), (col * panel, row * (panel + 30) + 30))
        draw.text((col * panel + 12, row * (panel + 30) + 8), f"{direction} / {'OPEN' if opened else 'CLOSED'} / 48 px per cell", fill="white")
OUT.parent.mkdir(parents=True, exist_ok=True)
sheet.convert("RGB").save(OUT)
print(OUT)
