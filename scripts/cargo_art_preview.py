"""Read-only shader/layout preview helpers; writes no production art."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / '1.6/Textures/Things/Building/StationTrade'
XML = ET.parse(ROOT / '1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml').getroot()
CARGO = next(d for d in XML.findall('ThingDef') if d.findtext('defName') == 'DeltaV_CargoContainer')
COLORS = [(255,158,41),(51,166,255),(64,217,97)] # CompSpaceCargo.TerminalColor

def nums(text): return tuple(float(n) for n in re.findall(r'-?(?:\d*\.\d+|\d+)', text))

def tint(name, state=1, paint=(255,255,255)):
    source = Image.open(ART / f'{name}.png').convert('RGBA')
    mask = Image.open(ART / f'{name}_m.png').convert('RGBA')
    assert source.size == mask.size
    rgb = source.convert('RGB')
    for channel,color in [('R',COLORS[state]),('G',paint)]:
        rgb = Image.composite(ImageChops.multiply(rgb,Image.new('RGB',rgb.size,color)),rgb,mask.getchannel(channel))
    rgb.putalpha(source.getchannel('A'))
    return rgb

def port(state=1, installed=False, paint=(255,255,255), fill=.5, tile=48, grid=False):
    canvas = Image.new('RGBA',(tile*4,tile*5))
    size = nums(CARGO.findtext('graphicData/drawSize'))
    sprite = tint('CargoPort',state,paint).resize(tuple(round(v*tile) for v in size),Image.Resampling.LANCZOS)
    center = (tile*2,tile*2.5)
    canvas.alpha_composite(sprite,(round(center[0]-sprite.width/2),round(center[1]-sprite.height/2)))
    slot = CARGO.find("modExtensions/li[@Class='DeltaV.DefModExtension_CargoAttachments']/slots/li")
    x,_,z = nums(slot.findtext('cell')); dx,dz = nums(slot.findtext('drawOffset'))
    assert (x,z,dx,dz)==(1,1,0,0)
    scale = float(slot.findtext('drawScale'))
    cx,cy = (x+.5+dx)*tile+tile,(1.5-z-dz)*tile+tile
    if installed:
        module = Image.open(ART/'CargoTemperature.png').convert('RGBA').resize((round(scale*tile),)*2,Image.Resampling.LANCZOS)
        canvas.alpha_composite(module,(round(cx-module.width/2),round(cy-module.height/2)))
    code = (ROOT/'Source/Buildings/CompSpaceCargo.cs').read_text(encoding='utf-8-sig')
    offset = nums(re.search(r'capacityBarOffset = new Vector2\(([^)]*)\)',code)[1])
    width,height = nums(re.search(r'capacityBarSize = new Vector2\(([^)]*)\)',code)[1])
    assert 0 < width < 2 and 0 < height < 1
    draw = ImageDraw.Draw(canvas)
    barx,bary = center[0]+offset[0]*tile,center[1]-offset[1]*tile
    for i in range(6):
        left = barx-width*tile/2+(i/6*width+.0175)*tile
        right = left+(width/6-.035)*tile
        top,bottom = bary-height*tile/2,bary+height*tile/2
        draw.rectangle((left,top,right,bottom),fill=(31,36,38,255))
        portion = min(1,max(0,fill*6-i))
        if portion: draw.rectangle((left,top,left+(right-left)*portion,bottom),fill=(229,217,51,255))
    if grid:
        draw.rectangle((cx-tile/2,cy-tile/2,cx+tile/2,cy+tile/2),outline=(105,200,255,255))
        draw.ellipse((1.5*tile-5,2.5*tile-5,1.5*tile+5,2.5*tile+5),fill=(245,225,175,255))
        draw.line((1.5*tile,2.5*tile-6,1.5*tile,1.5*tile),fill=(245,225,175,255),width=2)
    return canvas

def validate():
    shell=ET.parse(ROOT/'1.6/Defs/Misc/DeltaV_ShellRenderRules.xml').getroot()
    exterior=shell.find("DeltaV.ShellRenderRuleDef[@Name='DeltaVCargoShell']/render/alternateGraphics/li")
    for name,drawsize in [('CargoPort',nums(CARGO.findtext('graphicData/drawSize'))),('CargoExterior',nums(exterior.findtext('drawSize')))]:
        source = Image.open(ART/f'{name}.png'); a=source.getchannel('A')
        assert source.mode=='RGBA' and a.getextrema()[0]==0 and a.getextrema()[1]>=250
        bounds=a.point(lambda v:255 if v>=128 else 0).getbbox()
        footprint=((bounds[2]-bounds[0])/source.width*drawsize[0],(bounds[3]-bounds[1])/source.height*drawsize[1])
        assert abs(footprint[0]-2)<.03 and abs(footprint[1]-3)<.03,(name,footprint)
        mask=Image.open(ART/f'{name}_m.png')
        assert ImageChops.multiply(mask.getchannel('R'),mask.getchannel('G')).getbbox() is None
        print(name,source.size,'alpha>=128 bounds',bounds,'visible footprint',footprint)

if __name__=='__main__': validate()
