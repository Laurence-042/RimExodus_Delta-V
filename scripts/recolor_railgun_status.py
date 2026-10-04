"""Build CutoutComplex base sprites and red-channel masks from accepted railgun art.

RimWorld's Graphic_Single loads <texPath>_m for a shader that supports masks.
The red mask channel receives DrawColor; black retains the base texture.
"""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "1.6/Textures/Things/Building/SpaceWeapons"
REFERENCE = ROOT / "doc/art"


def build_mask(source: str, reference: str, region: tuple[int, int, int, int],
               select, brightness: float) -> None:
    base = Image.open(REFERENCE / reference).convert("RGBA")
    mask = Image.new("RGBA", base.size, (0, 0, 0, 255))
    pixels = base.load()
    masks = mask.load()
    x0, y0, x1, y1 = region
    for y in range(y0, min(y1, base.height)):
        for x in range(x0, min(x1, base.width)):
            r, g, b, a = pixels[x, y]
            if a == 0 or not select(r, g, b):
                continue
            gray = min(255, round((0.2126 * r + 0.7152 * g + 0.0722 * b) * brightness))
            pixels[x, y] = (gray, gray, gray, a)
            masks[x, y] = (255, 0, 0, 255)
    base.save(ART / f"{source}.png")
    mask.save(ART / f"{source}_m.png")


def preview() -> None:
    # Approximate the CutoutComplex red-mask blend for map-scale visual QA.
    variants = (
        ("Ready", (0.66, 0.92, 1.0)),
        ("Cooling", (1.0, 0.62, 0.20)),
        ("Unavailable", (0.72, 0.19, 0.22)),
    )
    image = Image.new("RGB", (720, 360), (31, 34, 38))
    draw = ImageDraw.Draw(image)
    for column, (name, tint) in enumerate(variants):
        x = 30 + 235 * column
        draw.text((x, 14), name, fill=(220, 220, 220))
        for row, source in enumerate(("RailgunLoader_Inner", "RailgunAccelerator_Inner")):
            state = name if row == 0 else ("Ready" if name == "Ready" else "Unavailable")
            state_tint = tint if row == 0 else (variants[0][1] if state == "Ready" else variants[2][1])
            base = Image.open(ART / f"{source}.png").convert("RGBA")
            mask = Image.open(ART / f"{source}_m.png").getchannel("R")
            pixels = base.load()
            masks = mask.load()
            for y in range(base.height):
                for px in range(base.width):
                    if masks[px, y] == 0:
                        continue
                    gray, _, _, alpha = pixels[px, y]
                    pixels[px, y] = tuple(round(gray * channel) for channel in state_tint) + (alpha,)
            sprite = base.resize((192, 96), Image.Resampling.LANCZOS)
            image.paste(sprite, (x, 40 + row * 125), sprite)
            if row == 1:
                small = base.resize((96, 48), Image.Resampling.LANCZOS)
                image.paste(small, (x, 285), small)
    image.save(REFERENCE / "Railgun_status_game_scale.png")


def main() -> None:
    build_mask("RailgunAccelerator_Inner", "RailgunAccelerator_Inner_copper_reference.png",
               (620, 190, 1160, 650),
               lambda r, g, b: r > g + 8 and g > b + 4, 1.22)
    build_mask("RailgunLoader_Inner", "RailgunLoader_Inner_screen_reference.png",
               (110, 250, 480, 550),
               lambda r, g, b: b > r + 25 and g > r + 8 and b > 65, 1.65)
    preview()


if __name__ == "__main__":
    main()
