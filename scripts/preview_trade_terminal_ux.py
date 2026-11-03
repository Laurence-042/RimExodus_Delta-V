"""Read-only sprite and command-icon QA at their displayed sizes."""
import hashlib
import json
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw
from cargo_art_preview import ROOT, ART, port

record_path = ROOT / 'doc/art/TradeTerminalUX_generation_prompts.json'
record = json.loads(record_path.read_text(encoding='utf-8'))
for asset in record['assets']:
    path = ROOT / asset['path']
    im = Image.open(path)
    assert im.mode == 'RGBA'
    a = im.getchannel('A')
    assert all(a.getpixel(p) == 0 for p in [(0, 0), (im.width-1, 0), (0, im.height-1), (im.width-1, im.height-1)])
    box = a.point(lambda v: 255 if v >= 128 else 0).getbbox()
    assert box and min(box) > 0 and box[2] < im.width and box[3] < im.height
    print(path.name, im.size, 'alpha>=128 bbox', box, 'sha256', hashlib.sha256(path.read_bytes()).hexdigest())

defs = ET.parse(ROOT / '1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml')
terminal_def = next(d for d in defs.getroot().findall('ThingDef') if d.findtext('defName') == 'DeltaV_TradeTerminal')
size = tuple(float(n) for n in terminal_def.findtext('graphicData/drawSize').strip('()').split(','))
terminal = Image.open(ART / 'TradeTerminal.png').convert('RGBA')
out = Image.new('RGBA', (1000, 560), (37, 40, 44, 255))
draw = ImageDraw.Draw(out)
draw.text((20, 15), 'Matching cargo / trade terminal family', fill='white')
out.alpha_composite(port(2, True, tile=48), (0, 35))
out.alpha_composite(terminal.resize((192, 192), Image.Resampling.LANCZOS), (230, 65))
draw.text((230, 42), 'Trade terminal / enlarged', fill='white')
draw.text((450, 42), 'Four rotations / 48 pixels per tile', fill='white')
scaled = terminal.resize(tuple(round(v*48) for v in size), Image.Resampling.LANCZOS)
for i, angle in enumerate([0, -90, 180, 90]):
    out.alpha_composite(scaled.rotate(angle, expand=True), (450+i*130, 85))
    draw.text((485+i*130, 195), ['N','E','S','W'][i], fill='white')
draw.text((450, 225), 'Ship rotation / same sprite / 2x2 footprint', fill='white')
for i, angle in enumerate([30, 135, 210, 315]):
    rotated = terminal.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True)
    rotated = rotated.resize((round(rotated.width*48*size[0]/terminal.width), round(rotated.height*48*size[1]/terminal.height)), Image.Resampling.LANCZOS)
    out.alpha_composite(rotated, (498+i*130-rotated.width//2, 310-rotated.height//2))
    draw.text((480+i*130, 390), str(angle), fill='white')
draw.text((20, 420), 'Current mode icons / 75 px command mockup (not an in-game screenshot)', fill='white')
for i, (name, label) in enumerate([('CargoSealed', 'Sealed'), ('CargoStorage', 'Storage')]):
    icon = Image.open(ROOT / f'1.6/Textures/UI/Commands/{name}.png').convert('RGBA')
    x = 30+i*140
    draw.rounded_rectangle((x, 450, x+75, 525), radius=4, fill=(65, 69, 73), outline=(130, 132, 136))
    out.alpha_composite(icon.resize((54, 54), Image.Resampling.LANCZOS), (x+10, 453))
    draw.text((x+10, 508), label, fill='white')
    out.alpha_composite(icon.resize((36, 36), Image.Resampling.LANCZOS), (x+85, 465))
out.save(ROOT / 'doc/art/TradeTerminalUX_preview.png')
