"""Render every faceplate the way the app draws it - the face with each knob cap,
selector and toggle sprite at the size and place faceplates.json gives it - so a
control that sits off its printed nut or tick ring can be SEEN.

    python tools/rig-art/preview-faces.py --out <dir> [--only miku,rat] [--sheet]

Writes <dir>/<id>.png per face (cap outlines and param names in magenta on a
second copy beside the clean one) and, with --sheet, contact sheets of 6.
"""
import argparse
import json
import math
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
RIG = os.path.join(REPO, 'DotCharter.Tone.UI', 'Assets', 'Rig')


def cap_diameter(face):
    """RigFaceplate.KnobCapDiameter: 1.6x the median knob nut, clamped."""
    knobs = sorted(c['d'] for c in face['controls'] if c['kind'] not in ('toggle', 'model_window'))
    if not knobs:
        return 0.1
    return min(max(knobs[len(knobs) // 2] * 1.6, 0.035), 0.2)


def angle_for(c):
    """KnobMath: -135..+135 degrees over the range, at the default value."""
    lo, hi, v = c.get('min', 0), c.get('max', 1), c.get('default', 0.5)
    if c.get('angles'):
        return c['angles'][max(0, min(len(c['angles']) - 1, round(v - lo)))]
    t = 0.5 if hi == lo else (v - lo) / (hi - lo)
    return -135 + 270 * max(0.0, min(1.0, t))


def render(fid, face, style):
    im = Image.open(os.path.join(RIG, 'Faces', face['file'])).convert('RGBA')
    w, h = im.size
    capd = cap_diameter(face) * w
    knob = Image.open(os.path.join(RIG, 'Caps', f'knob-{style.lower()}.webp')).convert('RGBA')
    selector = Image.open(os.path.join(RIG, 'Caps', 'selector-cap.webp')).convert('RGBA')
    up = Image.open(os.path.join(RIG, 'Caps', 'toggle-up.webp')).convert('RGBA')
    clean = im.copy()
    for c in face['controls']:
        x, y = c['x'] * w, c['y'] * h
        if c['kind'] == 'model_window':
            continue
        if c['kind'] == 'toggle':
            s = c['d'] * 1.5 * w
            sprite = up.resize((max(1, round(s)), max(1, round(s))), Image.LANCZOS)
        else:
            src = selector if c['kind'] == 'rotary_selector' else knob
            sprite = src.resize((max(1, round(capd)), max(1, round(capd))), Image.LANCZOS)
            sprite = sprite.rotate(-angle_for(c), resample=Image.BICUBIC)
        clean.alpha_composite(sprite, (round(x - sprite.width / 2), round(y - sprite.height / 2)))
    marked = clean.copy()
    d = ImageDraw.Draw(marked)
    for c in face['controls']:
        x, y = c['x'] * w, c['y'] * h
        r = (c['d'] * w * (1.25 if c['kind'] == 'model_window' else 1)) / 2
        d.ellipse((x - r, y - r, x + r, y + r), outline=(255, 0, 255, 255), width=2)
        d.line((x - 6, y, x + 6, y), fill=(0, 255, 255, 255), width=1)
        d.line((x, y - 6, x, y + 6), fill=(0, 255, 255, 255), width=1)
        d.text((x - r, y + r + 2), c['param'], fill=(255, 0, 255, 255))
    if face.get('footswitch'):
        f = face['footswitch']
        x, y, r = f['x'] * w, f['y'] * h, f['d'] * w * 1.25 / 2
        d.ellipse((x - r, y - r, x + r, y + r), outline=(0, 255, 0, 255), width=2)
    out = Image.new('RGBA', (w * 2 + 20, h), (40, 40, 44, 255))
    out.alpha_composite(clean, (0, 0))
    out.alpha_composite(marked, (w + 20, 0))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--only')
    ap.add_argument('--sheet', action='store_true')
    a = ap.parse_args()
    data = json.load(open(os.path.join(RIG, 'faceplates.json'), encoding='utf-8'))
    faces, styles = data['faces'], data.get('knobStyles', {})
    only = set(a.only.split(',')) if a.only else None
    os.makedirs(a.out, exist_ok=True)
    done = []
    for fid, face in faces.items():
        if only and fid not in only:
            continue
        img = render(fid, face, styles.get(fid, 'Aluminium'))
        img.convert('RGB').save(os.path.join(a.out, f'{fid}.png'))
        done.append((fid, img))
    if a.sheet:
        cell_w, cell_h = 700, 560
        for start in range(0, len(done), 6):
            chunk = done[start:start + 6]
            sheet = Image.new('RGB', (cell_w * 3, cell_h * 2), (30, 30, 34))
            d = ImageDraw.Draw(sheet)
            for k, (fid, img) in enumerate(chunk):
                t = img.copy()
                t.thumbnail((cell_w - 10, cell_h - 24))
                ox, oy = (k % 3) * cell_w + 5, (k // 3) * cell_h + 20
                sheet.paste(t.convert('RGB'), (ox, oy))
                d.text((ox, oy - 16), fid, fill=(255, 255, 255))
            sheet.save(os.path.join(a.out, f'sheet-{start // 6 + 1:02d}.png'))
    print(f'{len(done)} face(s) rendered to {a.out}')


if __name__ == '__main__':
    main()
