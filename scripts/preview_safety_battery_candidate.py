"""Preview-only shader mask and Rot4 comparison; writes exclusively to doc/art."""
from pathlib import Path
import argparse
import hashlib
from PIL import Image, ImageDraw, ImageChops
from storage_state_palette import production_colors

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / 'doc/art'
parser = argparse.ArgumentParser()
parser.add_argument('--variant', choices=['symmetric','faceted','mirrored'], default='mirrored')
variant = parser.parse_args().variant
SOURCE = ART / f'SafetyBattery_{variant}_concept.png'
before = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
source = Image.open(SOURCE).convert('RGBA')
assert source.size == (1536, 1024)
mask = Image.new('L', source.size)
src, dst = source.load(), mask.load()
faces = []
if variant != 'symmetric':
    # Include all three material planes, keeping their painted lighting intact.
    for top,bottom in [(198,424),(609,830)]:
        for left,right in [(180,536),(604,936),(1004,1356)]:
            cap=Image.new('L',source.size)
            ImageDraw.Draw(cap).polygon([(left+34,top),(right-34,top),(right,top+36),
                (right,bottom-36),(right-34,bottom),(left+34,bottom),
                (left,bottom-36),(left,top+36)],fill=255)
            mask=ImageChops.lighter(mask,cap)
            faces.append(cap.histogram()[255])
else:
    for top, bottom in [(200, 430), (600, 830)]:
        for left, right in [(170, 545), (595, 945), (995, 1365)]:
            marked = 0
            for y in range(top, bottom):
                for x in range(left, right):
                    r,g,b,a = src[x,y]
                    if a >= 128 and min(r,g,b) > 170 and max(r,g,b)-min(r,g,b) < 20:
                        dst[x,y] = 255
                        marked += 1
            assert marked > 60000, (left,top,marked)
            faces.append(marked)
channels = (mask, Image.new('L', source.size), Image.new('L', source.size), Image.new('L', source.size, 255))
Image.merge('RGBA', channels).save(ART / f'SafetyBattery_{variant}_concept_m.png', optimize=True)
assert ImageChops.multiply(mask,source.getchannel('A').point(lambda a:255 if a<128 else 0)).getbbox() is None
assert hashlib.sha256(SOURCE.read_bytes()).hexdigest() == before

def tint(image, red, color):
    rgba = image.split()
    tinted = Image.merge('RGBA', (*[c.point(lambda v,t=t:round(v*t)) for c,t in zip(rgba[:3],color)], rgba[3]))
    return Image.composite(tinted, image, red)

def render(image, red, color, rotation=0, tile=48, charge=.5, old=False):
    img = tint(image,red,color).resize((3*tile,2*tile),Image.Resampling.LANCZOS).rotate(-90*rotation,expand=True)
    # Proposed thinner bar fits the candidate's dedicated central recess.
    width,height,margin = 2.25*tile,(.22 if old else .16)*tile,(.08 if old else .06)*tile
    cx,cy=img.width/2,img.height/2
    bw,bh=(width,height) if rotation%2==0 else (height,width)
    d=ImageDraw.Draw(img)
    d.rectangle((cx-(bw+margin)/2,cy-(bh+margin)/2,cx+(bw+margin)/2,cy+(bh+margin)/2),fill=(77,77,77,255))
    if charge:
        rect=(cx-width/2,cy-height/2,cx-width/2+width*charge,cy+height/2) if rotation%2==0 else (cx-height/2,cy+width/2-width*charge,cx+height/2,cy+width/2)
        d.rectangle(rect,fill=(230,217,51,255))
    return img

connected,disconnected = ((.55,.70,.82),(.78,.69,.53)) if variant!='symmetric' else ((.2,1,.25),(1,.2,.2))
if variant == 'mirrored':
    palette = production_colors()
    connected,disconnected = [tuple(c/255 for c in palette[n]) for n in ('Active','Reserved')]
preview=Image.new('RGBA',(900,710),(37,40,44,255))
d=ImageDraw.Draw(preview)
d.text((20,12),'APPROVED - mirrored metal / rapid-gun cyan vs cannon gray / 50% charge' if variant=='mirrored' else 'HISTORICAL - '+variant+' / 50% charge',fill='white')
for r,label in enumerate(['N','E','S','W']):
    d.text((145+r*215,42),label,fill='white')
for row,(color,label) in enumerate([(connected,'CONNECTED'),(disconnected,'DISCONNECTED')]):
    d.text((18,74+row*165),label,fill='white')
    for r in range(4):
        img=render(source,mask,color,r)
        preview.alpha_composite(img,(130+r*215-img.width//2,145+row*165-img.height//2))

old=Image.open(ART/'SafetyBattery_small_indicator_reference.png').convert('RGBA')
old_mask=Image.open(ART/'SafetyBattery_small_indicator_reference_m.png').getchannel('R')
d.text((20,414),'SAME SCALE: connected vs disconnected on dark / light background' if variant!='symmetric' else 'SAME SCALE: OLD small surround vs NEW six caps',fill='white')
for row,tile in enumerate([32,24]):
    y=470+row*105
    d.text((20,y),f'{3*tile}x{2*tile}px',fill='white')
    entries = [(source,mask,connected,False,'Connected / dark'),(source,mask,disconnected,False,'Isolated / dark'),(source,mask,connected,False,'Connected / light'),(source,mask,disconnected,False,'Isolated / light')] if variant!='symmetric' else [(old,old_mask,connected,True,'OLD connected'),(source,mask,connected,False,'NEW connected'),(old,old_mask,disconnected,True,'OLD disconnected'),(source,mask,disconnected,False,'NEW disconnected')]
    for col,(im,m,color,is_old,label) in enumerate(entries):
        x=180+col*195
        d.text((x-60,y-30),label,fill='white')
        img=render(im,m,color,tile=tile,old=is_old)
        if variant!='symmetric' and col>=2:
            d.rectangle((x-img.width//2-8,y-8,x+img.width//2+8,y+img.height+8),fill=(160,157,150,255))
        preview.alpha_composite(img,(x-img.width//2,y))
preview.save(ART/f'SafetyBattery_{variant}_review.png')
solid=source.getchannel('A').point(lambda a:255 if a>=128 else 0).histogram()[255]
print('six faces:',faces,'mask/visible area:',round(sum(faces)/solid*100,1),'%')
print('mask pixels relative to old:',round(sum(faces)/old_mask.histogram()[255],1),'x')
print('source unchanged:',before)
print(ART/f'SafetyBattery_{variant}_review.png')
