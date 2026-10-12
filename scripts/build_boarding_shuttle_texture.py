"""Size the approved north-facing boarding shuttle art and its paint mask."""

from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "doc/art/BoardingShuttle_simplified_concept.png"
TEXTURES = ROOT / "1.6/Textures/Things/Building/Shuttle"
SPRITE = TEXTURES / "BoardingShuttle.png"
MASK = TEXTURES / "BoardingShuttle_m.png"
MENU_ICON = TEXTURES / "BoardingShuttle_MenuIcon.png"
PREVIEW = ROOT / "doc/art/BoardingShuttle_rotation_preview.png"
SIZE = (384, 640)  # Twice the Odyssey passenger shuttle's 192 x 320 canvas.


def build() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    if abs(source.width / source.height - 3 / 5) > 0.005:
        raise ValueError(f"Expected a 3:5 source canvas, got {source.size}")

    TEXTURES.mkdir(parents=True, exist_ok=True)
    sprite = source.resize(SIZE, Image.Resampling.LANCZOS)
    sprite.save(SPRITE)
    sprite.rotate(-90, expand=True).save(MENU_ICON)

    # RimWorld CutoutComplex uses the red mask channel for DrawColor. Paint the
    # broad neutral armor only; preserve the dark cabin and yellow ram stripes.
    mask = Image.new("RGBA", SIZE, (0, 0, 0, 255))
    src = sprite.load()
    dst = mask.load()
    for y in range(SIZE[1]):
        for x in range(SIZE[0]):
            r, g, b, a = src[x, y]
            neutral = max(r, g, b) - min(r, g, b) < 30
            brightness = (r + g + b) / 3
            if a > 32 and neutral and brightness > 115:
                strength = min(255, int((brightness - 115) * 2.2))
                dst[x, y] = (strength, 0, 0, 255)
    mask.save(MASK)

    # Same single source rotated; 64 px per occupied map cell in this preview.
    north = sprite.resize((192, 320), Image.Resampling.LANCZOS)
    rotations = [north, north.rotate(-90, expand=True),
                 north.rotate(180, expand=True), north.rotate(90, expand=True)]
    panel_width = 360
    preview = Image.new("RGBA", (panel_width * 4, 400), (31, 35, 42, 255))
    for index, rotated in enumerate(rotations):
        x = index * panel_width + (panel_width - rotated.width) // 2
        y = (400 - rotated.height) // 2
        preview.alpha_composite(rotated, (x, y))
    preview.save(PREVIEW)


if __name__ == "__main__":
    build()
