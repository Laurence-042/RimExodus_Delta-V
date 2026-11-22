"""Keep Workshop promo still images within 1 MB; requires Python Pillow.

Run from any directory. Pass image paths to override the eight default assets.
Images already within the limit are left byte-for-byte unchanged.
"""

import argparse
from io import BytesIO
from pathlib import Path

from PIL import Image


PROMO = Path(__file__).resolve().parents[1] / "doc" / "promo"
DEFAULT_IMAGES = (
    "name.jpg", "battle.jpg", "collision.jpg", "docking.jpg",
    "bording.jpg", "drag.jpg", "eva.jpg", "mining.jpg",
)


def animation_timing(image):
    durations = []
    for index in range(image.n_frames):
        image.seek(index)
        image.load()
        durations.append(image.info.get("duration", 0))
    return durations, image.info.get("loop", 0)


def compress(path, max_bytes):
    original_size = path.stat().st_size
    with Image.open(path) as source:
        if source.format not in ("JPEG", "WEBP"):
            raise ValueError(f"Unsupported image format: {path}")
        image_format = source.format
        size = source.size
        frame_count = getattr(source, "n_frames", 1)
        durations, loop = animation_timing(source) if frame_count > 1 else ([], 0)
        if original_size <= max_bytes:
            source.load()
            print(f"KEEP {path.name}: {original_size:,} bytes", flush=True)
            return
        frames = []
        for index in range(frame_count):
            source.seek(index)
            frames.append(source.convert("RGBA" if "A" in source.getbands() else "RGB"))
        options = {"format": image_format}
        if image_format == "JPEG":
            options.update(optimize=True, progressive=True)
        else:
            options.update(method=4, lossless=False)
            if frame_count > 1:
                options.update(save_all=True, append_images=frames[1:], duration=durations,
                               loop=loop, background=source.info.get("background", (0, 0, 0, 0)))

    for quality in range(80, 0, -10):
        print(f"ENCODE {path.name}: quality={quality}, {frame_count} frame(s)", flush=True)
        output = BytesIO()
        frames[0].save(output, quality=quality, **options)
        data = output.getvalue()
        print(f"TRY  {path.name}: quality={quality}, {len(data):,} bytes", flush=True)
        if len(data) > max_bytes:
            continue
        with Image.open(BytesIO(data)) as check:
            if check.size != size or getattr(check, "n_frames", 1) != frame_count:
                raise ValueError(f"Compression changed dimensions or frame count: {path}")
            if frame_count > 1:
                if animation_timing(check) != (durations, loop):
                    raise ValueError(f"Compression changed animation timing: {path}")
            else:
                check.load()
        temporary = path.with_name(path.name + ".tmp")
        try:
            temporary.write_bytes(data)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
        print(f"SAVE {path.name}: {original_size:,} -> {len(data):,} bytes; "
              f"{size[0]}x{size[1]}, {frame_count} frame(s)", flush=True)
        return
    raise ValueError(f"Cannot meet {max_bytes:,} bytes without further quality or size changes: {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("images", nargs="*", type=Path)
    parser.add_argument("--max-bytes", type=int, default=1_000_000)
    args = parser.parse_args()
    if args.max_bytes <= 0:
        parser.error("--max-bytes must be positive")
    for path in args.images or [PROMO / name for name in DEFAULT_IMAGES]:
        compress(path, args.max_bytes)


if __name__ == "__main__":
    main()
