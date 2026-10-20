"""Re-encode the rig's LOSSLESS face masters as lossy WebP (#407).

import-faceplates.py writes every face as a LOSSLESS WebP master (VP8L). This step
re-encodes each lossless face in Assets/Rig/Faces/ as lossy WebP with the pinned
cwebp, at the face's own resolution, alpha kept losslessly, metadata stripped. A
face that is already lossy is skipped, so running it twice never compresses a face
twice; a NEW face from import-faceplates.py goes through the same step.

Why these settings (measured on all 100 faces, 2026-10-07, #407):
  * FULL resolution. The module editor scales a face up to FireRigFaceMaxScale
    (2.0 on the wide ladder) to fill the dock; on a 4K panel at 200 % the face is
    drawn ~1800 physical px tall, more than the ~1470 px stored. Every pixel stored
    is shown, so none is dropped.
  * -sharp_yuv. Lossy WebP is always 4:2:0; the sharp RGB->YUV conversion keeps
    coloured print (the NAM amp's red scales) on its edges, which plain conversion
    smeared (the reason the faces went lossless on 2026-09-27).
  * quality QUALITY, -m 6. See --measure: the per-face worst-window SSIM and the
    chroma PSNR, against the lossless master, at the stored (= drawn) size.

The encoder is pinned: libwebp CWEBP_VERSION, fetched once from the WebM project's
release bucket into artifacts/ and checked against its SHA-256.

  python tools/rig-art/compress-faces.py                 # encode every lossless face
  python tools/rig-art/compress-faces.py --measure <dir> --masters <dir> [--quality 90]
        # encode the lossless masters in <dir> to a scratch folder and report quality
"""
import argparse, concurrent.futures, csv, hashlib, io, os, shutil, subprocess, sys, tempfile, urllib.request, zipfile

import cv2
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
FACES = os.path.join(REPO, 'DotCharter.Tone.UI', 'Assets', 'Rig', 'Faces')

CWEBP_VERSION = '1.6.0'
CWEBP_ZIP = f'libwebp-{CWEBP_VERSION}-windows-x64.zip'
CWEBP_URL = f'https://storage.googleapis.com/downloads.webmproject.org/releases/webp/{CWEBP_ZIP}'
CWEBP_SHA256 = '48886f506b21f62e4661f0f4cbfca19800897c385128e8902542d29a950c93f1'
CWEBP_CACHE = os.path.join(REPO, 'artifacts', 'tools', f'libwebp-{CWEBP_VERSION}')

QUALITY = 90
SHARP_YUV = True


def cwebp_path():
    exe = os.path.join(CWEBP_CACHE, f'libwebp-{CWEBP_VERSION}-windows-x64', 'bin', 'cwebp.exe')
    if not os.path.exists(exe):
        os.makedirs(CWEBP_CACHE, exist_ok=True)
        data = urllib.request.urlopen(CWEBP_URL).read()
        digest = hashlib.sha256(data).hexdigest()
        if digest != CWEBP_SHA256:
            sys.exit(f'{CWEBP_ZIP}: sha256 {digest}, expected {CWEBP_SHA256}')
        zipfile.ZipFile(io.BytesIO(data)).extractall(CWEBP_CACHE)
    version = subprocess.run([exe, '-version'], capture_output=True, text=True, check=True).stdout.split()[0]
    if version != CWEBP_VERSION:
        sys.exit(f'cwebp {version}, expected {CWEBP_VERSION}')
    return exe


def is_lossless(path):
    """True when the WebP's image chunk is VP8L (lossless)."""
    with open(path, 'rb') as f:
        data = f.read()
    i = 12
    while i + 8 <= len(data):
        tag, size = data[i:i + 4], int.from_bytes(data[i + 4:i + 8], 'little')
        if tag == b'VP8L': return True
        if tag == b'VP8 ': return False
        i += 8 + size + (size & 1)
    raise ValueError(f'{path}: no image chunk')


def encode(cwebp, src, dst, quality):
    """src (any image Pillow reads) -> lossy WebP at dst. Alpha lossless, no metadata."""
    with tempfile.TemporaryDirectory() as tmp:
        png = os.path.join(tmp, 'in.png')  # cwebp 1.6 reads PNG; the master is WebP
        Image.open(src).convert('RGBA').save(png)
        sharp = ['-sharp_yuv'] if SHARP_YUV else []
        subprocess.run([cwebp, '-quiet', '-q', str(quality), '-m', '6', *sharp,
                        '-alpha_q', '100', '-metadata', 'none', png, '-o', dst], check=True)


# ---- quality --------------------------------------------------------------------------

def _premultiplied(path):
    rgba = np.asarray(Image.open(path).convert('RGBA'), dtype=np.float64)
    return rgba[..., :3] * (rgba[..., 3:] / 255.0), rgba[..., 3]


def _ssim_map(a, b):
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    blur = lambda x: cv2.GaussianBlur(x, (11, 11), 1.5)
    ma, mb = blur(a), blur(b)
    va, vb, cov = blur(a * a) - ma * ma, blur(b * b) - mb * mb, blur(a * b) - ma * mb
    return ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2))


def _psnr(a, b, mask):
    mse = np.mean((a[mask] - b[mask]) ** 2)
    return 99.0 if mse == 0 else 10 * np.log10(255 ** 2 / mse)


def quality(master, lossy):
    """Compare at the stored size (the size the editor draws). Premultiplied RGB,
    so a colour under a transparent pixel never counts; only the face (alpha > 0)."""
    a, alpha_a = _premultiplied(master)
    b, alpha_b = _premultiplied(lossy)
    mask = alpha_a > 0
    ya = a @ [0.299, 0.587, 0.114]
    yb = b @ [0.299, 0.587, 0.114]
    chroma = lambda x, y: (x[..., 0] - y, x[..., 2] - y)  # R-Y, B-Y
    ra, ba = chroma(a, ya)
    rb, bb = chroma(b, yb)
    worst_window = 1.0
    ssim_means = []
    for ch in range(3):
        m = _ssim_map(a[..., ch], b[..., ch])[mask]
        ssim_means.append(float(m.mean()))
        worst_window = min(worst_window, float(np.percentile(m, 0.1)))
    return {
        'psnr_rgb': round(_psnr(a, b, mask), 2),
        'psnr_y': round(_psnr(ya, yb, mask), 2),
        'psnr_chroma': round(min(_psnr(ra, rb, mask), _psnr(ba, bb, mask)), 2),
        'ssim': round(min(ssim_means), 5),
        'ssim_worst_0.1pct': round(worst_window, 4),
        'alpha_max_diff': int(np.abs(alpha_a - alpha_b).max()),
    }


def _measure_one(args):
    cwebp, master, out_dir, q, sharp = args
    global SHARP_YUV
    SHARP_YUV = sharp
    dst = os.path.join(out_dir, os.path.basename(master))
    encode(cwebp, master, dst, q)
    w, h = Image.open(master).size
    row = {'face': os.path.basename(master), 'w': w, 'h': h,
           'lossless_bytes': os.path.getsize(master), 'lossy_bytes': os.path.getsize(dst)}
    row.update(quality(master, dst))
    return row


def measure(cwebp, masters, out_dir, q, workers):
    os.makedirs(out_dir, exist_ok=True)
    files = sorted(os.path.join(masters, f) for f in os.listdir(masters) if f.endswith('.webp'))
    with concurrent.futures.ProcessPoolExecutor(workers) as pool:
        rows = list(pool.map(_measure_one, [(cwebp, f, out_dir, q, SHARP_YUV) for f in files]))
    with open(os.path.join(out_dir, f'quality-q{q}.csv'), 'w', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    total_in = sum(r['lossless_bytes'] for r in rows)
    total_out = sum(r['lossy_bytes'] for r in rows)
    print(f'q{q}: {len(rows)} faces, {total_in / 1e6:.1f} MB -> {total_out / 1e6:.1f} MB')
    for key in ('psnr_rgb', 'psnr_y', 'psnr_chroma', 'ssim', 'ssim_worst_0.1pct'):
        worst = min(rows, key=lambda r: r[key])
        print(f'  worst {key:18} {worst[key]:>8}  ({worst["face"]})')
    print(f'  max alpha diff     {max(r["alpha_max_diff"] for r in rows)}')
    print(f'  largest face       {max(r["lossy_bytes"] for r in rows) / 1e3:.0f} KB')


def compress_in_place(cwebp, quality_):
    done = 0
    for name in sorted(os.listdir(FACES)):
        path = os.path.join(FACES, name)
        if not name.endswith('.webp') or not is_lossless(path): continue
        tmp = path + '.tmp.webp'
        encode(cwebp, path, tmp, quality_)
        before, after = os.path.getsize(path), os.path.getsize(tmp)
        shutil.move(tmp, path)
        done += 1
        print(f'{name}: {before / 1e3:.0f} KB -> {after / 1e3:.0f} KB')
    print(f'{done} face(s) re-encoded at q{quality_}')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--quality', type=int, default=QUALITY)
    ap.add_argument('--measure', metavar='OUT_DIR', help='encode the masters to OUT_DIR and report quality')
    ap.add_argument('--masters', default=FACES, help='folder of lossless masters (with --measure)')
    ap.add_argument('--workers', type=int, default=4)
    ap.add_argument('--no-sharp-yuv', action='store_true', help='plain RGB->YUV (for comparison only)')
    args = ap.parse_args()
    global SHARP_YUV
    SHARP_YUV = not args.no_sharp_yuv
    cwebp = cwebp_path()
    if args.measure:
        measure(cwebp, args.masters, args.measure, args.quality, args.workers)
    else:
        compress_in_place(cwebp, args.quality)


if __name__ == '__main__':
    main()
