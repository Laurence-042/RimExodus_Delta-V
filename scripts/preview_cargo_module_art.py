"""Four-way QA of native cargo states, real module placement and hack station."""
from PIL import Image, ImageDraw
from cargo_art_preview import ROOT, port, validate

validate()
out=Image.new('RGBA',(1440,1120),(37,40,44,255)); draw=ImageDraw.Draw(out)
for state,name in enumerate(['Hack required','Sealed','Storage']):
    for installed in [False,True]:
        col=state*2+int(installed)
        draw.text((col*240+12,12),name+(' + module' if installed else ' / empty'),fill='white')
        for row,angle in enumerate([0,90,180,270]):
            sprite=port(state,installed,fill=(row+1)/4,grid=True).rotate(angle,expand=True)
            out.alpha_composite(sprite,(col*240+(240-sprite.width)//2,row*260+35))
draw.text((15,1090),'48 px/tile; blue = construction cell; tan = hacker and facing; fill 25 / 50 / 75 / 100%',fill='white')
path=ROOT/'doc/art/CargoModule_game_scale_preview.png';out.save(path);print(path)
