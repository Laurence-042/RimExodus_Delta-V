"""Preview the engineering tops against the shared 2x2 base and draw-size contract."""
from pathlib import Path
from PIL import Image, ImageDraw
from build_weapon_preview_icons import ART, turret_icon

ROOT = Path(__file__).resolve().parents[1]
preview = Image.new("RGB", (960, 900), "#252b31")
draw = ImageDraw.Draw(preview)
for row, name in enumerate(("InertiaCoupler", "MiningBeam")):
    turret_icon("PointDefense_Base", name + "_Top", 2.4 / 2.6, name + "_MenuIcon")
    draw.text((12, row * 300 + 6), name + " / N E S W", fill="white")
    base = Image.open(ART / "PointDefense_Base.png").convert("RGBA").resize((220, 220))
    top = Image.open(ART / (name + "_Top.png")).convert("RGBA").resize((203, 203))
    for column, angle in enumerate((90, 0, -90, 180)):
        tile = base.copy()
        tile.alpha_composite(top.rotate(angle), (8, 8))
        preview.paste(tile, (column * 240 + 10, row * 300 + 28), tile)
        small = tile.resize((65, 65), Image.Resampling.LANCZOS)
        preview.paste(small, (column * 240 + 170, row * 300 + 230), small)
for column, name in enumerate(("RapidTurret", "LaserTurret", "InertiaCoupler", "MiningBeam")):
    draw.text((column * 240 + 12, 606), name + " / same scale", fill="white")
    base_name = "Turret2_Base" if column < 2 else "PointDefense_Base"
    tile = Image.open(ART / (base_name + ".png")).convert("RGBA").resize((220, 220))
    top = Image.open(ART / (name + "_Top.png")).convert("RGBA").resize((203, 203))
    tile.alpha_composite(top.rotate(90), (8, 8))
    preview.paste(tile, (column * 240 + 10, 628), tile)
    small = tile.resize((65, 65), Image.Resampling.LANCZOS)
    preview.paste(small, (column * 240 + 170, 830), small)
preview.save(ROOT / "doc/art/engineering/Engineering_Preview.png")
