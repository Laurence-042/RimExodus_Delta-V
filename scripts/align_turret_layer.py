"""Translate an RGBA sprite to its mechanical pivot without changing pixels."""
import argparse
from pathlib import Path
from PIL import Image


def align(source, output, pivot_x, pivot_y, canvas):
    sprite = Image.open(source).convert("RGBA")
    dx, dy = canvas // 2 - pivot_x, canvas // 2 - pivot_y
    bounds = sprite.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("Empty sprite")
    left, top, right, bottom = bounds
    if min(left + dx, top + dy) < 0 or max(right + dx, bottom + dy) > canvas:
        raise ValueError("Canvas would clip visible pixels")
    result = Image.new("RGBA", (canvas, canvas))
    visible = sprite.crop(bounds)
    result.paste(visible, (left + dx, top + dy))
    assert result.crop((left + dx, top + dy, right + dx, bottom + dy)).tobytes() == visible.tobytes()
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    result.save(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--pivot-x", type=int, required=True)
    parser.add_argument("--pivot-y", type=int, required=True)
    parser.add_argument("--canvas", type=int, required=True)
    args = parser.parse_args()
    align(args.source, args.output, args.pivot_x, args.pivot_y, args.canvas)
