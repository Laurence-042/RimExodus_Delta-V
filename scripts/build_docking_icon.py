"""Mirror the production door leaf exactly as the vanilla closed-door draw does."""
from pathlib import Path

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "1.6/Textures/Things/Building/Docking"

leaf = Image.open(ART / "DockingPort_Mover.png").convert("RGBA")
assert leaf.width == leaf.height
icon = Image.new("RGBA", leaf.size)
icon.alpha_composite(leaf)
icon.alpha_composite(ImageOps.mirror(leaf))
icon.save(ART / "DockingPort_MenuIcon.png")
print(ART / "DockingPort_MenuIcon.png")
