"""Render north-facing rail capacitor slot previews from the live Def geometry."""

from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
DEFS = ET.parse(ROOT / "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml").getroot()
TEXTURES = ROOT / "1.6/Textures/Things/Building/SpaceWeapons"
OUT = ROOT / "doc/art"


def numbers(value):
    return tuple(float(part) for part in re.findall(r"-?\d+(?:\.\d+)?", value))


def graphic(name, size):
    image = Image.open(TEXTURES / f"{name}.png").convert("RGBA")
    return image.resize(size, Image.Resampling.LANCZOS)


def render(with_shell):
    canvas = Image.new("RGBA", (1500, 650), (24, 26, 30, 255))
    for name, origin_x in (("DeltaV_Weapon_RailgunAccelerator", 60),
                           ("DeltaV_Weapon_RailgunAcceleratorLarge", 800)):
        definition = next(d for d in DEFS.findall("ThingDef")
                          if d.findtext("defName") == name)
        width, depth = map(int, numbers(definition.findtext("size")))
        props = definition.find("comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']")
        tile = 640 / width
        assert round(depth * tile) == 320
        min_x = -((width - 1) // 2)  # GenAdj.OccupiedRect, North
        min_z = -((depth - 1) // 2)
        inner = graphic("RailgunAccelerator_Inner", (640, 320))
        canvas.alpha_composite(inner, (origin_x, 260))
        centers = []
        for slot in props.findall("attachmentSlots/li"):
            cell_x, _, cell_z = numbers(slot.findtext("cell"))
            offset_x, offset_z = numbers(slot.findtext("drawOffset"))
            scale = float(slot.findtext("drawScale"))
            assert min_x <= cell_x < min_x + width
            assert min_z <= cell_z < min_z + depth
            center_x = origin_x + (cell_x - min_x + 0.5 + offset_x) * tile
            center_y = 260 + (depth - (cell_z - min_z) - 0.5 - offset_z) * tile
            centers.append((center_x, center_y))
            pixels = round(scale * tile)
            capacitor = graphic("RailgunCapacitor", (pixels, pixels))
            canvas.alpha_composite(capacitor,
                                   (round(center_x - pixels / 2), round(center_y - pixels / 2)))
        assert len(centers) == 2
        assert abs(centers[0][0] + centers[1][0] - 2 * (origin_x + 320)) < 0.01
        assert abs(centers[0][1] - centers[1][1]) < 0.01
        if with_shell:
            shell = graphic("RailgunShell", (640, 320))
            canvas.alpha_composite(shell, (origin_x, 100))  # center on north edge
        label = "2x1" if width == 2 else "4x2"
        ImageDraw.Draw(canvas).text((origin_x, 610), label, fill="white")
    path = OUT / ("RailgunCapacitor_layer_preview.png" if with_shell
                  else "RailgunCapacitor_slot_preview.png")
    canvas.save(path)
    print(path)


if __name__ == "__main__":
    render(False)
    render(True)
