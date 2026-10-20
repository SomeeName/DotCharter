"""Export the approved RGBA master to the app's PNG and Windows ICO assets.

Requires Pillow: python -m pip install Pillow
Run from any directory: python tools/branding/build_icons.py
"""

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "DotCharter.UI" / "Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def main():
    master = Image.open(Path(__file__).with_name("app-icon-master.png"))
    assert master.mode == "RGBA", "The approved source must retain its alpha channel"
    assert master.width == master.height
    assert master.getpixel((0, 0))[3] == 0
    icon = master.resize((256, 256), Image.Resampling.LANCZOS)
    icon.save(ASSETS / "app-icon.png")
    icon.save(ASSETS / "app-icon.ico", sizes=[(s, s) for s in SIZES])
    with Image.open(ASSETS / "app-icon.ico") as exported:
        assert exported.ico.sizes() == {(s, s) for s in SIZES}
        for size in SIZES:
            frame = exported.ico.getimage((size, size))
            assert frame.mode == "RGBA"
            assert frame.getpixel((0, 0))[3] == 0

    # Contact sheet is review output only: actual sizes and enlarged 16/32px.
    preview = Image.new("RGB", (720, 420), "#171320")
    draw = ImageDraw.Draw(preview)
    preview.paste(icon, (24, 24), icon)
    draw.text((24, 292), "DotCharter / pick + chart highway", fill="#ffbe57")
    for i, size in enumerate((16, 24, 32, 48, 64)):
        thumb = master.resize((size, size), Image.Resampling.LANCZOS)
        x = 318 + i * 76
        preview.paste(thumb, (x, 30), thumb)
        draw.text((x, 102), f"{size}px", fill="#e8e2ed")
    for i, size in enumerate((16, 32)):
        thumb = master.resize((size, size), Image.Resampling.LANCZOS)
        large = thumb.resize((128, 128), Image.Resampling.NEAREST)
        x = 330 + i * 170
        preview.paste(large, (x, 162), large)
        draw.text((x, 304), f"{size}px enlarged", fill="#e8e2ed")
    target = ROOT / "artifacts" / "app-icon-preview.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    preview.save(target)
    print(f"Verified {len(SIZES)} RGBA ICO frames; preview: {target}")


if __name__ == "__main__":
    main()
