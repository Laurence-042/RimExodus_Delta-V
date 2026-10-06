"""Build the launcher layers and render previews at the actual Def draw sizes.

The image model supplies painted surfaces; Python handles layer alignment and the
deterministic width adjustment needed for a wall-flush 1x3 launcher.
"""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
SOURCES = ROOT / "doc" / "art" / "missile"
BUILDINGS = ROOT / "1.6" / "Textures" / "Things" / "Building" / "SpaceWeapons"
PROJECTILES = ROOT / "1.6" / "Textures" / "Things" / "Projectile"


def fit_alpha(source: Path, canvas: tuple[int, int], margin: int) -> Image.Image:
    image = Image.open(source).convert("RGBA")
    bounds = image.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError(f"Empty sprite: {source}")
    image = image.crop(bounds)
    image.thumbnail(
        (canvas[0] - margin * 2, canvas[1] - margin * 2),
        Image.Resampling.LANCZOS,
    )
    result = Image.new("RGBA", canvas)
    result.alpha_composite(
        image, ((canvas[0] - image.width) // 2, (canvas[1] - image.height) // 2)
    )
    return result


def split(master: Image.Image, openings: list[tuple[int, int, int, int]]) -> tuple[Image.Image, Image.Image]:
    mask = Image.new("L", master.size)
    painter = ImageDraw.Draw(mask)
    for opening in openings:
        painter.rectangle(opening, fill=255)
    inner = Image.new("RGBA", master.size)
    outer = Image.new("RGBA", master.size)
    inside = mask.load()
    source = master.load()
    lower = inner.load()
    upper = outer.load()
    for y in range(master.height):
        for x in range(master.width):
            if inside[x, y]:
                lower[x, y] = source[x, y]
            else:
                upper[x, y] = source[x, y]
    if Image.alpha_composite(inner, outer).tobytes() != master.tobytes():
        raise AssertionError("Layer split changed source pixels")
    return inner, outer


def fill_launcher_frame(layer: Image.Image) -> Image.Image:
    """Fill the wall width and lengthen the approved shell without repainting it."""
    return layer.transform(
        layer.size,
        Image.Transform.AFFINE,
        (1 / 1.12, 0, 136.5 - 128 / 1.12,
         0, 1 / 1.11, 384 - 384 / 1.11),
        resample=Image.Resampling.BICUBIC,
    )


def draw_scaled(canvas: Image.Image, layer: Image.Image, center: tuple[int, int],
                size: tuple[float, float]) -> None:
    pixel_per_tile = 256
    scaled = layer.resize((round(size[0] * pixel_per_tile),
                           round(size[1] * pixel_per_tile)), Image.Resampling.LANCZOS)
    canvas.alpha_composite(scaled, (center[0] - scaled.width // 2,
                                    center[1] - scaled.height // 2))


def preview(name: str, inner: Image.Image, outer: Image.Image, missile: Image.Image,
            magazine: Image.Image) -> None:
    # Nominal launcher footprint: x=384..640, y=128..896. The magazine footprints
    # occupy the southern two cells to either side. North is toward image top.
    center = (512, 512 - round(0.15 * 256))
    payload_center = (512 - round(0.05 * 256), 512 - round(0.35 * 256))
    for loaded in (False, True):
        composite = Image.new("RGBA", (1024, 1024))
        draw_scaled(composite, inner, center, (1.06, 3))
        if loaded:
            draw_scaled(composite, missile, payload_center, (1.3, 1.3))
        draw_scaled(composite, outer, center, (1.06, 3))
        composite.thumbnail((512, 512), Image.Resampling.LANCZOS)
        composite.save(SOURCES / f"{name}_{'Loaded' if loaded else 'Empty'}_Preview.png")

    assembly = Image.new("RGBA", (1024, 1024))
    draw_scaled(assembly, magazine, (256, 640), (1.26, 2))
    draw_scaled(assembly, magazine, (768, 640), (1.26, 2))
    draw_scaled(assembly, inner, center, (1.06, 3))
    draw_scaled(assembly, missile, payload_center, (1.3, 1.3))
    draw_scaled(assembly, outer, center, (1.06, 3))
    assembly.thumbnail((512, 512), Image.Resampling.LANCZOS)
    assembly.save(SOURCES / "LauncherWithMagazines_Preview.png")


def main() -> None:
    BUILDINGS.mkdir(parents=True, exist_ok=True)
    PROJECTILES.mkdir(parents=True, exist_ok=True)
    missile = fit_alpha(SOURCES / "MissileHE_SimplifiedSource.png", (512, 512), 24)
    missile.save(PROJECTILES / "DeltaV_MissileHE.png")
    magazine = fit_alpha(SOURCES / "MissileMagazine_1x2_Source.png", (256, 512), 8)
    magazine.save(BUILDINGS / "MissileMagazine.png")
    magazine.resize((64, 128), Image.Resampling.LANCZOS).save(
        SOURCES / "MissileMagazine_GameScale.png"
    )
    configurations = (
        ("Large", (256, 768), [(100, 128, 155, 520), (129, 566, 221, 706)]),
    )
    for name, canvas, openings in configurations:
        master = fit_alpha(SOURCES / f"Launcher{name}_EmptySource.png", canvas, 8)
        inner, outer = split(master, openings)
        inner = fill_launcher_frame(inner)
        outer = fill_launcher_frame(outer)
        inner.save(BUILDINGS / "MissileLauncher_Inner.png")
        outer.save(BUILDINGS / "MissileLauncher_Outer.png")
        preview(f"Launcher{name}", inner, outer, missile, magazine)


if __name__ == "__main__":
    main()
