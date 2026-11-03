"""User-authorized mask authoring only. Never modifies the generated colour sprites."""
from pathlib import Path
import hashlib
from PIL import Image, ImageDraw, ImageChops

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / '1.6/Textures/Things/Building/StationTrade'

def polygon(size, points):
    im = Image.new('L', size)
    ImageDraw.Draw(im).polygon(points, fill=255)
    return im

def build(name, terminal, hatch):
    source = ART / f'{name}.png'
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    image = Image.open(source).convert('RGBA')
    neutral = Image.new('L', image.size)
    neutral.putdata([255 if max(r,g,b)-min(r,g,b) < 38 and r+g+b > 285 and a >= 128 else 0
                     for r,g,b,a in image.getdata()])
    red = ImageChops.multiply(polygon(image.size, terminal), neutral) if terminal else Image.new('L', image.size)
    green = Image.new('L', image.size)
    for area in hatch:
        green = ImageChops.lighter(green, ImageChops.multiply(polygon(image.size, area), neutral))
    assert ImageChops.multiply(red, green).getbbox() is None
    mask = Image.merge('RGBA', (red, green, Image.new('L', image.size), Image.new('L', image.size, 255)))
    mask.save(ART / f'{name}_m.png')
    assert hashlib.sha256(source.read_bytes()).hexdigest() == before
    invisible = image.getchannel('A').point(lambda a:255 if a < 128 else 0)
    assert ImageChops.multiply(ImageChops.lighter(red,green),invisible).getbbox() is None
    print(name, 'source sha256', before, 'red pixels', red.histogram()[255], 'green pixels', green.histogram()[255])

build('CargoPort',
    [(155,187),(432,187),(461,216),(461,390),(451,406),(451,457),
     (471,472),(471,503),(399,503),(387,491),(204,491),(192,503),
     (120,503),(120,472),(139,457),(139,407),(126,390),(126,216)],
    [[(136,615),(275,615),(285,605),(501,605),(501,972),(95,972),(95,820),
      (104,808),(104,648)],
     [(523,605),(738,605),(749,615),(886,615),(921,648),(921,808),(931,820),
      (931,972),(523,972)],
     [(95,987),(501,987),(501,1329),(285,1329),(243,1370),(143,1370),
      (104,1331),(104,1165),(95,1151)],
     [(523,987),(931,987),(931,1151),(921,1165),(921,1331),(882,1370),
      (783,1370),(740,1329),(523,1329)]])
build('CargoExterior', None,
    [[(148,170),(496,170),(503,180),(503,1354),(491,1363),(153,1363),(113,1323),(113,205)],
     [(526,170),(872,170),(909,207),(909,1322),(870,1363),(530,1363),(519,1353),(519,180)]])
