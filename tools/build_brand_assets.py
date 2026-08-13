from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageEnhance


def build_assets(source_path: Path, output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)

    source = Image.open(source_path).convert("RGB")
    source.save(output_dir / "ZZZSwitch-Brand-Source.png", optimize=True)

    width, height = source.size
    side = min(width, height)
    top = max(0, min(height - side, round((height - side) * 0.35)))
    square = source.crop((0, top, side, top + side))
    square = ImageEnhance.Contrast(square).enhance(1.04)
    square.save(output_dir / "ZZZSwitch-Icon.png", optimize=True)
    square.save(
        output_dir / "ZZZSwitch.ico",
        format="ICO",
        sizes=[
            (16, 16),
            (20, 20),
            (24, 24),
            (32, 32),
            (40, 40),
            (48, 48),
            (64, 64),
            (128, 128),
            (256, 256),
        ],
    )

    wordmark_region = source.crop(
        (
            round(width * 0.09),
            round(height * 0.72),
            round(width * 0.91),
            round(height * 0.85),
        )
    )
    luminance = wordmark_region.convert("L")
    alpha = luminance.point(
        lambda value: 0
        if value <= 12
        else 255
        if value >= 220
        else round((value - 12) * 255 / 208)
    )
    wordmark = Image.new("RGBA", wordmark_region.size, (255, 255, 255, 0))
    wordmark.putalpha(alpha)
    content_box = alpha.getbbox()
    if content_box is None:
        raise RuntimeError("Could not locate the ZZZSwitch wordmark.")

    padding = 12
    left = max(0, content_box[0] - padding)
    top = max(0, content_box[1] - padding)
    right = min(wordmark.width, content_box[2] + padding)
    bottom = min(wordmark.height, content_box[3] + padding)
    wordmark.crop((left, top, right, bottom)).save(
        output_dir / "ZZZSwitch-Wordmark.png",
        optimize=True,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    build_assets(args.source, args.output_dir)


if __name__ == "__main__":
    main()
