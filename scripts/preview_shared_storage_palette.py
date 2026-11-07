"""Read-only production state-color comparison, with the original gun references."""
from pathlib import Path
import hashlib
from PIL import Image, ImageChops, ImageDraw, ImageFont
import cargo_art_preview as cargo
from storage_state_palette import production_colors

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / 'doc/art'
# Shader multipliers: blue/gray calibrated against the existing gun armor.
# Green/red borrow vanilla terminal hues, softened for a broad material panel.
PALETTE = production_colors()
COLORS = [PALETTE[n] for n in ('Locked','HostileLocked','Reserved','Active')]
SWATCHES = [(65,163,81), (170,62,51), (166,164,164), (146,185,193)]
NAMES = ['非敌对锁定 · 远古绿', '敌对锁定 · 终端红', '封存 / 脱网 · 重炮灰', '仓储 / 接网 · 速射青灰']
FILES = [cargo.ART/'CargoPort.png', cargo.ART/'CargoPort_m.png',
         ROOT/'1.6/Textures/Things/Building/Power/SafetyBattery.png',
         ROOT/'1.6/Textures/Things/Building/Power/SafetyBattery_m.png']
before = [hashlib.sha256(p.read_bytes()).hexdigest() for p in FILES]
source = Image.open(FILES[2]).convert('RGBA')
mask = Image.open(FILES[3]).getchannel('R')
font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 18)
small = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 14)
canvas = Image.new('RGBA', (1240,900), (37,40,44,255))
d = ImageDraw.Draw(canvas)
d.text((28,18), '统一状态色 · 货仓三态 / 电池两态', font=font, fill='white')
d.text((28,49), '正式资产与色表预览；锁定色按当前敌对关系区分。容量条统一显示 50%。', font=small, fill=(190,195,201))

def battery(color, tile):
    rgb = source.convert('RGB')
    rgb = Image.composite(ImageChops.multiply(rgb,Image.new('RGB',source.size,color)),rgb,mask)
    rgb.putalpha(source.getchannel('A'))
    img = rgb.resize((3*tile,2*tile),Image.Resampling.LANCZOS)
    bd = ImageDraw.Draw(img)
    cx,cy = img.width/2,img.height/2
    w,h,m = 2.25*tile,.16*tile,.06*tile
    bd.rectangle((cx-(w+m)/2,cy-(h+m)/2,cx+(w+m)/2,cy+(h+m)/2),fill=(77,77,77,255))
    bd.rectangle((cx-w/2,cy-h/2,cx,cy+h/2),fill=(230,217,51,255))
    return img

def paste_center(img,x,y):
    canvas.alpha_composite(img,(round(x-img.width/2),round(y-img.height/2)))

cargo.COLORS = COLORS
for i,(color,name) in enumerate(zip(COLORS,NAMES)):
    x=200+290*i
    d.rectangle((x-100,90,x+100,116),fill=SWATCHES[i])
    d.text((x-100,126),name,font=font,fill='white')
    d.text((x-100,154),'材质色参考 · 保留斜面明暗',font=small,fill=(190,195,201))
    paste_center(cargo.port(i,tile=48),x,294)
    if i>=2:
        paste_center(battery(color,48),x,476)
    else:
        d.text((x-100,460),'电池不使用锁定色',font=font,fill=(190,195,201))
    d.text((x-100,555),'缩小对照 · 24 像素 / 格',font=small,fill=(190,195,201))
    paste_center(cargo.port(i,tile=24),x-65,641)
    if i>=2:
        paste_center(battery(color,24),x+40,640)
d.text((28,205),'货仓',font=font,fill='white')
d.text((28,439),'电池',font=font,fill='white')
d.text((28,747),'正式模式图标 / 现有炮身参考',font=font,fill='white')
for name,x,label in [('CargoSealed',200,'封存'),('CargoStorage',490,'仓储')]:
    icon=Image.open(ROOT/f'1.6/Textures/UI/Commands/{name}.png').convert('RGBA')
    assert icon.getchannel('A').getextrema()[0] == 0
    paste_center(icon.resize((80,80),Image.Resampling.LANCZOS),x,824)
    d.text((x-30,875),label,font=small,fill='white')
for name,x,label in [('CannonTurret_Top',780,'重炮'),('RapidTurret_Top',1070,'速射炮')]:
    ref=Image.open(ROOT/f'1.6/Textures/Things/Building/SpaceWeapons/{name}.png').convert('RGBA')
    bounds=ref.getchannel('A').point(lambda a:255 if a>=128 else 0).getbbox()
    ref=ref.crop(bounds)
    ref.thumbnail((180,115),Image.Resampling.LANCZOS)
    paste_center(ref,x,817)
    d.text((x-30,875),label,font=small,fill='white')
output=ART/'SharedStoragePalette_gun_reference_review.png'
canvas.save(output)
assert before == [hashlib.sha256(p.read_bytes()).hexdigest() for p in FILES]
print(output)
print('Source images and masks unchanged.')
