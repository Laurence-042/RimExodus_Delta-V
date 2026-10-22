"""Compose the tractor icon and inspect its actual Def scale in four directions."""
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw
from build_weapon_preview_icons import ART, turret_icon

ROOT = Path(__file__).resolve().parents[1]
definition = ET.parse(ROOT / '1.6/Defs/SpaceCombat/DeltaV_Tractor.xml').find('ThingDef')
base_size = float(definition.findtext('graphicData/drawSize').strip('()').split(',')[0])
top_size = float(definition.findtext('building/turretTopDrawSize'))
turret_icon('PointDefense_Base', 'TractorBeam_Top', top_size / base_size, 'TractorBeam_MenuIcon')
preview = Image.new('RGB', (800, 360), '#252b31')
draw = ImageDraw.Draw(preview)
for column, angle in enumerate((90, 0, -90, 180)):
    draw.text((column * 200 + 12, 12), ('N', 'E', 'S', 'W')[column], fill='white')
    for pixels_per_cell, y in ((130, 40), (48, 245)):
        width = round(base_size * pixels_per_cell)
        base = Image.open(ART / 'PointDefense_Base.png').convert('RGBA').resize((width, width), Image.Resampling.LANCZOS)
        top_width = round(top_size * pixels_per_cell)
        top = Image.open(ART / 'TractorBeam_Top.png').convert('RGBA').resize((top_width, top_width), Image.Resampling.LANCZOS).rotate(angle)
        offset = (width - top_width) // 2
        base.alpha_composite(top, (offset, offset))
        x = column * 200 + (200 - width) // 2
        preview.paste(base, (x, y), base)
draw.text((12, 332), 'Top: enlarged / Bottom: 48 px per cell. Source pivot (630,630) -> (720,720).', fill='white')
preview.save(ROOT / 'doc/art/tractor/TractorBeam_Preview.png')
