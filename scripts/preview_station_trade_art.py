"""Render Delta-V trade buildings at the current Def footprint for visual QA."""

from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
DEF_PATH = ROOT / "1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml"
OUT = ROOT / "doc/art/StationTrade_game_scale_preview.png"
CELL_PIXELS = 48


def main() -> None:
    root = ET.parse(DEF_PATH).getroot()
    defs = root.findall("ThingDef")
    entries = []
    for node in defs:
        name = node.findtext("defName")
        if name not in {"DeltaV_CargoContainer", "DeltaV_TradeTerminal"}:
            continue
        size = tuple(map(int, re.findall(r"\d+", node.findtext("size"))))
        draw_size = tuple(map(float, re.findall(r"[\d.]+", node.findtext("graphicData/drawSize"))))
        assert draw_size == size, (name, size, draw_size)
        variants = [("TradeTerminal", node.findtext("graphicData/texPath"))] if name == "DeltaV_TradeTerminal" else [
            ("LockedCargo", node.findtext("graphicData/texPath")),
            ("UnlockedCargo", root.findtext("AdaptiveStorage.GraphicsDef/graphics/li/graphicData/unlockedGraphicData/texPath")),
        ]
        for label, tex_path in variants:
            path = ROOT / "1.6/Textures" / (tex_path + ".png")
            source = Image.open(path).convert("RGBA")
            for corner in ((0, 0), (source.width - 1, 0),
                           (0, source.height - 1),
                           (source.width - 1, source.height - 1)):
                assert source.getpixel(corner)[3] == 0, (path, corner)
            bounds = source.getchannel("A").point(lambda a: 255 if a >= 128 else 0).getbbox()
            assert bounds is not None, path
            if name == "DeltaV_CargoContainer":
                assert bounds[0] < source.width * 0.015, (label, bounds)
                assert bounds[1] < source.height * 0.015, (label, bounds)
                assert bounds[2] > source.width * 0.985, (label, bounds)
                assert bounds[3] > source.height * 0.985, (label, bounds)
            center = ((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2)
            assert abs(center[0] - source.width / 2) < source.width * 0.06, (label, bounds)
            assert abs(center[1] - source.height / 2) < source.height * 0.06, (label, bounds)
            entries.append((label, size, source, bounds))

    font = ImageFont.load_default()
    width, height = 3 * 210 + 20, 445
    canvas = Image.new("RGB", (width, height), (31, 34, 39))
    draw = ImageDraw.Draw(canvas)
    for index, (name, size, source, bounds) in enumerate(entries):
        left = 15 + index * 210
        draw.text((left, 10), name, fill=(230, 230, 230), font=font)
        draw.text((left, 28), f"{size[0]}x{size[1]}  alpha>=128: {bounds}",
                  fill=(170, 180, 190), font=font)
        actual = source.resize((size[0] * CELL_PIXELS, size[1] * CELL_PIXELS),
                               Image.Resampling.LANCZOS)
        x = left + (190 - actual.width) // 2
        background = Image.new("RGBA", actual.size, (63, 68, 75, 255))
        background.alpha_composite(actual)
        canvas.paste(background.convert("RGB"), (x, 75))
        doubled = background.resize((actual.width * 2, actual.height * 2),
                                    Image.Resampling.NEAREST)
        preview_width = min(doubled.width, 190)
        cropped = doubled.crop(((doubled.width - preview_width) // 2, 0,
                                (doubled.width + preview_width) // 2,
                                min(doubled.height, 80)))
        canvas.paste(cropped.convert("RGB"), (left + (190 - preview_width) // 2, 175))
    cargo = {name: (size, source) for name, size, source, _ in entries}
    if "LockedCargo" in cargo and "UnlockedCargo" in cargo:
        draw.text((15, 270), "Adjacent 2x3 hull bays at game scale", fill=(230, 230, 230), font=font)
        for index, name in enumerate(("LockedCargo", "LockedCargo", "UnlockedCargo",
                                      "UnlockedCargo", "LockedCargo", "LockedCargo")):
            size, source = cargo[name]
            actual = source.resize((size[0] * CELL_PIXELS, size[1] * CELL_PIXELS),
                                   Image.Resampling.LANCZOS)
            x = 15 + index * actual.width
            background = Image.new("RGBA", actual.size, (63, 68, 75, 255))
            background.alpha_composite(actual)
            canvas.paste(background.convert("RGB"), (x, 290))
    canvas.save(OUT)
    print(OUT)


if __name__ == "__main__":
    main()
