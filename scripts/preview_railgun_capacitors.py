"""Preview live rail Def layers and slots in four rotations; no asset mutation."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw
ROOT = Path(__file__).resolve().parents[1]
DEFS = ET.parse(ROOT / '1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml').getroot()
TEXTURES = ROOT / '1.6/Textures'
OUT = ROOT / 'doc/art/Railgun_unified_preview.png'
def numbers(value):
    return tuple(float(p) for p in re.findall(r'-?\d+(?:\.\d+)?', value))
def layer(canvas, path, center, size, tile):
    image = Image.open(TEXTURES / (path + '.png')).convert('RGBA')
    image = image.resize(tuple(round(v * tile) for v in size), Image.Resampling.LANCZOS)
    canvas.alpha_composite(image, tuple(round(c - s / 2) for c, s in zip(center, image.size)))
def render(tile):
    canvas = Image.new('RGBA', (8 * tile, 8 * tile), (24, 26, 30, 255))
    draw = ImageDraw.Draw(canvas)
    for i in range(9):
        draw.line((i * tile, 0, i * tile, 8 * tile), fill='#343940')
        draw.line((0, i * tile, 8 * tile, i * tile), fill='#343940')
    for index, kind in enumerate(('Loader', 'Accelerator', 'Accelerator', 'Accelerator')):
        definition = next(d for d in DEFS if d.findtext('defName') == 'DeltaV_Weapon_Railgun' + kind)
        width, depth = numbers(definition.findtext('size'))
        assert (width, depth) == (3, 1)
        center = (4.5 * tile, (5.5 - index) * tile)
        size = numbers(definition.findtext('graphicData/drawSize'))
        layer(canvas, definition.findtext('graphicData/texPath'), center, size, tile)
        props = definition.find("comps/li[@Class='DeltaV.CompProperties_" + ('SpaceWeapon' if kind == 'Loader' else 'SpaceRailgun') + "']")
        for slot in props.findall('attachmentSlots/li'):
            x, _, z = numbers(slot.findtext('cell'))
            dx, dz = numbers(slot.findtext('drawOffset'))
            assert -1 <= x <= 1 and z == 0
            if index == 3:
                continue
            plugin = 'RailgunCapacitor' if index < 2 else 'RailgunEnergyRecovery'
            scale = float(slot.findtext('drawScale'))
            layer(canvas, 'Things/Building/SpaceWeapons/' + plugin,
                  (center[0] + (x + dx) * tile, center[1] - (z + dz) * tile), (scale, scale), tile)
        shell = definition.findtext("comps/li[@Class='DeltaV.CompProperties_RailgunShell']/texPath")
        layer(canvas, shell, (center[0], center[1] - depth * tile / 2), size, tile)
    return canvas
if __name__ == '__main__':
    high, actual = render(64), render(24)
    result = Image.new('RGBA', (high.width * 4, high.height + actual.height + 40), '#181a1e')
    for i, direction in enumerate(('North', 'East', 'South', 'West')):
        result.alpha_composite(high.rotate(-90 * i), (i * high.width, 20))
        result.alpha_composite(actual.rotate(-90 * i), (i * high.width, high.height + 35))
        ImageDraw.Draw(result).text((i * high.width + 10, 4), direction + ' / 3x1 parts', fill='white')
    OUT.parent.mkdir(parents=True, exist_ok=True)
    result.save(OUT)
    print(OUT)
