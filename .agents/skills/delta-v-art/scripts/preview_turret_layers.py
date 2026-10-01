"""Compose four right-angle turret previews at approximate in-game size."""

import argparse
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base", type=Path)
    parser.add_argument("top", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--tile", type=int, default=192)
    args = parser.parse_args()
    base = Image.open(args.base).convert("RGBA")
    top = Image.open(args.top).convert("RGBA")
    if base.size != top.size or base.width != base.height:
        parser.error("base and top must have the same square canvas")

    sheet = Image.new("RGB", (args.tile * 4, args.tile), (31, 38, 45))
    for index, angle in enumerate((0, 90, 180, 270)):
        rotated = top.rotate(angle, resample=Image.Resampling.NEAREST)
        layer = Image.alpha_composite(base, rotated)
        thumb = layer.resize((args.tile, args.tile), Image.Resampling.LANCZOS)
        sheet.paste(thumb, (index * args.tile, 0), thumb)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.output)
    print(f"{args.output}: 0/90/180/270 degree previews")


if __name__ == "__main__":
    main()
