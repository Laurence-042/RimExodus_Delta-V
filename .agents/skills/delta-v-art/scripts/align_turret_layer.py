"""Place a sprite pivot at the center of a larger transparent canvas, without resampling."""

import argparse
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--pivot-x", type=int, required=True,
                        help="rotation pivot x in the source image, in pixels")
    parser.add_argument("--pivot-y", type=int, required=True,
                        help="rotation pivot y in the source image, in pixels")
    parser.add_argument("--canvas", type=int, required=True,
                        help="even square output side length in pixels")
    args = parser.parse_args()

    if args.canvas <= 0 or args.canvas % 2:
        parser.error("--canvas must be a positive even integer")
    source = Image.open(args.source).convert("RGBA")
    if not (0 <= args.pivot_x < source.width and 0 <= args.pivot_y < source.height):
        parser.error("pivot must lie inside the source image")

    dx = args.canvas // 2 - args.pivot_x
    dy = args.canvas // 2 - args.pivot_y
    bbox = source.getchannel("A").getbbox()
    if bbox is None:
        parser.error("source has no visible pixels")
    shifted = (bbox[0] + dx, bbox[1] + dy, bbox[2] + dx, bbox[3] + dy)
    if min(shifted[:2]) < 0 or max(shifted[2:]) > args.canvas:
        parser.error(f"visible pixels would be clipped: shifted alpha bbox={shifted}")

    result = Image.new("RGBA", (args.canvas, args.canvas), (0, 0, 0, 0))
    result.paste(source, (dx, dy))  # No mask or resampling: copy each source RGBA pixel.
    if result.crop((dx, dy, dx + source.width, dy + source.height)).tobytes() != source.tobytes():
        raise RuntimeError("RGBA pixels changed during translation")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result.save(args.output)
    print(f"{args.output}: shift=({dx},{dy}), pivot=({args.canvas//2},{args.canvas//2}), "
          f"alpha_bbox={shifted}, source_rgba_unchanged=yes")


if __name__ == "__main__":
    main()
