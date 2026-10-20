"""Import the generated rig faceplates into the app.

For every module in rig-art-spec.json:
  1. cut the unit out of its (near-uniform, near-black) background: grow the background
     inward from the image border through pixels that are both close to the border colour
     and flat (no edge), so a black pedal on a black field still stops at its bevel;
  2. find the bare control NUTS the art prints where each knob/switch goes — two
     detectors, merged: a template for a dark hole inside a bright ring, and silver
     hexagon-ish blobs (a nut drawn without a visible hole);
  3. match them to the module's on-face controls: fit the spec layout onto the
     candidates (scale + offset per axis, iterated, trimmed), assign by minimum total
     distance, and refuse a match that sits too far from where the fitted layout says;
  4. apply overrides (tools/rig-art/faceplate-overrides.json) for faces the detector
     cannot read, then write Assets/Rig/Faces/<art>-face.webp (WebP with alpha, cropped
     to the unit) and its entry in Assets/Rig/faceplates.json (positions are fractions of
     the written image; d = nut diameter as a fraction of its width).
The knob-cap / switch / LED sprites are copied to Assets/Rig/Caps/ as WebP.

Review sheets (every control circled and named, on a BRIGHT ground so a fringe shows)
go to --review. Look at them before committing: a wrong match is a knob that turns the
wrong parameter.

  python tools/rig-art/import-faceplates.py --src <rig-art-spec dir> [--only id,id] [--review <dir>]
"""
import argparse, json, os
import cv2
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
ASSETS = os.path.join(REPO, 'DotCharter.Tone.UI', 'Assets', 'Rig')
OVERRIDES = os.path.join(HERE, 'faceplate-overrides.json')
# Where each selector's positions are printed, per value (selector-angles.json).
ANGLES = os.path.join(HERE, 'selector-angles.json')
# Placement corrections in FACE coordinates, applied last (face-corrections.json).
CORRECTIONS = os.path.join(HERE, 'face-corrections.json')
# The widest a face is stored. Faces are kept at (up to) the generated art's own
# resolution: the rig draws the selected gear BIG, on high-DPI screens too, and a
# face stored smaller than it is drawn is upscaled - the printed scales and labels
# came out blurry at 560 / 900 px (owner, 2026-09-27).
FACE_MAX_WIDTH = {'stomp': 1024, 'treadle': 1024, 'junction': 1280, 'head': 1600, 'rack': 1600, 'cab': 1024}
# The face is written as a LOSSLESS master; tools/rig-art/compress-faces.py then
# re-encodes it lossy with sharp RGB->YUV (#407), which keeps coloured print on its
# edges where Pillow's plain lossy encode smeared it (the NAM amp's red scales measured
# 32 dB PSNR even at quality 100, 2026-09-27). Run compress-faces.py after importing.
WEBP_LOSSLESS = True
MAX_MISS = 0.075  # a control further than this (fraction of width) from the fitted layout is unmatched

# ---------------------------------------------------------------------------------------
# 1. background removal


def cut_out(rgba):
    alpha = rgba[..., 3]
    if alpha[:4, :4].mean() < 20 and alpha[-4:, -4:].mean() < 20:
        return alpha
    bgr = cv2.cvtColor(rgba, cv2.COLOR_RGBA2BGR)
    h, w = bgr.shape[:2]
    lab = cv2.cvtColor(bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    border = np.concatenate([lab[:4].reshape(-1, 3), lab[-4:].reshape(-1, 3),
                             lab[:, :4].reshape(-1, 3), lab[:, -4:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    spread = np.percentile(np.linalg.norm(border - bg, axis=1), 95)
    dist = np.linalg.norm(lab - bg, axis=2)
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32)
    grad = cv2.magnitude(cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3), cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3))
    passable = ((dist < max(6.0, spread * 1.6 + 3)) & (grad < 40)).astype(np.uint8)
    n, labels = cv2.connectedComponents(passable, connectivity=4)
    edge = set(np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))) - {0}
    fg = np.where(np.isin(labels, list(edge)), 0, 255).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    n, lab2, stats, _ = cv2.connectedComponentsWithStats(fg)
    if n <= 1:
        return np.full((h, w), 255, np.uint8)
    fg = np.where(lab2 == 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA])), 255, 0).astype(np.uint8)
    contours, _ = cv2.findContours(fg, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    solid = np.zeros_like(fg)
    cv2.drawContours(solid, contours, -1, 255, cv2.FILLED)
    return cv2.GaussianBlur(solid, (3, 3), 0)


def boxed(w, h, box):
    """A rounded-rectangle cut-out for a face whose background the automatic cut cannot
    separate (a vignette in the unit's own colour)."""
    x0, y0, x1, y1 = int(box[0] * w), int(box[1] * h), int(box[2] * w), int(box[3] * h)
    mask = np.zeros((h, w), np.uint8)
    r = int((x1 - x0) * 0.05)
    cv2.rectangle(mask, (x0 + r, y0), (x1 - r, y1), 255, cv2.FILLED)
    cv2.rectangle(mask, (x0, y0 + r), (x1, y1 - r), 255, cv2.FILLED)
    for cx, cy in ((x0 + r, y0 + r), (x1 - r, y0 + r), (x0 + r, y1 - r), (x1 - r, y1 - r)):
        cv2.circle(mask, (cx, cy), r, 255, cv2.FILLED)
    return cv2.GaussianBlur(mask, (3, 3), 0)


# ---------------------------------------------------------------------------------------
# 2. nut candidates


def _ring_template(r):
    size = int(r * 5) | 1
    c = size // 2
    yy, xx = np.mgrid[:size, :size]
    d = np.hypot(yy - c, xx - c)
    t = np.zeros((size, size), np.float32)
    t[d <= r] = -1.0
    t[(d > r * 1.25) & (d <= r * 2.2)] = 1.0
    return t, c


def _is_metal(sat, val):
    """A nut is chrome: grey (low saturation) and shaded (bright AND dark across its
    faces). Printed text fails one or the other — coloured ink, or a flat white fill —
    which is what keeps an "O" or an "A" in a label from reading as a nut."""
    if sat.size < 12:
        return False
    grey = np.mean(sat < 70)
    return grey > 0.55 and np.mean(val > 100) > 0.4 and np.std(val) > 18


def candidates(rgb, alpha):
    """[(x, y, diameter, score)] in pixels."""
    h, w = alpha.shape
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    S, V = hsv[..., 1], hsv[..., 2]
    yy, xx = np.mgrid[:h, :w]
    found = []
    g = gray.astype(np.float32)
    for r in np.geomspace(w * 0.0035, w * 0.03, 18):
        t, c = _ring_template(r)
        res = cv2.matchTemplate(g, t, cv2.TM_CCOEFF_NORMED)
        ys, xs = np.where(res > 0.55)
        for y, x in zip(ys, xs):
            cx, cy = x + c, y + c
            y0, y1, x0, x1 = int(cy - r * 2.3), int(cy + r * 2.3) + 1, int(cx - r * 2.3), int(cx + r * 2.3) + 1
            if y0 < 0 or x0 < 0 or y1 > h or x1 > w:
                continue
            d = np.hypot(yy[y0:y1, x0:x1] - cy, xx[y0:y1, x0:x1] - cx)
            ring = (d > r * 1.25) & (d <= r * 2.2)
            if _is_metal(S[y0:y1, x0:x1][ring], V[y0:y1, x0:x1][ring]):
                found.append((float(res[y, x]), float(cx), float(cy), float(r * 4.4)))
    # a nut drawn SOLID (a bolt head, a toggle's mount): a bright chrome disc on darker paint
    for r in np.geomspace(w * 0.006, w * 0.03, 14):
        size = int(r * 3.4) | 1
        c = size // 2
        yy2, xx2 = np.mgrid[:size, :size]
        dd = np.hypot(yy2 - c, xx2 - c)
        t = np.zeros((size, size), np.float32)
        t[dd <= r] = 1.0
        t[(dd > r * 1.2) & (dd <= r * 1.65)] = -1.0
        res = cv2.matchTemplate(g, t, cv2.TM_CCOEFF_NORMED)
        ys, xs = np.where(res > 0.6)
        for y, x in zip(ys, xs):
            cx, cy = x + c, y + c
            y0, y1, x0, x1 = int(cy - r), int(cy + r) + 1, int(cx - r), int(cx + r) + 1
            if y0 < 0 or x0 < 0 or y1 > h or x1 > w:
                continue
            disc = np.hypot(yy[y0:y1, x0:x1] - cy, xx[y0:y1, x0:x1] - cx) <= r
            if _is_metal(S[y0:y1, x0:x1][disc], V[y0:y1, x0:x1][disc]):
                found.append((float(res[y, x]) * 0.95, float(cx), float(cy), float(r * 2.6)))
    # silver blobs: a nut drawn solid (no visible hole)
    silver = ((S < 70) & (V > 115) & (alpha > 128)).astype(np.uint8) * 255
    silver = cv2.morphologyEx(silver, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(silver)
    for i in range(1, n):
        x, y, bw, bh, area = stats[i]
        if not (w * 0.016 <= bw <= w * 0.11) or not 0.75 < bw / bh < 1.33:
            continue
        fill = area / float(bw * bh)
        box = (slice(y, y + bh), slice(x, x + bw))
        if 0.35 < fill < 0.95 and _is_metal(S[box].ravel(), V[box].ravel()):
            found.append((0.5 + 0.2 * min(fill, 0.8), x + bw / 2.0, y + bh / 2.0, float(max(bw, bh))))
    found.sort(reverse=True)
    kept = []
    for s, x, y, d in found:
        if all(np.hypot(x - kx, y - ky) > max(d, kd) * 0.5 for _, kx, ky, kd in kept):
            kept.append((s, x, y, d))
    return [(x, y, d, s) for s, x, y, d in kept]


# ---------------------------------------------------------------------------------------
# 3. matching


def hungarian(cost):
    n, m = cost.shape
    INF = 1e18
    u, v = [0.0] * (n + 1), [0.0] * (m + 1)
    p, way = [0] * (m + 1), [0] * (m + 1)
    for i in range(1, n + 1):
        p[0], j0 = i, 0
        minv, used = [INF] * (m + 1), [False] * (m + 1)
        while True:
            used[j0] = True
            i0, delta, j1 = p[j0], INF, 0
            for j in range(1, m + 1):
                if not used[j]:
                    cur = cost[i0 - 1][j - 1] - u[i0] - v[j]
                    if cur < minv[j]:
                        minv[j], way[j] = cur, j0
                    if minv[j] < delta:
                        delta, j1 = minv[j], j
            for j in range(m + 1):
                if used[j]:
                    u[p[j]] += delta
                    v[j] -= delta
                else:
                    minv[j] -= delta
            j0 = j1
            if p[j0] == 0:
                break
        while True:
            j1 = way[j0]
            p[j0] = p[j1]
            j0 = j1
            if j0 == 0:
                break
    res = [-1] * n
    for j in range(1, m + 1):
        if p[j]:
            res[p[j] - 1] = j - 1
    return res


def match(spec_pts, spec_sizes, cands, w, h):
    """Returns (assign: candidate index or -1 per control, worst miss as fraction of w)."""
    S = np.array(spec_pts, float) * [w, h]
    C = np.array([(c[0], c[1]) for c in cands], float)
    D = np.array([c[2] for c in cands], float)
    Z = np.array(spec_sizes, float)[:, None] * w
    size_ok = (D[None, :] >= Z * 0.28) & (D[None, :] <= Z * 1.15)
    if len(C) == 0:
        return [-1] * len(S), 1.0
    starts = [(1.0, 0.0, 1.0, 0.0)]
    for k in (0, 1):
        pass
    if np.ptp(S[:, 0]) > 1 and np.ptp(S[:, 1]) >= 0:
        sx = np.clip(np.ptp(C[:, 0]) / np.ptp(S[:, 0]), 0.6, 1.8) if np.ptp(S[:, 0]) > 1 else 1.0
        sy = np.clip(np.ptp(C[:, 1]) / max(np.ptp(S[:, 1]), 1), 0.6, 1.8) if np.ptp(S[:, 1]) > 1 else 1.0
        starts += [(1.0, 0.0, 1.35, h * -0.02), (1.0, 0.0, 1.2, 0.0),
                   (sx, C[:, 0].min() - sx * S[:, 0].min(), sy, C[:, 1].min() - sy * S[:, 1].min())]
    best = None
    for sx, tx, sy, ty in starts:
        assign = None
        for _ in range(15):
            P = np.stack([S[:, 0] * sx + tx, S[:, 1] * sy + ty], 1)
            dist = np.linalg.norm(P[:, None, :] - C[None, :, :], axis=2)
            # a nut is a fraction of its control's printed size: a letter of the label
            # (far smaller) or the footswitch (far larger) is not this control's nut
            dist = dist + np.where(size_ok, 0.0, MAX_MISS * w * 4)
            # a dummy column per control lets a control stay unmatched at a fixed price
            cost = np.concatenate([dist, np.full((len(S), len(S)), MAX_MISS * w)], axis=1)
            assign = [j if j < len(C) else -1 for j in hungarian(cost)]
            pairs = [(i, j) for i, j in enumerate(assign) if j >= 0]
            if len(pairs) < 2:
                break
            ii = [i for i, _ in pairs]
            A = C[[j for _, j in pairs]]
            new = []
            for k in (0, 1):
                xs, ys = S[ii, k], A[:, k]
                if np.ptp(xs) > 1:
                    s, _ = np.polyfit(xs, ys, 1)
                    s = float(np.clip(s, 0.6, 1.8))
                else:
                    s = [sx, sy][k]
                t = float(np.median(ys - s * xs))
                new += [s, t]
            if np.allclose(new, [sx, tx, sy, ty], atol=0.5):
                break
            sx, tx, sy, ty = new
        P = np.stack([S[:, 0] * sx + tx, S[:, 1] * sy + ty], 1)
        misses = sum(1 for j in assign if j < 0)
        worst = max((np.hypot(*(P[i] - C[j])) for i, j in enumerate(assign) if j >= 0), default=0) / w
        key = (misses, worst)
        if best is None or key < best[0]:
            best = (key, assign)
    return best[1], best[0][1]


def find_footswitch(rgb, alpha, spec_xy):
    """The printed footswitch near the spec's (x, y): the round chrome cap closest to
    it, as (x, y, d) in pixels, or the spec position with a typical size when the art
    shows none the detector can find."""
    h, w = alpha.shape
    sx, sy = spec_xy[0] * w, spec_xy[1] * h
    x0, x1 = int(max(0, sx - w * 0.22)), int(min(w, sx + w * 0.22))
    y0, y1 = int(max(0, sy - h * 0.12)), int(min(h, sy + h * 0.12))
    gray = cv2.cvtColor(rgb[y0:y1, x0:x1], cv2.COLOR_RGB2GRAY)
    gray = cv2.GaussianBlur(gray, (5, 5), 1.5)
    circles = cv2.HoughCircles(gray, cv2.HOUGH_GRADIENT, dp=1.2, minDist=w * 0.05, param1=120, param2=30,
                               minRadius=int(w * 0.035), maxRadius=int(w * 0.11))
    best = None
    if circles is not None:
        for cx, cy, r in circles[0]:
            dist = np.hypot(cx + x0 - sx, cy + y0 - sy)
            # the collar, not a ring inside the cap: the LARGEST circle near the spot
            if dist < w * 0.16 and (best is None or r > best[3] / 2):
                best = (dist, cx + x0, cy + y0, r * 2)
    if best is None:
        return float(sx), float(sy), float(w * 0.15), True
    return float(best[1]), float(best[2]), float(best[3]), False


# ---------------------------------------------------------------------------------------


def import_face(mod, src_dir, overrides, review):
    art = mod['current_art'].rsplit('.', 1)[0]
    im = Image.open(os.path.join(src_dir, 'out', art + '-face.png')).convert('RGBA')
    rgba = np.array(im)
    override = overrides.get(mod['id'], {})
    if '_box' in override:
        alpha = boxed(rgba.shape[1], rgba.shape[0], override['_box'])
    else:
        alpha = cut_out(rgba)
    rgba[..., 3] = alpha
    h, w = alpha.shape
    rgb = np.ascontiguousarray(rgba[..., :3])

    controls = mod['controls_on_face']
    placed, problems = [], []
    if controls:
        cands = candidates(rgb, alpha)
        assign, worst = match([(c['center']['x'], c['center']['y']) for c in controls],
                              [c.get('diameter') or c.get('size') for c in controls], cands, w, h)
        for c, j in zip(controls, assign):
            if c['param'] in override:
                x, y, d = override[c['param']]
                placed.append({'param': c['param'], 'kind': c['kind'], 'x': x * w, 'y': y * h, 'd': d * w, 'manual': True})
            elif j >= 0:
                x, y, d, _ = cands[j]
                placed.append({'param': c['param'], 'kind': c['kind'], 'x': x, 'y': y, 'd': d})
            else:
                problems.append(f"{c['param']} unmatched")
        # two controls on one nut is a wrong match, never a coincidence
        spots = [(round(p['x'] / 4), round(p['y'] / 4)) for p in placed]
        if len(set(spots)) != len(spots):
            problems.append('two controls share a nut')

    # the printed footswitch of a floor unit: the app makes it the bypass switch
    foot = None
    spec_foot = (mod.get('decor') or {}).get('footswitch')
    if spec_foot and mod['family'] in ('stomp', 'treadle'):
        if '_footswitch' in override:
            fx, fy, fd = override['_footswitch']
            foot = (fx * w, fy * h, fd * w)
        else:
            fx, fy, fd, guessed = find_footswitch(rgb, alpha, spec_foot)
            foot = (fx, fy, fd)
            if guessed:
                problems.append('footswitch not found (spec position)')

    ys, xs = np.where(alpha > 16)
    mx, my = int(w * 0.015), int(h * 0.015)
    x0, x1 = max(0, xs.min() - mx), min(w, xs.max() + mx + 1)
    y0, y1 = max(0, ys.min() - my), min(h, ys.max() + my + 1)
    crop = Image.fromarray(rgba[y0:y1, x0:x1])
    cw, ch = x1 - x0, y1 - y0
    out = []
    ranges = {c['param']: c for c in controls}
    for p in placed:
        c = ranges[p['param']]
        out.append({'param': p['param'], 'kind': p['kind'],
                    'x': round((p['x'] - x0) / cw, 4), 'y': round((p['y'] - y0) / ch, 4),
                    'd': round(p['d'] / cw, 4),
                    # the range and default, so a preview with no live unit (the gear
                    # library) still turns each knob to where a fresh one starts
                    'min': c['min'], 'max': c['max'], 'default': c['default']})
        printed = SELECTOR_ANGLES.get(f"{mod['id']}.{p['param']}")
        if printed:
            out[-1]['angles'] = printed
    entry_foot = None
    if foot is not None:
        entry_foot = {'x': round((foot[0] - x0) / cw, 4), 'y': round((foot[1] - y0) / ch, 4), 'd': round(foot[2] / cw, 4)}
    target_w = min(cw, FACE_MAX_WIDTH.get(mod['family'], 1024))
    if target_w != cw:
        crop = crop.resize((target_w, round(ch * target_w / cw)), Image.LANCZOS)
    name = art + '-face.webp'
    crop.save(os.path.join(ASSETS, 'Faces', name), 'WEBP', lossless=WEBP_LOSSLESS, quality=100, method=4)
    if review is not None:
        review.append((mod, crop, out + ([dict(entry_foot, param='FOOTSWITCH')] if entry_foot else []), problems))
    entry = {'file': name, 'width': crop.width, 'height': crop.height, 'controls': out}
    if entry_foot:
        entry['footswitch'] = entry_foot
    for key, fix in FACE_CORRECTIONS.get(mod['id'], {}).items():
        target = entry.get('footswitch') if key == 'footswitch' else next((c for c in out if c['param'] == key), None)
        if target is None:
            problems.append(f'correction for {key}: no such control')
            continue
        target['x'], target['y'] = fix[0], fix[1]
        if len(fix) > 2 and fix[2]:
            target['d'] = fix[2]
    return entry, problems


def review_sheets(items, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    cw, chh = 330, 520
    for start in range(0, len(items), 12):
        chunk = items[start:start + 12]
        sheet = Image.new('RGB', (cw * 4, chh * 3), (240, 240, 240))
        d = ImageDraw.Draw(sheet)
        for k, (mod, img, placed, problems) in enumerate(chunk):
            im = img.copy()
            im.thumbnail((cw - 10, chh - 34))
            ox, oy = (k % 4) * cw + 5, (k // 4) * chh + 28
            sheet.paste(im, (ox, oy), im)
            d.text((ox, oy - 24), mod['id'] + ('  !! ' + '; '.join(problems) if problems else ''),
                   fill=(200, 0, 0) if problems else (0, 0, 0))
            for p in placed:
                cx, cy = ox + p['x'] * im.width, oy + p['y'] * im.height
                r = max(4, p['d'] * im.width / 2)
                d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=(255, 0, 255), width=2)
                d.text((cx - r, cy + r + 1), p['param'][:12], fill=(255, 0, 255))
        sheet.save(os.path.join(out_dir, f'review-{start // 12 + 1:02d}.png'))


def import_caps(src_dir):
    shared = {'toggle-up', 'toggle-down', 'selector-cap', 'led-on', 'led-off', 'footswitch'}
    for f in sorted(os.listdir(os.path.join(src_dir, 'out'))):
        base = f[:-4]
        if f.endswith('.png') and (base.startswith('knob-') or base in shared):
            im = Image.open(os.path.join(src_dir, 'out', f)).convert('RGBA')
            size = 128 if base.startswith('led-') else 256
            im = im.resize((size, size), Image.LANCZOS)
            im.save(os.path.join(ASSETS, 'Caps', base + '.webp'), 'WEBP', quality=90, method=6)


SELECTOR_ANGLES = {}
FACE_CORRECTIONS = {}


def main():
    global SELECTOR_ANGLES, FACE_CORRECTIONS
    if os.path.exists(CORRECTIONS):
        FACE_CORRECTIONS = {k: v for k, v in json.load(open(CORRECTIONS, encoding='utf-8')).items() if not k.startswith('_')}
    if os.path.exists(ANGLES):
        SELECTOR_ANGLES = {k: v for k, v in json.load(open(ANGLES, encoding='utf-8')).items() if not k.startswith('_')}
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--only')
    ap.add_argument('--review')
    a = ap.parse_args()
    spec = json.load(open(os.path.join(a.src, 'rig-art-spec.json'), encoding='utf-8'))
    overrides = json.load(open(OVERRIDES, encoding='utf-8')) if os.path.exists(OVERRIDES) else {}
    overrides = {k: v for k, v in overrides.items() if not k.startswith('_')}
    os.makedirs(os.path.join(ASSETS, 'Faces'), exist_ok=True)
    os.makedirs(os.path.join(ASSETS, 'Caps'), exist_ok=True)
    only = set(a.only.split(',')) if a.only else None
    path = os.path.join(ASSETS, 'faceplates.json')
    layout = json.load(open(path, encoding='utf-8')) if os.path.exists(path) else {}
    faces = layout.get('faces', {})
    review, bad = [], []
    for mod in spec['modules']:
        if only and mod['id'] not in only:
            continue
        entry, problems = import_face(mod, a.src, overrides, review)
        faces[mod['id']] = entry
        if problems:
            bad.append((mod['id'], problems))
        print(f"{mod['id']:14} {len(entry['controls']):2}/{len(mod['controls_on_face']):2}  {'; '.join(problems) or 'ok'}")
    layout = {
        '_about': 'Generated by tools/rig-art/import-faceplates.py. x/y = control centre as a fraction '
                  'of the face image, d = nut diameter as a fraction of its width.',
        'faces': dict(sorted(faces.items())),
        'knobStyles': dict(sorted({m['id']: m['finish']['knob_style'] for m in spec['modules']}.items())),
    }
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(layout, f, indent=1)
        f.write('\n')
    import_caps(a.src)
    if a.review:
        review_sheets(review, a.review)
    print(f'{len(bad)} face(s) with problems:')
    for m, p in bad:
        print('  ', m, p)


if __name__ == '__main__':
    main()
