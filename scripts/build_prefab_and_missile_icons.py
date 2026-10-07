"""Build deterministic UI art for the prefab pawn marker and missile launcher."""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1] / "1.6" / "Textures"
BUILDINGS = ROOT / "Things" / "Building"
WEAPONS = BUILDINGS / "SpaceWeapons"


def pawn_marker() -> None:
    scale = 4
    icon = Image.new("RGBA", (128 * scale, 128 * scale))
    draw = ImageDraw.Draw(icon)

    def box(coords):
        return tuple(value * scale for value in coords)

    draw.polygon(
        [(64 * scale, 5 * scale), (120 * scale, 64 * scale),
         (64 * scale, 123 * scale), (8 * scale, 64 * scale)],
        fill=(22, 31, 39, 255), outline=(7, 11, 15, 255), width=5 * scale,
    )
    draw.polygon(
        [(64 * scale, 15 * scale), (109 * scale, 64 * scale),
         (64 * scale, 113 * scale), (19 * scale, 64 * scale)],
        outline=(58, 157, 180, 255), width=5 * scale,
    )
    draw.line([(25 * scale, 64 * scale), (103 * scale, 64 * scale)],
              fill=(47, 78, 91, 255), width=3 * scale)
    draw.line([(64 * scale, 23 * scale), (64 * scale, 104 * scale)],
              fill=(47, 78, 91, 255), width=3 * scale)
    draw.ellipse(box((49, 36, 79, 66)), fill=(205, 221, 223, 255),
                 outline=(8, 14, 18, 255), width=3 * scale)
    draw.polygon(
        [(42 * scale, 92 * scale), (47 * scale, 74 * scale),
         (57 * scale, 68 * scale), (71 * scale, 68 * scale),
         (81 * scale, 74 * scale), (86 * scale, 92 * scale)],
        fill=(184, 207, 212, 255), outline=(8, 14, 18, 255),
    )
    draw.line([(51 * scale, 81 * scale), (77 * scale, 81 * scale)],
              fill=(58, 157, 180, 255), width=5 * scale)
    draw.polygon(
        [(64 * scale, 109 * scale), (56 * scale, 101 * scale),
         (64 * scale, 104 * scale), (72 * scale, 101 * scale)],
        fill=(86, 191, 207, 255),
    )

    target = BUILDINGS / "Prefab" / "PawnMarker.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    icon.resize((128, 128), Image.Resampling.LANCZOS).save(target)


def missile_launcher() -> None:
    inner = Image.open(WEAPONS / "MissileLauncher_Inner.png").convert("RGBA")
    outer = Image.open(WEAPONS / "MissileLauncher_Outer.png").convert("RGBA")
    missile = Image.open(ROOT / "Things" / "Projectile" / "DeltaV_MissileHE.png").convert("RGBA")

    assembly = Image.new("RGBA", inner.size)
    assembly.alpha_composite(inner)
    # The world layer uses drawSize=(1.3,1.3), drawOffset=(-0.05,0.35).
    # This source canvas is 256 px/tile along the launcher axis.
    missile_size = round(1.3 * 256)
    missile = missile.resize((missile_size, missile_size), Image.Resampling.LANCZOS)
    x = round((assembly.width - missile_size) / 2 - 0.05 * 256)
    y = round((assembly.height - missile_size) / 2 - 0.35 * 256)
    assembly.alpha_composite(missile, (x, y))
    assembly.alpha_composite(outer)

    # Designator_Build draws ThingDef icons using graphicData.drawSize=(1.06,3)
    # even when uiIconPath names a separate texture. Precompress the 1x3 image
    # into a square; the vanilla icon rect restores its world aspect ratio.
    icon = Image.new("RGBA", (256, 256))
    fitted = assembly.resize((240, 240), Image.Resampling.LANCZOS)
    icon.alpha_composite(fitted, (8, 8))
    icon.save(WEAPONS / "MissileLauncher_MenuIcon.png")


if __name__ == "__main__":
    pawn_marker()
    missile_launcher()
