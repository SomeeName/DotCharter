"""Turns the raw README shots of Capture-ReadmeMedia.ps1 into docs/media/*.webp.

Usage: python tools/readme-media/Edit-ReadmeMedia.py <raw-dir> docs/media

Each raw shot is a 1920x1080 window. A shot is cropped (or not), scaled, given rounded corners and
a soft drop shadow on a transparent margin, and saved as WebP. The Play Mode picture is one frame of
a burst (play-NN.png): PLAY_FRAME picks it; choose one with 0 misses. Needs Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

PLAY_FRAME = 15
MARGIN = 28

# output name: (raw shot, crop box in raw pixels or None for the whole window, output width, corner radius)
SHOTS = {
    "hero": ("hero", None, 1600, 22),
    "ai-chat": ("chat", None, 960, 14),
    "seven-string": ("seven", None, 960, 14),
    "library": ("library", None, 960, 14),
    "song-select": ("songselect", None, 960, 14),
    "play": (f"play-{PLAY_FRAME:02d}", None, 960, 14),
    "rig": ("rig", None, 960, 14),
    # The Inspector's left half: the tool rail and the active Sound-tab page.
    "bends": ("bend", (90, 553, 690, 1033), 480, 12),
    "techniques": ("technique", (90, 553, 690, 1033), 480, 12),
    "chord-shapes": ("chords", (90, 553, 690, 1033), 480, 12),
}


def framed(shot: Image.Image, radius: int) -> Image.Image:
    w, h = shot.size
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)
    size = (w + 2 * MARGIN, h + 2 * MARGIN)
    shadow = Image.new("L", size, 0)
    ImageDraw.Draw(shadow).rounded_rectangle((MARGIN, MARGIN + 6, MARGIN + w, MARGIN + h + 6), radius=radius, fill=110)
    shadow = shadow.filter(ImageFilter.GaussianBlur(12))
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    out.putalpha(shadow)
    body = shot.convert("RGBA")
    body.putalpha(mask)
    out.alpha_composite(body, (MARGIN, MARGIN))
    return out


def main(raw: Path, dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    total = 0
    for name, (source, box, width, radius) in SHOTS.items():
        shot = Image.open(raw / f"{source}.png").convert("RGB")
        if box:
            shot = shot.crop(box)
        height = round(shot.height * width / shot.width)
        shot = shot.resize((width, height), Image.LANCZOS)
        path = dest / f"{name}.webp"
        framed(shot, radius).save(path, "WEBP", quality=86, method=6)
        total += path.stat().st_size
        print(f"{path.name:20} {width + 2 * MARGIN}x{height + 2 * MARGIN} {path.stat().st_size // 1024} KB")
    print(f"total {total // 1024} KB")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(Path(sys.argv[1]), Path(sys.argv[2]))
