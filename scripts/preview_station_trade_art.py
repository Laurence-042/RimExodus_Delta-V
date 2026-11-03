"""Interior / exterior cargo art and independent terminal / hatch tint QA."""
from PIL import Image, ImageDraw
from cargo_art_preview import ROOT, ART, tint, port, validate

validate()
out=Image.new('RGBA',(1100,640),(37,40,44,255));draw=ImageDraw.Draw(out)
for i,name in enumerate(['Hack required','Sealed','Storage','Painted hatch']):
    paint=(160,185,235) if i==3 else (255,255,255)
    sprite=port(min(i,2),i==3,paint,fill=i/3)
    draw.text((i*215+15,10),name,fill='white')
    out.alpha_composite(sprite,(i*215,25))
draw.text((875,10),'Exterior box',fill='white')
exterior=tint('CargoExterior',paint=(160,185,235)).resize((109,146),Image.Resampling.LANCZOS)
out.alpha_composite(exterior,(900,70))
draw.text((15,290),'Two independent mask channels; cyan loading guides and expansion plumbing do not change color.',fill='white')
large=port(0,True,fill=.5,tile=72).crop((55,40,235,190))
out.alpha_composite(large.resize((360,300),Image.Resampling.NEAREST),(20,310))
draw.text((410,330),'Top row: upright terminal / empty or installed cooling socket',fill='white')
draw.text((410,355),'Bottom 2x2: walkable elevator hatch / live capacity / laser guides',fill='white')
draw.text((410,380),'The exterior box uses the same footprint and a live sun-directed shadow.',fill='white')
terminal=Image.open(ART/'TradeTerminal.png').convert('RGBA').resize((96,96),Image.Resampling.LANCZOS)
out.alpha_composite(terminal,(430,430));draw.text((550,465),'Station trade terminal / 2x2 top-down table',fill='white')
path=ROOT/'doc/art/StationTrade_game_scale_preview.png';out.save(path);print(path)
