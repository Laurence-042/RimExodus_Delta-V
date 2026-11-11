"""Build a translucent vacuum-barrier atlas for Verse.MaterialAtlasPool."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


TILE = 192
PADDING = 32
SLOT = TILE + 2 * PADDING
FRAME = 16
NORTH, EAST, SOUTH, WEST = 1, 2, 4, 8


def draw_tile(mask):
    tile = Image.new("RGBA", (TILE, TILE), (115, 169, 222, 88))
    draw = ImageDraw.Draw(tile)
    for start in (42, 139):
        draw.rectangle((0, start, TILE - 1, start + 9), fill=(146, 195, 233, 97))

    rim = Image.new("RGBA", tile.size)
    draw = ImageDraw.Draw(rim)
    draw.rectangle((0, 0, TILE - 1, FRAME - 1), fill=(27, 29, 32, 255))
    draw.rectangle((0, 3, TILE - 1, 12), fill=(86, 89, 94, 255))
    draw.rectangle((0, 3, TILE - 1, 5), fill=(151, 155, 160, 255))
    draw.rectangle((0, 10, TILE - 1, 11), fill=(117, 178, 229, 255))
    for side in (NORTH, EAST, SOUTH, WEST):
        if not mask & side:
            tile.alpha_composite(rim)
        rim = rim.transpose(Image.Transpose.ROTATE_270)

    corner = Image.new("RGBA", tile.size)
    draw = ImageDraw.Draw(corner)
    draw.rectangle((0, 0, FRAME - 1, FRAME - 1), fill=(27, 29, 32, 255))
    draw.polygon([(3, 3), (12, 3), (12, 8), (8, 12), (3, 12)], fill=(151, 155, 160, 255))
    for sides in (NORTH | WEST, NORTH | EAST, SOUTH | EAST, SOUTH | WEST):
        if not mask & sides:
            tile.alpha_composite(corner)
        corner = corner.transpose(Image.Transpose.ROTATE_270)
    return tile


def pad(tile):
    padded = Image.new("RGBA", (SLOT, SLOT))
    padded.paste(tile, (PADDING, PADDING))
    # Duplicate the edge pixels into the UV gutter so bilinear sampling preserves seams.
    nearest = Image.Resampling.NEAREST
    padded.paste(tile.crop((0, 0, 1, TILE)).resize((PADDING, TILE), nearest), (0, PADDING))
    padded.paste(tile.crop((TILE - 1, 0, TILE, TILE)).resize((PADDING, TILE), nearest), (PADDING + TILE, PADDING))
    for edge, y in ((0, 0), (TILE - 1, PADDING + TILE)):
        strip = padded.crop((0, PADDING + edge, SLOT, PADDING + edge + 1))
        padded.paste(strip.resize((SLOT, PADDING), nearest), (0, y))
    return padded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1]
                        / "1.6/Textures/Things/Building/SpaceWeapons")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    atlas = Image.new("RGBA", (4 * SLOT, 4 * SLOT))
    for mask in range(16):
        tile = draw_tile(mask)
        # Unity UV row zero is at the bottom. MaterialAtlasPool takes the middle 192 px of each slot.
        atlas.paste(pad(tile), (mask % 4 * SLOT, (3 - mask // 4) * SLOT))
    atlas.save(args.output / "VacuumBarrier_Atlas.png", optimize=True)
    draw_tile(0).save(args.output / "VacuumBarrier.png", optimize=True)


if __name__ == "__main__":
    main()
