"""Compose menu and blueprint graphics from existing finished-building layers."""

from pathlib import Path
import sys

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "1.6/Textures/Things/Building/SpaceWeapons"
SIZE = 256


def layer(name):
    return Image.open(ART / f"{name}.png").convert("RGBA")


def centered(canvas, image):
    canvas.alpha_composite(image, ((canvas.width - image.width) // 2,
                                   (canvas.height - image.height) // 2))


def turret_icon(base, top, top_scale, output):
    icon = layer(base).resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    width = round(SIZE * top_scale)
    rotating_top = layer(top).resize((width, width), Image.Resampling.LANCZOS)
    # TurretTop.ArtworkRotation=-90: the source art points right, north-facing top points up.
    centered(icon, rotating_top.rotate(90))
    icon.save(ART / f"{output}.png")


def small_shield_icon():
    icon = layer("PointDefense_Base").resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    width = round(SIZE * 1.2 / 1.3)
    # ShieldProjector already faces north, matching CompSpaceShield.ShellTopAngle.
    centered(icon, layer("ShieldProjector").resize((width, width), Image.Resampling.LANCZOS))
    icon.save(ART / "ShieldGeneratorSmall_MenuIcon.png")


def rail_icon(inner, output):
    icon = Image.new("RGBA", (SIZE, SIZE))
    inner_image = layer(inner).resize((SIZE, SIZE // 2), Image.Resampling.LANCZOS)
    shell = layer("RailgunShell").resize((SIZE, SIZE // 2), Image.Resampling.LANCZOS)
    # Rail shell center is at the inner layer's north edge, not the part center.
    icon.alpha_composite(inner_image, (0, SIZE // 4))
    icon.alpha_composite(shell, (0, 0))
    icon.save(ART / f"{output}.png")


def rail_loader_icon():
    # Blueprint footprint is 3×2; the 3×1 shell is centered at the footprint's north edge.
    width = 384
    tile = width // 3
    icon = Image.new("RGBA", (width, 2 * tile))
    for name, top in (("RailgunLoader_Inner", tile // 2), ("RailgunLoader_Shell", 0)):
        sprite = layer(name).resize((width, tile), Image.Resampling.LANCZOS)
        icon.alpha_composite(sprite, (0, top))
    icon.save(ART / "RailgunLoader_MenuIcon.png")


def missile_blueprint():
    # World dimensions and offsets match the launcher Def, including its
    # 0.15-cell north overhang and the loaded projectile's separate center.
    pixels_per_cell = 240
    canvas = Image.new("RGBA", (336, 816))  # drawSize=(1.4, 3.4)

    def paste(name, width, height, x, z):
        part = layer(name).resize((round(width * pixels_per_cell),
                                   round(height * pixels_per_cell)),
                                  Image.Resampling.LANCZOS)
        left = round(canvas.width / 2 + x * pixels_per_cell - part.width / 2)
        top = round(canvas.height / 2 - z * pixels_per_cell - part.height / 2)
        canvas.alpha_composite(part, (left, top))

    paste("MissileLauncher_Inner", 1.06, 3, 0, 0.15)
    missile = Image.open(ROOT / "1.6/Textures/Things/Projectile/DeltaV_MissileHE.png").convert("RGBA")
    part = missile.resize((312, 312), Image.Resampling.LANCZOS)
    canvas.alpha_composite(part, (round(canvas.width / 2 - 0.05 * pixels_per_cell - 156),
                                  round(canvas.height / 2 - 0.35 * pixels_per_cell - 156)))
    paste("MissileLauncher_Outer", 1.06, 3, 0, 0.15)
    canvas.save(ART / "MissileLauncher_Blueprint.png")


if __name__ == "__main__":
    rail_loader_icon()
    if sys.argv[1:] == ["--rail-loader"]:
        sys.exit(0)
    small_shield_icon()
    turret_icon("PointDefense_Base", "LaserTurret_Top", 1.2 / 1.3,
                "PointDefense_MenuIcon")
    for suffix, top in (("Cannon", "CannonTurret_Top"),
                        ("Rapid", "RapidTurret_Top"),
                        ("Laser", "LaserTurret_Top")):
        turret_icon("Turret2_Base", top, 2.4 / 2.6, f"Turret2_{suffix}_MenuIcon")
    for suffix, top in (("Cannon", "CannonTurret_Top"),
                        ("Rapid", "RapidTurret_Top"),
                        ("Laser", "LaserTurret_Top")):
        turret_icon("Turret3_Base", top, 3.4 / 3.6, f"Turret3_{suffix}_MenuIcon")
    rail_icon("RailgunAccelerator_Inner", "RailgunAccelerator_MenuIcon")
    missile_blueprint()
