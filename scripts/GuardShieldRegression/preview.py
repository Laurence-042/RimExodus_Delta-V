"""Offline UV/clipping QA with original reference art, not an in-game screenshot."""
from pathlib import Path
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont

root=Path(__file__).resolve().parents[2]
out=root/'Source/obj/GuardShieldPreview'
scenes=json.loads((out/'geometry.json').read_text())
source=root.parent/'_references/RimWorldArt/Game art source - RimWorld/Other/ForceField.psd'
tex=np.asarray(Image.open(source).convert('RGBA'),dtype=float)/255
font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',19)
small=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',15)
scale=1.1601562
w,h=360,310
X,Z=np.meshgrid((np.arange(w)+.5-w/2)/3,(h/2-np.arange(h)-.5)/3)
def render(fields,clipped):
    base=np.full((h,w,3),.055)
    # Neutral deck/grid makes interior whitening and seam artifacts visible.
    deck=(abs(X)<35)&(abs(Z)<21)
    base[deck]=(.22,.24,.26)
    grid=deck&((np.mod(X+35,5)<.22)|(np.mod(Z+21,5)<.22))
    base[grid]=(.10,.12,.14)
    for c in fields:
        extent=c['radius']*scale
        u=(X-c['x'])/(2*extent)+.5;v=.5-(Z-c['z'])/(2*extent)
        valid=(u>=0)&(u<=1)&(v>=0)&(v<=1)
        px=np.clip(u*511,0,511);py=np.clip(v*511,0,511)
        ix=px.astype(int);iy=py.astype(int);fx=(px-ix)[...,None];fy=(py-iy)[...,None]
        sample=(tex[iy,ix]*(1-fx)*(1-fy)+tex[iy,np.minimum(ix+1,511)]*fx*(1-fy)+tex[np.minimum(iy+1,511),ix]*(1-fx)*fy+tex[np.minimum(iy+1,511),np.minimum(ix+1,511)]*fx*fy)
        if clipped:
            poly=c['polygon'];mask=Image.new('L',(w,h));draw=ImageDraw.Draw(mask)
            if len(poly)>=3:draw.polygon([((x+c['x'])*3+w/2,h/2-(z+c['z'])*3) for x,z in poly],fill=255)
            valid &= np.asarray(mask)>0
        # Fixed alpha, no animation: preview of UV and partition, not GPU timing or exact backend blending.
        alpha=sample[...,3]*.34*valid
        base=base*(1-alpha[...,None])+sample[...,:3]*np.array([.8,.9,1])*alpha[...,None]
    return Image.fromarray(np.uint8(np.clip(base,0,1)*255))
canvas=Image.new('RGB',(w*4,h*3+125),(22,24,29));draw=ImageDraw.Draw(canvas)
draw.text((15,10),'Native ForceField texture: overlapping quads / clipped union',font=font,fill='white')
draw.text((15,38),'Offline original-art UV preview. Same tint/alpha; gameplay rendering still requires validation.',font=small,fill=(180,185,195))
for i,(name,fields) in enumerate(scenes.items()):
    x=(i%2)*w*2;y=80+(i//2)*(h+15)
    for j,clipped in enumerate([False,True]):
        panel=render(fields,clipped);canvas.paste(panel,(x+j*w,y))
        draw.text((x+j*w+8,y+5),name+(' / clipped' if clipped else ' / original overlap'),font=small,fill='white')
canvas.save(out/'comparison.png')
print(out/'comparison.png')
