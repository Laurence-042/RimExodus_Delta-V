"""Inspect generated cargo assets at real Def geometry; writes QA composites only."""
from pathlib import Path
import argparse
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageChops

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--locked', type=Path)
parser.add_argument('--unlocked', type=Path)
args = parser.parse_args()
root = ET.parse(ROOT/'1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml').getroot()
defs = {d.findtext('defName'): d for d in root.findall('ThingDef')}
cargo = defs['DeltaV_CargoContainer']
def nums(s): return tuple(float(n) for n in re.findall(r'-?\d+(?:\.\d+)?', s))
def tex(s): return ROOT/'1.6/Textures'/f'{s}.png'
slot = cargo.find("modExtensions/li[@Class='DeltaV.DefModExtension_CargoAttachments']/slots/li")
w,h = nums(cargo.findtext('size'))
x,_,z = nums(slot.findtext('cell'))
dx,dz = nums(slot.findtext('drawOffset'))
scale = float(slot.findtext('drawScale'))
cx = x + ((int(w)-1)//2) + .5 + dx
cy = h - (z + ((int(h)-1)//2) + .5 + dz)
assert scale/2 <= cx <= w-scale/2 and scale/2 <= cy <= h-scale/2
paths = [args.unlocked or tex(root.findtext('AdaptiveStorage.GraphicsDef/graphics/li/graphicData/unlockedGraphicData/texPath')),
         args.locked or tex(cargo.findtext('graphicData/texPath'))]
sources = [Image.open(p) for p in paths]
for path,im in zip(paths,sources):
    assert im.mode == 'RGBA', path
    alpha = im.getchannel('A'); bounds = alpha.point(lambda a: 255 if a >= 128 else 0).getbbox()
    low,high=alpha.getextrema()
    assert low==0 and high>=250, path
    assert all(alpha.getpixel(pos)==0 for pos in [(0,0),(im.width-1,0),(0,im.height-1),(im.width-1,im.height-1)])
    assert bounds[0]<im.width*.015 and bounds[1]<im.height*.015 and bounds[2]>im.width*.985 and bounds[3]>im.height*.985
    print(path.name,im.size,'alpha bounds',bounds)
assert sources[0].size == sources[1].size
a,b=[im.getchannel('A').point(lambda a: 255 if a>=128 else 0) for im in sources]
diff=ImageChops.difference(a,b)
agreement=1-sum(diff.getdata())/(255*a.width*a.height)
assert agreement>.995,agreement
print('State silhouette agreement:',agreement,'module center in tiles:',cx,cy,'scale:',scale)
module=Image.open(tex(defs['DeltaV_CargoTemperatureModule'].findtext('graphicData/texPath'))).convert('RGBA')
out=Image.new('RGBA',(1000,820),(37,40,44,255));draw=ImageDraw.Draw(out)
for col,(source,installed) in enumerate([(sources[0],False),(sources[0],True),(sources[1],False),(sources[1],True)]):
    draw.text((col*250+12,10),('Unlocked' if col<2 else 'Locked')+(' + module' if installed else ' / empty socket'),fill='white')
    for row,angle in enumerate([0,90,180,270]):
        tile=48
        base=source.resize((round(w*tile),round(h*tile)),Image.Resampling.LANCZOS)
        if installed:
            overlay=module.resize((round(scale*tile),round(scale*tile)),Image.Resampling.LANCZOS)
            base.alpha_composite(overlay,(round(cx*tile-overlay.width/2),round(cy*tile-overlay.height/2)))
        base=base.rotate(angle,expand=True)
        out.alpha_composite(base,(col*250+50,row*180+40))
draw.text((12,780),'48 px/tile; current Def slot, offset and scale; read-only QA composition',fill='white')
output=ROOT/'doc/art/CargoModule_game_scale_preview.png';out.save(output);print(output)
