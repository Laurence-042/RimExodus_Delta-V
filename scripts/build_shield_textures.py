"""Build the selected shield art from approved concept sheets.

Only deterministic alpha cropping, scaling and centering are performed here.
"""

from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
CONCEPTS = ROOT / "doc" / "art"
OUTPUT = ROOT / "1.6" / "Textures" / "Things" / "Building" / "SpaceWeapons"


def alpha_bbox(image: Image.Image, threshold: int = 16):
    alpha = image.getchannel("A")
    return alpha.point(lambda value: 255 if value > threshold else 0).getbbox()


def build_generator():
    source = Image.open(CONCEPTS / "ShieldGenerator_LayersSource.png").convert("RGBA")
    if source.width != 1774 or source.height != 887:
        raise ValueError("Unexpected shield layer sheet dimensions")
    parts = {}
    for name, region in (("ShieldGenerator", (0, 0, 900, 887)),
                         ("ShieldProjector", (900, 0, 1774, 887))):
        part = source.crop(region)
        bounds = alpha_bbox(part)
        if bounds is None:
            raise ValueError(f"{name} has no visible pixels")
        cropped = part.crop(bounds)
        cropped.thumbnail((240, 240), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (256, 256))
        canvas.alpha_composite(cropped,
                               ((256 - cropped.width) // 2, (256 - cropped.height) // 2))
        path = OUTPUT / f"{name}.png"
        canvas.save(path)
        parts[name] = canvas
        print(f"{path}: source bounds={bounds}, result bounds={alpha_bbox(canvas)}")
    icon = parts["ShieldGenerator"].copy()
    top = parts["ShieldProjector"].resize((239, 239), Image.Resampling.LANCZOS)
    icon.alpha_composite(top, ((256 - top.width) // 2, (256 - top.height) // 2))
    icon.save(OUTPUT / "ShieldGenerator_MenuIcon.png")


def build_field():
    source = Image.open(CONCEPTS / "ShieldConcept_C.png").convert("RGBA")
    # The approved field is the right-hand graphic. Use its middle section so
    # its illuminated front edge is uniform across arbitrary shield lengths.
    strip = source.crop((1050, 340, 1550, 545))
    bounds = alpha_bbox(strip)
    if bounds is None or bounds[0] > 0 or bounds[2] < strip.width:
        raise ValueError("Field strip must fill both horizontal edges")
    strip = strip.crop((0, bounds[1], strip.width, bounds[3]))
    strip = strip.resize((512, 128), Image.Resampling.LANCZOS)
    path = OUTPUT / "ShieldField.png"
    strip.save(path)
    print(f"{path}: source vertical bounds={bounds}, result bounds={alpha_bbox(strip)}")


if __name__ == "__main__":
    OUTPUT.mkdir(parents=True, exist_ok=True)
    build_generator()
    build_field()
