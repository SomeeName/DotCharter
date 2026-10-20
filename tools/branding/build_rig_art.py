"""Resize the approved RGBA rig artwork to embedded UI assets (Pillow required)."""

from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).with_name("rig")
TARGET = ROOT / "DotCharter.UI" / "Assets" / "Rig"


def main():
    TARGET.mkdir(parents=True, exist_ok=True)
    for name, width in (("amp-chassis", 1600), ("speaker-cabinet", 256)):
        with Image.open(SOURCE / f"{name}-master.png") as source:
            assert source.mode == "RGBA", "Generated art must retain alpha"
            assert source.getpixel((0, 0))[3] == 0, "Do not flatten transparency"
            height = round(source.height * width / source.width)
            target = TARGET / f"{name}.png"
            source.resize((width, height), Image.Resampling.LANCZOS).save(
                target, optimize=True
            )
        with Image.open(target) as result:
            assert result.mode == "RGBA"
            assert result.size == (width, height)
            assert result.getpixel((0, 0))[3] == 0
            print(f"{target}: {width}x{height}, {target.stat().st_size:,} bytes, RGBA")


if __name__ == "__main__":
    main()
