"""Read production Def geometry and render QA previews without editing textures."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
TEXTURES = ROOT / '1.6/Textures'
defs = {d.findtext('defName'): d for p in (ROOT/'1.6/Defs').rglob('*.xml') for d in ET.parse(p).getroot() if d.findtext('defName')}
def nums(s): return tuple(float(n) for n in re.findall(r'-?\d+(?:\.\d+)?', s))
def graphic(path, size): return Image.open(TEXTURES/(path+'.png')).convert('RGBA').resize(tuple(round(x) for x in size),Image.Resampling.LANCZOS)
def draw_host(name, tile):
    if name=='DeltaV_CargoContainer':
        from cargo_art_preview import port
        return port(1,True,tile=tile).crop((tile,tile,tile*3,tile*4))
    d=defs[name]; w,h=nums(d.findtext('size')); canvas=Image.new('RGBA',(round(w*tile),round(h*tile)))
    path=d.findtext('graphicData/texPath')
    canvas.alpha_composite(graphic(path,(w*tile,h*tile)))
    config=d.find("modExtensions/li[@Class='DeltaV.DefModExtension_CargoAttachments']")
    if config is None:config=d.find("comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']")
    slots=config.findall('slots/li') or config.findall('attachmentSlots/li') if config is not None else []
    for slot in slots:
        x,_,z=nums(slot.findtext('cell'));dx,dz=nums(slot.findtext('drawOffset'));scale=float(slot.findtext('drawScale'))
        minx=-((int(w)-1)//2);minz=-((int(h)-1)//2)
        cx=(x-minx+.5+dx)*tile;cy=(h-(z-minz)-.5-dz)*tile
        plugin='DeltaV_CargoTemperatureModule' if name=='DeltaV_CargoContainer' else 'DeltaV_RailgunEnergyRecovery'
        tex=graphic(defs[plugin].findtext('graphicData/texPath'),(scale*tile,scale*tile))
        canvas.alpha_composite(tex,(round(cx-tex.width/2),round(cy-tex.height/2)))
    if 'RailgunAccelerator' in name:
        shell=graphic('Things/Building/SpaceWeapons/RailgunShell',(w*tile,h*tile))
        canvas.alpha_composite(shell,(0,-round(h*tile/2)))
    return canvas

names=['DeltaV_SafetyBattery','DeltaV_Weapon_RailgunAccelerator','DeltaV_Weapon_RailgunAcceleratorLarge','DeltaV_CargoContainer']
for name in ['DeltaV_SafetyBattery','DeltaV_RailgunEnergyRecovery','DeltaV_CargoTemperatureModule']:
    path=TEXTURES/(defs[name].findtext('graphicData/texPath')+'.png');im=Image.open(path)
    assert im.mode=='RGBA' and im.getchannel('A').getextrema()[0]==0 and im.getchannel('A').getextrema()[1]>=250,path
    print(name,im.size,'alpha bounds',im.getchannel('A').getbbox())
out=Image.new('RGBA',(1024,650),(37,40,44,255));draw=ImageDraw.Draw(out)
for col,name in enumerate(names):
    draw.text((col*250+10,10),name.replace('DeltaV_',''),fill='white')
    north=draw_host(name,48)
    for row,angle in enumerate([0,90,180,270]):
        rotated=north.rotate(angle,expand=True)
        out.alpha_composite(rotated,(col*250+20,row*155+35))
path=ROOT/'doc/art/EquipmentCost_game_scale_preview.png';out.save(path);print(path)
