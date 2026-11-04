"""Read-only EVA QA: real Def scale, native south-only rotation and X exhaust points."""
from pathlib import Path
import hashlib
import math
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / 'doc/art/eva'
definition = next(d for d in ET.parse(ROOT / '1.6/Defs/SpaceCombat/DeltaV_Eva.xml').getroot()
                  if d.findtext('defName') == 'DeltaV_EvaSuit')
graphic = definition.find('graphicData')
pair = lambda text: tuple(map(float, text.strip('()').split(',')))
size = pair(graphic.findtext('drawSize'))
path = ROOT / ('1.6/Textures/' + graphic.findtext('texPath') + '_south.png')
source = ART / 'Eva_SCV_concept_v4.png'
assert path.read_bytes() == source.read_bytes(), 'Production sprite must preserve approved source'
assert graphic.findtext('graphicClass') == 'Graphic_Multi'
assert graphic.findtext('drawRotated') == 'true' and graphic.findtext('allowFlip') == 'false'
assert all(not path.with_name('EvaSuit_' + d + '.png').exists() for d in ('north', 'east', 'west'))
assert (ROOT / ('1.6/Textures/' + definition.findtext('uiIconPath') + '.png')).exists()
sprite = Image.open(path)
assert sprite.mode == 'RGBA'
alpha = sprite.getchannel('A')
assert all(alpha.getpixel(p) == 0 for p in [(0, 0), (sprite.width-1, 0),
                                         (0, sprite.height-1), (sprite.width-1, sprite.height-1)])
box = alpha.point(lambda v: 255 if v >= 128 else 0).getbbox()
assert box and box[0] > 0 and box[1] > 0 and box[2] < sprite.width and box[3] < sprite.height
visible = ((box[2]-box[0])*size[0]/sprite.width, (box[3]-box[1])*size[1]/sprite.height)
assert all(.95 < v < 1.01 for v in visible), visible
layout = [(pair(t.findtext('position')), pair(t.findtext('force')))
          for t in definition.findall('comps/li/virtualThrusters/li')]

# Native Graphic_Multi uses the south texture for every material, adds 180 degrees,
# and does not flip west when allowFlip=false. Clockwise map rotation is negative PIL rotation.
def facing_image(rotation, tile):
    return sprite.rotate(-(rotation*90 + 180)).resize(
        tuple(round(v*tile) for v in size), Image.Resampling.LANCZOS)

out = Image.new('RGBA', (1000, 700), (35, 39, 44, 255))
draw = ImageDraw.Draw(out)
draw.text((20, 16), 'EVA / approved source unchanged / four directions / arrows = virtual exhaust', fill='white')
for r, name in enumerate(('North', 'East', 'South', 'West')):
    cx, cy, tile = 125 + r*250, 170, 180
    draw.text((cx-20, 47), name, fill='white')
    draw.rectangle((cx-tile/2, cy-tile/2, cx+tile/2, cy+tile/2), outline=(91, 97, 106))
    im = facing_image(r, tile)
    out.alpha_composite(im, (cx-im.width//2, cy-im.height//2))
    for (forward, left), (fx, fy) in layout:
        x, y = -left, -forward
        ex, ey = fy, fx
        angle = math.radians(r*90)
        rotate = lambda a, b: (a*math.cos(angle)-b*math.sin(angle), a*math.sin(angle)+b*math.cos(angle))
        x, y = rotate(x, y)
        ex, ey = rotate(ex, ey)
        length = math.hypot(ex, ey)
        px, py = cx+x*tile, cy+y*tile
        qx, qy = px+ex/length*32, py+ey/length*32
        draw.line((px, py, qx, qy), fill=(80, 225, 255), width=2)
        draw.ellipse((px-2, py-2, px+2, py+2), fill='white')
    for y, background in ((345, (22, 26, 32)), (445, (174, 176, 179))):
        draw.rectangle((cx-90, y-35, cx+90, y+35), fill=background)
        im = facing_image(r, 48)
        out.alpha_composite(im, (cx-im.width//2, y-im.height//2))

draw.text((20, 292), '48 pixels/tile: dark space and light deck', fill='white')
draw.text((20, 504), 'Cargo style comparison / 48 pixels/tile / cargo 2x3, EVA 1x1', fill='white')
cargo = Image.open(ROOT / '1.6/Textures/Things/Building/StationTrade/CargoExterior.png').convert('RGBA')
cargo = cargo.resize((109, 146), Image.Resampling.LANCZOS)
out.alpha_composite(cargo, (65, 535))
out.alpha_composite(facing_image(2, 48), (205, 579))
draw.text((360, 560), 'One south source; native four-way rotation, no mirroring.', fill='white')
draw.text((360, 584), 'No runtime, geometry, paint mask or weapon-origin changes.', fill='white')
draw.text((360, 608), 'Preview is not an in-game screenshot.', fill='white')
out.convert('RGB').save(ART / 'Eva_game_scale_preview.png')
print('RGBA:', sprite.size, 'alpha>=128 bbox:', box, 'visible cells:', visible)
print('SHA256:', hashlib.sha256(path.read_bytes()).hexdigest())
print(ART / 'Eva_game_scale_preview.png')
