"""Read-only production mask and Def geometry preview."""
from pathlib import Path
import hashlib
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageChops
from storage_state_palette import production_colors

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / '1.6/Textures/Things/Building/Power'
OUT = ROOT / 'doc/art'

def numbers(value):
    return tuple(float(n) for n in re.findall(r'-?\d+(?:\.\d+)?', value))

def definition(name):
    return ET.parse(ROOT / f'1.6/Defs/ThingDefs_Buildings/DeltaV_{name}.xml').getroot().find('ThingDef')

battery_path = ART / 'SafetyBattery.png'
before = hashlib.sha256(battery_path.read_bytes()).hexdigest()
battery = Image.open(battery_path).convert('RGBA')
assert battery.size == (1536, 1024)
mask = Image.open(ART / 'SafetyBattery_m.png').convert('RGBA')
assert mask.size == battery.size
red = mask.getchannel('R')
assert not mask.getchannel('G').getbbox() and not mask.getchannel('B').getbbox()
assert ImageChops.multiply(red,battery.getchannel('A').point(lambda a:255 if a<128 else 0)).getbbox() is None
assert red.getbbox() and hashlib.sha256(battery_path.read_bytes()).hexdigest() == before

reactor_def = definition('ShipReactor')
reactor = Image.open(ART / 'ShipReactor.png').convert('RGBA')
dw, dh = numbers(reactor_def.findtext('graphicData/drawSize'))
tile = 64
preview = Image.new('RGBA', (1152, 690), (37, 40, 44, 255))
draw = ImageDraw.Draw(preview)
for i in range(4):
    cx, cy = 144 + i * 288, 180
    tex = reactor.resize((round(dw * tile), round(dh * tile)), Image.Resampling.LANCZOS).rotate(-90 * i, expand=True)
    preview.alpha_composite(tex, (cx - tex.width // 2, cy - tex.height // 2))
    draw.rectangle((cx-128, cy-128, cx+128, cy+128), outline=(60,230,220), width=1)
    draw.text((cx-125, 15), ['N', 'E', 'S', 'W'][i]+' - 4x4 footprint', fill='white')

def tinted(tint):
    channels = battery.split()
    altered = [c.point(lambda v, t=t: round(v*t)) for c,t in zip(channels[:3], tint)]
    return Image.composite(Image.merge('RGBA', (*altered,channels[3])), battery, red)

palette = production_colors()
for row, (state, label) in enumerate([('Active','CONNECTED'), ('Reserved','DISCONNECTED')]):
    tint = tuple(c/255 for c in palette[state])
    draw.text((12, 355+row*165),label,fill='white')
    for i,charge in enumerate([0,.25,.5,1]):
        tex = tinted(tint).resize((192,128),Image.Resampling.LANCZOS)
        tex=tex.rotate(-90*i,expand=True)
        bar = ImageDraw.Draw(tex)
        w,h,margin = 2.25*tile,.16*tile,.06*tile
        cx,cy=tex.width/2,tex.height/2
        bw,bh=(w,h) if i%2==0 else (h,w)
        bar.rectangle((cx-(bw+margin)/2,cy-(bh+margin)/2,cx+(bw+margin)/2,cy+(bh+margin)/2),fill=(77,77,77,255))
        if charge:
            box=(cx-w/2,cy-h/2,cx-w/2+w*charge,cy+h/2) if i%2==0 else (cx-h/2,cy+w/2-w*charge,cx+h/2,cy+w/2)
            bar.rectangle(box,fill=(230,217,51,255))
        preview.alpha_composite(tex,(144+i*288-tex.width//2,430+row*165-tex.height//2))
preview.save(OUT / 'PowerEquipment_alignment_preview.png')
print('mask bounds', red.getbbox(), 'source unchanged', before)
print('reactor straight edges in cells', ((93/1254-.5)*dw,(1162/1254-.5)*dw,(.5-96/1254)*dh,(.5-1157/1254)*dh))
print(OUT / 'PowerEquipment_alignment_preview.png')
