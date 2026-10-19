"""Read-only art QA: overlay real XML nozzle positions on the unchanged shuttle sprite."""
from pathlib import Path
import math
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
definition = next(x for x in ET.parse(ROOT / '1.6/Defs/SpaceCombat/DeltaV_ShuttlesAndMoonPool.xml').getroot()
                  if x.findtext('defName') == 'DeltaV_ShuttleBoarding')
sprite = Image.open(ROOT / ('1.6/Textures/' + definition.findtext('graphicData/texPath') + '.png')).convert('RGBA')
pair = lambda value: tuple(map(float, value.strip('()').split(',')))
width, height = pair(definition.findtext('graphicData/drawSize'))
layout = [(pair(x.findtext('position')), pair(x.findtext('force')))
          for x in definition.findall('modExtensions/li/virtualThrusters/li')]
scale = 60
panel = Image.new('RGBA', (480, 480), (22, 28, 38, 255))
center = (240, 200)
sprite = sprite.resize((round(width * scale), round(height * scale)), Image.Resampling.LANCZOS)
panel.alpha_composite(sprite, (round(center[0] - sprite.width / 2), round(center[1] - sprite.height / 2)))
draw = ImageDraw.Draw(panel)
for i, ((forward, left), (fx, fy)) in enumerate(layout):
    x, y = center[0] - left * scale, center[1] - forward * scale
    magnitude = math.hypot(fx, fy)
    color = (255, 176, 75) if fx > 0 else (80, 216, 240)
    # Arrow points out of the nozzle (opposite the force).
    length = 80 if fx > 0 else 32
    ex, ey = x + fy / magnitude * length, y + fx / magnitude * length
    draw.line((x, y, ex, ey), fill=color, width=3)
    draw.ellipse((x - 3, y - 3, x + 3, y + 3), fill='white')
    draw.text((ex + 4, ey - 5), str(i + 1), fill=color)

out = Image.new('RGB', (960, 1010), (22, 28, 38))
draw = ImageDraw.Draw(out)
draw.text((20, 12), 'Boarding shuttle: XML nozzle alignment / arrows = exhaust direction', fill='white')
for index, name in enumerate(('North', 'East', 'South', 'West')):
    x, y = (index % 2) * 480, 40 + (index // 2) * 480
    out.paste(panel.rotate(-index * 90), (x, y))
    draw.text((x + 15, y + 12), name, fill='white')
destination = ROOT / 'doc/art/BoardingShuttle_thruster_alignment.png'
out.save(destination)
print(destination)
for index, (position, force) in enumerate(layout):
    print(f'{index+1}: body position={position}, force={force}')
