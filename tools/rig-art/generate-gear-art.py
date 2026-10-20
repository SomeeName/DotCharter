#!/usr/bin/env python3
# ---------------------------------------------------------------------------
#  PROCEDURAL PLACEHOLDER ART for the JuceSharp 0.9 gear wave (43 modules).
#
#  These are NOT the final artwork. The 57 illustrations that shipped before
#  this wave (Assets/Rig/Art/*.webp) are photoreal 3D renders of hardware; the
#  43 files this script writes are stylised, vector-flat front panels drawn
#  with Pillow. They exist so every native module has its OWN illustration
#  instead of falling back to the merger patchbay sprite (which is what
#  "some grey box" looks like on the board and in the gear rail), and so
#  RigArtworkTests.EveryNativeModule_HasItsOwnIllustration passes honestly.
#
#  THE OWNER DECIDES WHAT REPLACES THEM. When a hand-drawn / rendered file
#  lands for an id, drop it in over the generated one and remove that row from
#  SPECS below (or leave it — the script only writes what it is asked for via
#  --only). Nothing in the app reads this script; it is committed so the whole
#  set can be regenerated consistently (same palette, same geometry) if the
#  placeholders need a tweak before the real art exists.
#
#  Usage:
#     python tools/rig-art/generate-gear-art.py                 # write all 43 + contact sheet
#     python tools/rig-art/generate-gear-art.py --only rat,looper
#     python tools/rig-art/generate-gear-art.py --sheet-only
#
#  Output: DotCharter.UI/Assets/Rig/Art/<basename>.webp  (VP8 lossy, RGB, no
#  alpha — matching every existing file in that folder) and, unless
#  --no-sheet, a contact sheet PNG (default next to the repo, see --sheet).
#
#  Requires Pillow with WebP support (`pip install pillow`).
#
#  NOTE: `.gitignore` ignores `*.py` repo-wide, so this file is tracked via
#  `git add -f` — the same way `tools/branding/build_rig_art.py` is.
# ---------------------------------------------------------------------------

from __future__ import annotations

import argparse
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

# ---------------------------------------------------------------------------
# Palette + house geometry, read off the existing set (see report in the PR):
#   * background is a flat near-black, ~#0f0e13, opaque (no alpha anywhere)
#   * stompboxes are ~250-360 x ~370-450, amp heads 480x~200, cabs 480x378
#   * jack barrels stick out of the left and right edges at mid height
# ---------------------------------------------------------------------------

BG = (15, 14, 19)
SS = 3  # supersample factor

FONT_DIR = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    for candidate in (os.path.join(FONT_DIR, name), name):
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def F_PLATE(size: int) -> ImageFont.FreeTypeFont:
    return font("seguisb.ttf", size)


def F_LABEL(size: int) -> ImageFont.FreeTypeFont:
    return font("segoeui.ttf", size)


def rgb(v: int) -> tuple[int, int, int]:
    return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF)


def shade(c, f: float):
    return tuple(max(0, min(255, int(round(ch * f)))) for ch in c)


def mix(a, b, t: float):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


# ---------------------------------------------------------------------------
# SPECS — the whole table this script draws from.
#   id:      native module type id (JuceSharp gear catalogue)
#   base:    kebab-case descriptive asset basename (house style; NOT the id)
#   name:    display name, screen-printed on the plate
#   badge:   the gear-rail drawer the module sits in
#   family:  stomp | head | cab | rack | treadle
#   coat/accent: the (Coat, Accent) pair mirrored into RigArtwork.Finishes
#   knobs:   visible knob caps (from the native param count, capped 4-6)
#   glyph:   the one distinctive feature drawn on the face
# ---------------------------------------------------------------------------

SPECS = [
    # ---- Dynamics -------------------------------------------------------
    dict(id="limiter", base="pedal-limiter", name="PEDAL LIMITER", badge="DYNAMICS",
         family="stomp", coat=0x39424B, accent=0xCFE0EE, knobs=4, glyph="ceiling"),
    dict(id="transient", base="transient-shaper", name="TRANSIENT", badge="DYNAMICS",
         family="stomp", coat=0x4A5560, accent=0xDBE8F4, knobs=4, glyph="transient"),
    dict(id="multiband", base="multiband-compressor", name="MULTIBAND COMP", badge="DYNAMICS",
         family="rack", coat=0x2C3239, accent=0xBFD4E6, knobs=6, glyph="bands"),
    dict(id="expander", base="expander", name="EXPANDER", badge="DYNAMICS",
         family="stomp", coat=0x445059, accent=0xC8DCEB, knobs=5, glyph="expand"),
    dict(id="optocomp", base="optical-compressor", name="OPTICAL COMP", badge="DYNAMICS",
         family="stomp", coat=0x5D5348, accent=0xFFE9B8, knobs=4, glyph="lamp"),
    dict(id="ducker", base="ducker", name="DUCKER", badge="DYNAMICS",
         family="stomp", coat=0x353F46, accent=0xB7D0E0, knobs=5, glyph="duck"),

    # ---- Drive ----------------------------------------------------------
    dict(id="rat", base="rodent-distortion", name="RODENT", badge="DRIVE",
         family="stomp", coat=0x1B1B1E, accent=0xE8E3D6, knobs=3, glyph="rodent"),
    dict(id="ds1", base="orange-distortion", name="ORANGE DIST", badge="DRIVE",
         family="stomp", coat=0xC8621A, accent=0xFFD9A8, knobs=3, glyph="spike"),
    dict(id="hm2", base="chainsaw-distortion", name="CHAINSAW", badge="DRIVE",
         family="stomp", coat=0x1C1C22, accent=0xFF5A3C, knobs=4, glyph="saw"),
    dict(id="tapesat", base="tape-saturation", name="TAPE SAT", badge="DRIVE",
         family="stomp", coat=0x7A5C33, accent=0xF2D9A6, knobs=5, glyph="reels"),
    dict(id="bassod", base="bass-overdrive", name="BASS DRIVE", badge="DRIVE",
         family="stomp", coat=0x1F3A6B, accent=0xA9C8FF, knobs=5, glyph="lowwave"),
    dict(id="tubepre", base="tube-preamp-di", name="TUBE PRE / DI", badge="DRIVE",
         family="stomp", coat=0x8A7A5C, accent=0xFFEEC2, knobs=6, glyph="tube"),
    dict(id="octfuzz", base="octave-fuzz", name="OCTAVE FUZZ", badge="DRIVE",
         family="stomp", coat=0x3B2A12, accent=0xF5C44A, knobs=4, glyph="octave"),

    # ---- Amp / cab ------------------------------------------------------
    dict(id="bassamp", base="bass-amp", name="BASS AMP", badge="AMP",
         family="head", coat=0x2A2118, accent=0xE8B878, knobs=6, glyph="lowwave"),
    dict(id="rsamp", base="rocksmith-amp", name="ROCKSMITH AMP", badge="AMP",
         family="head", coat=0x2B1610, accent=0xFF8A4C, knobs=6, glyph="tube"),
    dict(id="acpre", base="acoustic-preamp", name="ACOUSTIC PREAMP", badge="AMP",
         family="head", coat=0x6B5A41, accent=0xF3DCB0, knobs=6, glyph="soundhole"),
    dict(id="poweramp", base="power-amp-sag", name="POWER AMP + SAG", badge="AMP",
         family="head", coat=0x3A1D1D, accent=0xF08A6A, knobs=6, glyph="tube"),
    dict(id="micpos", base="cab-mic-position", name="MIC POSITION", badge="CAB",
         family="cab", coat=0x241F1B, accent=0xD8A86A, knobs=4, glyph="mic"),

    # ---- Filter / eq ----------------------------------------------------
    dict(id="svf", base="multimode-filter", name="MULTIMODE FILTER", badge="FILTER",
         family="rack", coat=0x4C4A16, accent=0xF2E06A, knobs=6, glyph="filtercurve"),
    dict(id="comb", base="comb-filter", name="COMB FILTER", badge="FILTER",
         family="stomp", coat=0x3F4A20, accent=0xD8E77A, knobs=5, glyph="comb"),
    dict(id="vocoder", base="vocoder", name="VOCODER", badge="FILTER",
         family="rack", coat=0x6A5A1F, accent=0xFFE89A, knobs=6, glyph="vocoder"),
    dict(id="talkbox", base="talk-box", name="TALK BOX", badge="FILTER",
         family="stomp", coat=0x5C3A1E, accent=0xFFC78A, knobs=5, glyph="talk"),
    dict(id="tilt", base="tilt-eq", name="TILT EQ", badge="EQ",
         family="stomp", coat=0x3D5220, accent=0xC6E876, knobs=3, glyph="tilt"),

    # ---- Modulation -----------------------------------------------------
    dict(id="tzflanger", base="thru-zero-flanger", name="THRU-ZERO", badge="MODULATION",
         family="stomp", coat=0x1E3A5C, accent=0x9ECBF5, knobs=6, glyph="thruzero"),
    dict(id="barberpole", base="barber-pole-phaser", name="BARBER POLE", badge="MODULATION",
         family="stomp", coat=0x7A2A2A, accent=0xFFB0A0, knobs=6, glyph="barber"),
    dict(id="autopan", base="auto-pan", name="AUTO-PAN", badge="MODULATION",
         family="stomp", coat=0x334A78, accent=0xB6C9FF, knobs=5, glyph="pan"),
    dict(id="ensemble", base="string-ensemble", name="ENSEMBLE", badge="MODULATION",
         family="stomp", coat=0x4A3A72, accent=0xC3B0FF, knobs=6, glyph="voices"),
    dict(id="freqshift", base="frequency-shifter", name="FREQ SHIFTER", badge="MODULATION",
         family="stomp", coat=0x2D2A5E, accent=0x9F9BF0, knobs=4, glyph="freqshift"),

    # ---- Pitch ----------------------------------------------------------
    dict(id="detune", base="detune-micro-pitch", name="MICRO PITCH", badge="PITCH",
         family="stomp", coat=0x6B2F5E, accent=0xF3A8E0, knobs=5, glyph="detune"),
    dict(id="pitchcorrect", base="pitch-correction", name="PITCH CORRECT", badge="PITCH",
         family="stomp", coat=0x47304F, accent=0xDCB0E8, knobs=6, glyph="quantize"),
    dict(id="twelvestring", base="twelve-string-sim", name="12-STRING", badge="PITCH",
         family="stomp", coat=0x5A4630, accent=0xEBC79A, knobs=6, glyph="strings"),
    dict(id="basssynth", base="bass-synth-tracker", name="BASS SYNTH", badge="PITCH",
         family="stomp", coat=0x24305A, accent=0x8FB4FF, knobs=6, glyph="square"),

    # ---- Delay ----------------------------------------------------------
    dict(id="multitap", base="multitap-delay", name="MULTITAP", badge="TIME",
         family="stomp", coat=0x0E5A63, accent=0x7FE3E8, knobs=6, glyph="taps"),
    dict(id="duckdelay", base="ducking-delay", name="DUCKING DELAY", badge="TIME",
         family="stomp", coat=0x14454F, accent=0x86D6DD, knobs=6, glyph="duck"),
    dict(id="bbddelay", base="analog-bbd-delay", name="ANALOG BBD", badge="TIME",
         family="stomp", coat=0x2A4A4A, accent=0x9FD8D0, knobs=6, glyph="buckets"),
    dict(id="granular", base="granular-cloud", name="GRANULAR CLOUD", badge="TIME",
         family="stomp", coat=0x1A4A58, accent=0x8AE0F0, knobs=6, glyph="grains"),
    dict(id="rhythmdelay", base="rhythmic-tremolo-delay", name="RHYTHM DELAY", badge="TIME",
         family="stomp", coat=0x105057, accent=0x74DAD6, knobs=6, glyph="pattern"),

    # ---- Reverb ---------------------------------------------------------
    dict(id="hallreverb", base="hall-room-reverb", name="HALL / ROOM", badge="TIME",
         family="stomp", coat=0x2B4A5E, accent=0xA8D2EA, knobs=6, glyph="hall"),
    dict(id="convreverb", base="convolution-reverb", name="CONVOLUTION", badge="TIME",
         family="rack", coat=0x334A52, accent=0xA6DDE0, knobs=5, glyph="impulse"),
    dict(id="gatedreverb", base="gated-reverb", name="GATED VERB", badge="TIME",
         family="stomp", coat=0x1C3F44, accent=0x8FCFC6, knobs=6, glyph="gated"),
    dict(id="reversereverb", base="reverse-reverb", name="REVERSE VERB", badge="TIME",
         family="stomp", coat=0x274A44, accent=0x9FE0C2, knobs=6, glyph="swell"),

    # ---- Utility --------------------------------------------------------
    dict(id="volume", base="volume-expression-pedal", name="VOLUME", badge="UTILITY",
         family="treadle", coat=0x2E2E34, accent=0xD6DBE2, knobs=0, glyph="treadle"),
    dict(id="midside", base="mid-side-processor", name="MID / SIDE", badge="UTILITY",
         family="rack", coat=0x3A3A44, accent=0xC2C8D6, knobs=6, glyph="midside"),
    dict(id="looper", base="looper", name="LOOPER", badge="UTILITY",
         family="stomp", coat=0x45264F, accent=0xE3A8F0, knobs=4, glyph="loop",
         footswitches=2),
]


# ---------------------------------------------------------------------------
# Primitive drawing helpers (all coordinates are in supersampled space)
# ---------------------------------------------------------------------------


def grain(img: Image.Image, amount: int, seed: int) -> Image.Image:
    """Fine metal-flake noise over the whole panel. Also what keeps the lossy
    WebP in the 20-50 KB band the hand-authored files sit in — a perfectly flat
    vector panel encodes to ~6 KB and reads as a cartoon next to them."""
    rnd = random.Random(seed)
    w, h = img.size
    nw, nh = max(1, w // SS), max(1, h // SS)
    noise = Image.new("L", (nw, nh))
    noise.putdata([128 + rnd.randint(-amount, amount) for _ in range(nw * nh)])
    noise = noise.resize((w, h), Image.BILINEAR)
    grey = Image.merge("RGB", (noise, noise, noise))
    return Image.blend(img, ImageChops.overlay(img, grey), 0.55)


def vgradient(size, top, bottom):
    w, h = size
    strip = Image.new("RGB", (1, h))
    for y in range(h):
        strip.putpixel((0, y), mix(top, bottom, y / max(1, h - 1)))
    return strip.resize((w, h))


def panel(d: ImageDraw.ImageDraw, box, coat, radius, seed=0, bevel=True):
    """A coated enclosure face: vertical gradient, top highlight, bottom shade."""
    x0, y0, x1, y1 = box
    img = d._image
    grad = vgradient((int(x1 - x0), int(y1 - y0)), shade(coat, 1.22), shade(coat, 0.62))
    mask = Image.new("L", grad.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, grad.size[0] - 1, grad.size[1] - 1), radius=radius, fill=255)
    img.paste(grad, (int(x0), int(y0)), mask)
    if bevel:
        d.rounded_rectangle(box, radius=radius, outline=shade(coat, 0.30), width=3 * SS)
        d.rounded_rectangle((x0 + 3 * SS, y0 + 3 * SS, x1 - 3 * SS, y1 - 3 * SS),
                            radius=max(1, radius - 3 * SS), outline=shade(coat, 1.45), width=SS)


def screw(d: ImageDraw.ImageDraw, cx, cy, r, angle=32):
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(52, 54, 58), outline=(16, 16, 18), width=max(1, SS // 2))
    d.ellipse((cx - r * 0.78, cy - r * 0.78, cx + r * 0.78, cy + r * 0.78), fill=(128, 132, 138))
    d.ellipse((cx - r * 0.62, cy - r * 0.62, cx + r * 0.62, cy + r * 0.62), fill=(88, 92, 98))
    a = math.radians(angle)
    dx, dy = math.cos(a) * r * 0.62, math.sin(a) * r * 0.62
    d.line((cx - dx, cy - dy, cx + dx, cy + dy), fill=(38, 40, 44), width=max(2, int(r * 0.28)))


def jack(d: ImageDraw.ImageDraw, cx, cy, r, side):
    """The chrome barrel that sticks out of the enclosure side."""
    w = r * 1.5
    box = (cx - w, cy - r, cx + w, cy + r) if side else (cx - w, cy - r, cx + w, cy + r)
    d.rounded_rectangle(box, radius=r * 0.35, fill=(96, 100, 106), outline=(30, 31, 34), width=SS)
    d.rounded_rectangle((box[0] + r * 0.25, box[1] + r * 0.22, box[2] - r * 0.25, box[1] + r * 0.5),
                        radius=r * 0.2, fill=(150, 155, 162))
    d.ellipse((cx - r * 0.42, cy - r * 0.42, cx + r * 0.42, cy + r * 0.42), fill=(22, 22, 25))


def knob(d: ImageDraw.ImageDraw, cx, cy, r, accent, value=0.62, ticks=True, skirt=True):
    if ticks:
        for i in range(11):
            a = math.radians(135 + i * 27)
            rr = r * 1.30
            px, py = cx + math.cos(a) * rr, cy + math.sin(a) * rr
            d.ellipse((px - SS * 0.9, py - SS * 0.9, px + SS * 0.9, py + SS * 0.9),
                      fill=mix((190, 190, 196), accent, 0.25))
    if skirt:
        d.ellipse((cx - r * 1.06, cy - r * 1.06, cx + r * 1.06, cy + r * 1.06), fill=(10, 10, 12))
    # body: dark cap with a top-left sheen
    for i in range(int(r), 0, -1):
        t = i / r
        c = mix((70, 72, 78), (18, 18, 21), t)
        d.ellipse((cx - i, cy - i, cx + i, cy + i), fill=c)
    d.ellipse((cx - r * 0.72, cy - r * 0.85, cx - r * 0.05, cy - r * 0.28), fill=(96, 99, 106))
    d.ellipse((cx - r * 0.55, cy - r * 0.55, cx + r * 0.55, cy + r * 0.55), fill=(30, 31, 35))
    a = math.radians(135 + value * 270)
    d.line((cx, cy, cx + math.cos(a) * r * 0.92, cy + math.sin(a) * r * 0.92),
           fill=mix((235, 238, 242), accent, 0.35), width=max(2, int(r * 0.16)))


def led(d: ImageDraw.ImageDraw, cx, cy, r, accent):
    for i in range(int(r * 2.6), int(r), -1):
        t = (i - r) / max(1, r * 1.6)
        d.ellipse((cx - i, cy - i, cx + i, cy + i), fill=mix(shade(accent, 0.55), BG, 0.55 + 0.45 * t))
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=mix(accent, (255, 255, 255), 0.45))
    d.ellipse((cx - r * 0.4, cy - r * 0.8, cx + r * 0.25, cy - r * 0.1), fill=(255, 255, 255))


def footswitch(d: ImageDraw.ImageDraw, cx, cy, r):
    d.ellipse((cx - r * 1.22, cy - r * 1.22, cx + r * 1.22, cy + r * 1.22), fill=(24, 25, 28))
    for i in range(int(r), 0, -1):
        t = i / r
        d.ellipse((cx - i, cy - i, cx + i, cy + i), fill=mix((178, 182, 190), (74, 78, 84), t))
    d.ellipse((cx - r * 0.62, cy - r * 0.62, cx + r * 0.62, cy + r * 0.62), fill=(132, 136, 144))
    d.ellipse((cx - r * 0.34, cy - r * 0.34, cx + r * 0.34, cy + r * 0.34), fill=(92, 96, 102))


def plate(d: ImageDraw.ImageDraw, box, text, accent, coat, size=None):
    x0, y0, x1, y1 = box
    d.rounded_rectangle(box, radius=(y1 - y0) * 0.22, fill=shade(coat, 0.42), outline=shade(accent, 0.7),
                        width=max(1, SS))
    size = size or int((y1 - y0) * 0.52)
    f = F_PLATE(size)
    while f.getlength(text) > (x1 - x0) * 0.88 and size > 6 * SS:
        size -= SS
        f = F_PLATE(size)
    tw = f.getlength(text)
    d.text(((x0 + x1) / 2 - tw / 2, (y0 + y1) / 2 - size * 0.68), text, font=f, fill=mix(accent, (255, 255, 255), 0.25))


def badge(d: ImageDraw.ImageDraw, x, y, text, accent):
    f = F_LABEL(int(8.5 * SS))
    d.text((x, y), text, font=f, fill=mix(accent, (120, 120, 130), 0.45))


def brand(d: ImageDraw.ImageDraw, cx, y, accent):
    f = F_LABEL(int(7.5 * SS))
    t = "DOTCHARTER"
    d.text((cx - f.getlength(t) / 2, y), t, font=f, fill=mix(accent, (110, 110, 120), 0.55))


# ---------------------------------------------------------------------------
# Glyphs — the one distinctive feature per module, drawn on the face.
# Each takes (draw, cx, cy, w, h, accent).
# ---------------------------------------------------------------------------


def _poly(d, pts, accent, width=None, closed=False):
    width = width or int(2.2 * SS)
    d.line(pts + ([pts[0]] if closed else []), fill=accent, width=width, joint="curve")


def g_ceiling(d, cx, cy, w, h, a):
    d.line((cx - w / 2, cy - h * 0.22, cx + w / 2, cy - h * 0.22), fill=a, width=int(2.4 * SS))
    pts = []
    for i in range(41):
        t = i / 40
        v = math.sin(t * math.pi * 3) * h * 0.42
        v = max(-h * 0.2, min(h * 0.2, v)) if abs(v) > h * 0.2 else v
        pts.append((cx - w / 2 + t * w, cy + h * 0.12 + v))
    _poly(d, pts, shade(a, 0.85))


def g_transient(d, cx, cy, w, h, a):
    _poly(d, [(cx - w / 2, cy + h / 2), (cx - w * 0.12, cy - h / 2), (cx + w * 0.05, cy + h * 0.1),
              (cx + w / 2, cy + h / 2)], a)


def g_bands(d, cx, cy, w, h, a):
    for i, hh in enumerate((0.55, 0.95, 0.7)):
        x = cx - w / 2 + w * (0.18 + i * 0.32)
        d.rounded_rectangle((x - w * 0.09, cy + h / 2 - h * hh, x + w * 0.09, cy + h / 2),
                            radius=w * 0.04, fill=mix(a, (20, 20, 24), 0.15 + i * 0.12))


def g_expand(d, cx, cy, w, h, a):
    _poly(d, [(cx - w / 2, cy), (cx - w * 0.1, cy)], a)
    _poly(d, [(cx - w * 0.1, cy), (cx + w / 2, cy - h * 0.42)], a)
    _poly(d, [(cx - w * 0.1, cy), (cx + w / 2, cy + h * 0.42)], shade(a, 0.6))


def g_lamp(d, cx, cy, w, h, a):
    r = min(w, h) * 0.24
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=a, width=int(2.2 * SS))
    d.ellipse((cx - r * 0.45, cy - r * 0.45, cx + r * 0.45, cy + r * 0.45), fill=a)
    for i in range(8):
        ang = math.radians(i * 45)
        d.line((cx + math.cos(ang) * r * 1.35, cy + math.sin(ang) * r * 1.35,
                cx + math.cos(ang) * r * 1.9, cy + math.sin(ang) * r * 1.9), fill=shade(a, 0.8), width=int(1.8 * SS))


def g_duck(d, cx, cy, w, h, a):
    pts = []
    for i in range(49):
        t = i / 48
        dip = -h * 0.42 * math.exp(-((t - 0.42) ** 2) / 0.012)
        pts.append((cx - w / 2 + t * w, cy - h * 0.2 - dip))
    _poly(d, pts, a)
    d.line((cx - w * 0.02, cy - h * 0.5, cx - w * 0.02, cy + h * 0.5), fill=shade(a, 0.45), width=int(1.5 * SS))


def g_rodent(d, cx, cy, w, h, a):
    r = min(w, h) * 0.42
    d.ellipse((cx - r * 1.5, cy - r * 0.7, cx + r * 0.7, cy + r * 0.9), outline=a, width=int(2.2 * SS))
    d.ellipse((cx - r * 1.75, cy - r * 1.5, cx - r * 0.65, cy - r * 0.4), outline=a, width=int(2 * SS))
    d.ellipse((cx + r * 0.15, cy - r * 0.35, cx + r * 0.75, cy + r * 0.25), outline=a, width=int(2 * SS))
    d.ellipse((cx + r * 0.52, cy - r * 0.05, cx + r * 0.72, cy + r * 0.15), fill=a)
    pts = [(cx - r * 1.45, cy + r * 0.55)]
    for i in range(1, 25):
        t = i / 24
        pts.append((cx - r * 1.45 - t * w * 0.42, cy + r * 0.55 - math.sin(t * math.pi * 1.1) * h * 0.3))
    _poly(d, pts, shade(a, 0.8), width=int(1.8 * SS))


def g_spike(d, cx, cy, w, h, a):
    pts = []
    for i in range(61):
        t = i / 60
        v = math.sin(t * math.pi * 4)
        v = math.copysign(min(abs(v), 0.62), v) / 0.62
        pts.append((cx - w / 2 + t * w, cy - v * h * 0.42))
    _poly(d, pts, a)


def g_saw(d, cx, cy, w, h, a):
    pts = []
    n = 9
    for i in range(n * 2 + 1):
        x = cx - w / 2 + w * i / (n * 2)
        pts.append((x, cy + (h * 0.38 if i % 2 else -h * 0.38)))
    _poly(d, pts, a)


def g_reels(d, cx, cy, w, h, a):
    r = min(w, h) * 0.3
    for sx in (-1, 1):
        c = cx + sx * w * 0.26
        d.ellipse((c - r, cy - r, c + r, cy + r), outline=a, width=int(2.2 * SS))
        d.ellipse((c - r * 0.3, cy - r * 0.3, c + r * 0.3, cy + r * 0.3), fill=a)
        for i in range(3):
            ang = math.radians(i * 120 + 20)
            d.line((c + math.cos(ang) * r * 0.35, cy + math.sin(ang) * r * 0.35,
                    c + math.cos(ang) * r * 0.85, cy + math.sin(ang) * r * 0.85),
                   fill=shade(a, 0.75), width=int(1.6 * SS))
    d.line((cx - w * 0.26, cy - r * 0.98, cx + w * 0.26, cy - r * 0.98), fill=shade(a, 0.65), width=int(1.8 * SS))


def g_lowwave(d, cx, cy, w, h, a):
    pts = [(cx - w / 2 + i / 60 * w, cy - math.sin(i / 60 * math.pi * 1.6) * h * 0.44) for i in range(61)]
    _poly(d, pts, a, width=int(3.2 * SS))


def g_tube(d, cx, cy, w, h, a):
    for sx in (-1, 1):
        c = cx + sx * w * 0.2
        d.rounded_rectangle((c - w * 0.11, cy - h * 0.42, c + w * 0.11, cy + h * 0.34),
                            radius=w * 0.11, outline=a, width=int(2.2 * SS))
        d.line((c, cy - h * 0.2, c, cy + h * 0.2), fill=mix(a, (255, 220, 150), 0.6), width=int(2.4 * SS))
        d.line((c - w * 0.11, cy + h * 0.34, c + w * 0.11, cy + h * 0.34), fill=shade(a, 0.7), width=int(2 * SS))


def g_octave(d, cx, cy, w, h, a):
    f = F_PLATE(int(h * 0.8))
    t = "8va"
    d.text((cx - f.getlength(t) / 2, cy - h * 0.52), t, font=f, fill=a)


def g_soundhole(d, cx, cy, w, h, a):
    r = min(w, h) * 0.38
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=a, width=int(2.6 * SS))
    d.ellipse((cx - r * 1.28, cy - r * 1.28, cx + r * 1.28, cy + r * 1.28), outline=shade(a, 0.55),
              width=int(1.6 * SS))
    for i in range(6):
        x = cx - r * 0.62 + i * r * 0.25
        d.line((x, cy - r * 1.5, x, cy + r * 1.5), fill=shade(a, 0.5), width=max(1, int(1.1 * SS)))


def g_mic(d, cx, cy, w, h, a):
    """A dynamic mic on a stand, facing the grille — the module IS the mic's
    position in front of the cab, so that is what the sprite has to show."""
    cw, ch = w * 0.30, h * 0.30
    top = cy - h * 0.44
    d.rounded_rectangle((cx - cw, top, cx + cw, top + ch), radius=cw, fill=shade(a, 0.72), outline=a,
                        width=int(1.8 * SS))
    for i in range(4):
        y = top + ch * (0.22 + i * 0.19)
        d.line((cx - cw * 0.72, y, cx + cw * 0.72, y), fill=shade(a, 0.45), width=max(1, int(1.2 * SS)))
    # body + boom
    d.rounded_rectangle((cx - cw * 0.42, top + ch, cx + cw * 0.42, top + ch * 1.5), radius=cw * 0.2,
                        fill=shade(a, 0.5))
    d.line((cx, top + ch * 1.5, cx, cy + h * 0.32), fill=shade(a, 0.62), width=int(2.6 * SS))
    d.line((cx - w * 0.26, cy + h * 0.44, cx + w * 0.26, cy + h * 0.44), fill=shade(a, 0.62), width=int(2.6 * SS))
    d.line((cx - w * 0.16, cy + h * 0.44, cx, cy + h * 0.32), fill=shade(a, 0.5), width=int(2 * SS))
    d.line((cx + w * 0.16, cy + h * 0.44, cx, cy + h * 0.32), fill=shade(a, 0.5), width=int(2 * SS))


def g_filtercurve(d, cx, cy, w, h, a):
    lp = [(cx - w / 2 + i / 60 * w, cy - h * 0.3 + (h * 0.66 if i / 60 > 0.55 else 0) *
           (1 / (1 + math.exp(-(i / 60 - 0.62) * 26)))) for i in range(61)]
    _poly(d, lp, a)
    hp = [(cx - w / 2 + i / 60 * w, cy + h * 0.36 - h * 0.6 * (1 / (1 + math.exp(-(i / 60 - 0.38) * 22))))
          for i in range(61)]
    _poly(d, hp, shade(a, 0.55))


def g_comb(d, cx, cy, w, h, a):
    n = 7
    for i in range(n):
        x = cx - w / 2 + w * (i + 0.5) / n
        d.line((x, cy + h * 0.44, x, cy - h * 0.44 + i * h * 0.05), fill=mix(a, (30, 30, 34), i * 0.08),
               width=int(2.2 * SS))
    d.line((cx - w / 2, cy + h * 0.44, cx + w / 2, cy + h * 0.44), fill=shade(a, 0.6), width=int(1.8 * SS))


def g_vocoder(d, cx, cy, w, h, a):
    n = 9
    for i in range(n):
        x = cx - w / 2 + w * (i + 0.5) / n
        hh = h * (0.2 + 0.64 * abs(math.sin(i * 1.1)))
        d.rounded_rectangle((x - w * 0.032, cy + h * 0.44 - hh, x + w * 0.032, cy + h * 0.44),
                            radius=w * 0.02, fill=mix(a, (255, 255, 255), 0.1 if i % 2 else 0.0))


def g_talk(d, cx, cy, w, h, a):
    d.arc((cx - w * 0.2, cy - h * 0.44, cx + w * 0.46, cy + h * 0.44), 300, 60, fill=a, width=int(2.4 * SS))
    pts = [(cx - w * 0.46, cy + h * 0.4), (cx - w * 0.3, cy + h * 0.4), (cx - w * 0.12, cy)]
    _poly(d, pts, shade(a, 0.8))
    for i, rr in enumerate((0.16, 0.26, 0.36)):
        d.arc((cx + w * (0.1 + rr), cy - h * rr * 1.6, cx + w * (0.1 + rr) + w * rr, cy + h * rr * 1.6),
              300, 60, fill=shade(a, 0.75 - i * 0.15), width=int(1.8 * SS))


def g_tilt(d, cx, cy, w, h, a):
    _poly(d, [(cx - w / 2, cy + h * 0.38), (cx + w / 2, cy - h * 0.38)], a, width=int(3 * SS))
    d.line((cx - w / 2, cy, cx + w / 2, cy), fill=shade(a, 0.4), width=max(1, int(1.2 * SS)))


def g_thruzero(d, cx, cy, w, h, a):
    up = [(cx - w / 2 + i / 60 * w, cy - math.sin(i / 60 * math.pi) * h * 0.42) for i in range(61)]
    dn = [(cx - w / 2 + i / 60 * w, cy + math.sin(i / 60 * math.pi) * h * 0.42) for i in range(61)]
    _poly(d, up, a)
    _poly(d, dn, shade(a, 0.6))
    d.line((cx, cy - h * 0.5, cx, cy + h * 0.5), fill=mix(a, (255, 255, 255), 0.4), width=max(1, int(1.4 * SS)))


def g_barber(d, cx, cy, w, h, a):
    r = min(w, h) * 0.42
    d.ellipse((cx - w * 0.5, cy - r, cx + w * 0.5, cy + r), outline=shade(a, 0.5), width=int(1.6 * SS))
    for i in range(6):
        x = cx - w * 0.42 + i * w * 0.17
        _poly(d, [(x, cy + r * 0.85), (x + w * 0.1, cy - r * 0.85)], mix(a, (255, 255, 255), 0.15 if i % 2 else 0),
              width=int(2.4 * SS))


def g_pan(d, cx, cy, w, h, a):
    d.line((cx - w * 0.42, cy, cx + w * 0.42, cy), fill=shade(a, 0.55), width=int(1.8 * SS))
    for sx, lbl in ((-1, "L"), (1, "R")):
        f = F_PLATE(int(h * 0.55))
        d.text((cx + sx * w * 0.46 - (f.getlength(lbl) if sx > 0 else 0), cy - h * 0.38), lbl, font=f, fill=a)
    _poly(d, [(cx - w * 0.18, cy - h * 0.2), (cx - w * 0.3, cy), (cx - w * 0.18, cy + h * 0.2)], a)
    _poly(d, [(cx + w * 0.18, cy - h * 0.2), (cx + w * 0.3, cy), (cx + w * 0.18, cy + h * 0.2)], a)


def g_voices(d, cx, cy, w, h, a):
    for i, off in enumerate((-0.3, 0.0, 0.3)):
        pts = [(cx - w / 2 + t / 60 * w, cy + off * h - math.sin(t / 60 * math.pi * 2 + i) * h * 0.18)
               for t in range(61)]
        _poly(d, pts, mix(a, (30, 30, 36), i * 0.18), width=int(2 * SS))


def g_freqshift(d, cx, cy, w, h, a):
    pts = [(cx - w / 2 + i / 60 * w * 0.72, cy + h * 0.18 - math.sin(i / 60 * math.pi * 3) * h * 0.28)
           for i in range(61)]
    _poly(d, pts, a)
    _poly(d, [(cx + w * 0.3, cy + h * 0.34), (cx + w * 0.3, cy - h * 0.4)], a, width=int(2.4 * SS))
    _poly(d, [(cx + w * 0.2, cy - h * 0.24), (cx + w * 0.3, cy - h * 0.44), (cx + w * 0.4, cy - h * 0.24)], a)


def g_detune(d, cx, cy, w, h, a):
    for i, off in enumerate((-0.16, 0.16)):
        pts = [(cx - w / 2 + t / 60 * w, cy + off * h - math.sin(t / 60 * math.pi * 2.2) * h * 0.22)
               for t in range(61)]
        _poly(d, pts, mix(a, (40, 40, 46), i * 0.3), width=int(2.2 * SS))
    f = F_PLATE(int(h * 0.44))
    d.text((cx + w * 0.36, cy - h * 0.3), "\u00b1", font=f, fill=a)


def g_quantize(d, cx, cy, w, h, a):
    for i in range(4):
        x0 = cx - w / 2 + i * w * 0.25
        y = cy + h * 0.34 - i * h * 0.22
        d.line((x0, y, x0 + w * 0.25, y), fill=a, width=int(2.4 * SS))
        if i < 3:
            d.line((x0 + w * 0.25, y, x0 + w * 0.25, y - h * 0.22), fill=shade(a, 0.5), width=max(1, int(1.4 * SS)))
    pts = [(cx - w / 2 + i / 60 * w, cy + h * 0.16 - i / 60 * h * 0.5 + math.sin(i / 6) * h * 0.07)
           for i in range(61)]
    _poly(d, pts, shade(a, 0.5), width=int(1.6 * SS))


def g_strings(d, cx, cy, w, h, a):
    for i in range(6):
        x = cx - w * 0.42 + i * w * 0.168
        d.line((x, cy - h * 0.44, x, cy + h * 0.44), fill=a, width=int(2.2 * SS))
        d.line((x + w * 0.055, cy - h * 0.44, x + w * 0.055, cy + h * 0.44), fill=shade(a, 0.5),
               width=max(1, int(1.2 * SS)))


def g_square(d, cx, cy, w, h, a):
    pts = []
    n = 4
    for i in range(n * 2 + 1):
        x = cx - w / 2 + w * i / (n * 2)
        y = cy + (h * 0.36 if (i // 1) % 2 == 0 else -h * 0.36)
        if pts:
            pts.append((x, pts[-1][1]))
        pts.append((x, y))
    _poly(d, pts, a)


def g_taps(d, cx, cy, w, h, a):
    for i in range(6):
        x = cx - w / 2 + w * (i + 0.5) / 6
        hh = h * 0.86 * (0.92 ** (i * 2.2))
        d.line((x, cy + h * 0.42, x, cy + h * 0.42 - hh), fill=mix(a, (28, 28, 32), i * 0.12), width=int(2.6 * SS))
    d.line((cx - w / 2, cy + h * 0.42, cx + w / 2, cy + h * 0.42), fill=shade(a, 0.45), width=max(1, int(1.4 * SS)))


def g_buckets(d, cx, cy, w, h, a):
    for i in range(5):
        x = cx - w * 0.4 + i * w * 0.2
        d.rounded_rectangle((x - w * 0.058, cy - h * 0.2, x + w * 0.058, cy + h * 0.28),
                            radius=w * 0.03, outline=mix(a, (30, 30, 34), i * 0.1), width=int(1.8 * SS))
        if i < 4:
            d.line((x + w * 0.058, cy + h * 0.04, x + w * 0.142, cy + h * 0.04), fill=shade(a, 0.6),
                   width=max(1, int(1.4 * SS)))


def g_grains(d, cx, cy, w, h, a):
    rnd = random.Random(7)
    for _ in range(34):
        t = rnd.random()
        x = cx - w / 2 + t * w
        y = cy + (rnd.random() - 0.5) * h * 0.9
        r = SS * (1.0 + rnd.random() * 2.2)
        d.ellipse((x - r, y - r, x + r, y + r), fill=mix(a, (28, 28, 34), rnd.random() * 0.55))


def g_pattern(d, cx, cy, w, h, a):
    n = 8
    for i in range(n):
        x = cx - w / 2 + w * (i + 0.5) / n
        if i % 2 == 0:
            d.rounded_rectangle((x - w * 0.04, cy - h * 0.34, x + w * 0.04, cy + h * 0.34),
                                radius=w * 0.02, fill=a)
        else:
            d.ellipse((x - w * 0.017, cy - w * 0.017, x + w * 0.017, cy + w * 0.017), fill=shade(a, 0.55))


def g_hall(d, cx, cy, w, h, a):
    d.arc((cx - w * 0.46, cy - h * 0.42, cx + w * 0.46, cy + h * 0.9), 180, 360, fill=a, width=int(2.4 * SS))
    d.line((cx - w * 0.46, cy + h * 0.24, cx - w * 0.46, cy + h * 0.44), fill=a, width=int(2.4 * SS))
    d.line((cx + w * 0.46, cy + h * 0.24, cx + w * 0.46, cy + h * 0.44), fill=a, width=int(2.4 * SS))
    for i, s in enumerate((0.62, 0.4)):
        d.arc((cx - w * 0.46 * s, cy - h * 0.42 * s, cx + w * 0.46 * s, cy + h * 0.9 * s), 180, 360,
              fill=shade(a, 0.6 - i * 0.18), width=int(1.8 * SS))


def g_impulse(d, cx, cy, w, h, a):
    d.line((cx - w * 0.36, cy + h * 0.42, cx - w * 0.36, cy - h * 0.42), fill=a, width=int(3 * SS))
    rnd = random.Random(3)
    for i in range(22):
        x = cx - w * 0.3 + i * w * 0.03
        hh = h * 0.8 * math.exp(-i * 0.16) * (0.4 + rnd.random() * 0.6)
        d.line((x, cy + h * 0.42, x, cy + h * 0.42 - hh), fill=mix(a, (28, 28, 32), 0.15 + i * 0.03),
               width=max(1, int(1.6 * SS)))


def g_gated(d, cx, cy, w, h, a):
    pts = []
    for i in range(45):
        t = i / 44
        pts.append((cx - w / 2 + t * w * 0.62, cy + h * 0.42 - h * 0.8 * math.exp(-t * 1.8)))
    _poly(d, pts, a)
    d.line((cx + w * 0.12, cy - h * 0.48, cx + w * 0.12, cy + h * 0.48), fill=mix(a, (255, 255, 255), 0.4),
           width=int(2.2 * SS))
    d.line((cx + w * 0.12, cy + h * 0.42, cx + w / 2, cy + h * 0.42), fill=shade(a, 0.45), width=int(1.8 * SS))


def g_swell(d, cx, cy, w, h, a):
    pts = [(cx - w / 2 + i / 44 * w, cy + h * 0.42 - h * 0.8 * ((i / 44) ** 2.4)) for i in range(45)]
    _poly(d, pts, a)
    _poly(d, [(cx + w * 0.2, cy - h * 0.34), (cx + w * 0.42, cy - h * 0.34)], a)
    _poly(d, [(cx + w * 0.28, cy - h * 0.44), (cx + w * 0.18, cy - h * 0.34), (cx + w * 0.28, cy - h * 0.24)], a)


def g_loop(d, cx, cy, w, h, a):
    r = min(w, h) * 0.46
    d.arc((cx - r, cy - r, cx + r, cy + r), 30, 300, fill=a, width=int(3.2 * SS))
    ang = math.radians(300)
    tipx, tipy = cx + math.cos(ang) * r, cy + math.sin(ang) * r
    # arrowhead tangent to the arc, so the loop reads as a direction
    t = math.radians(300 + 90)
    bx, by = math.cos(t) * r * 0.42, math.sin(t) * r * 0.42
    nx, ny = math.cos(math.radians(300)) * r * 0.3, math.sin(math.radians(300)) * r * 0.3
    d.polygon([(tipx + bx, tipy + by), (tipx - bx * 0.2 + nx, tipy - by * 0.2 + ny),
               (tipx - bx, tipy - by)], fill=a)


def g_midside(d, cx, cy, w, h, a):
    f = F_PLATE(int(h * 0.86))
    t = "M/S"
    d.text((cx - f.getlength(t) / 2, cy - h * 0.56), t, font=f, fill=a)


def g_treadle(d, cx, cy, w, h, a):
    _poly(d, [(cx - w * 0.44, cy + h * 0.42), (cx + w * 0.44, cy - h * 0.3), (cx + w * 0.44, cy + h * 0.42)],
          a, closed=True)
    for i in range(4):
        t = 0.18 + i * 0.2
        x = cx - w * 0.44 + t * w * 0.88
        y0 = cy + h * 0.42 - t * h * 0.72
        d.line((x, y0, x, cy + h * 0.42), fill=shade(a, 0.55), width=max(1, int(1.6 * SS)))


GLYPHS = {k[2:]: v for k, v in list(globals().items()) if k.startswith("g_")}


# ---------------------------------------------------------------------------
# Families
# ---------------------------------------------------------------------------


def knob_grid(d, spec, box, accent, r):
    """Knobs laid out the way RigArtwork.EnclosureColumns describes: <=4 in
    2 columns, 5-6 across three."""
    n = spec["knobs"]
    if n <= 0:
        return
    cols = 3 if n >= 5 else (2 if n >= 2 else 1)
    if n == 3:
        cols = 3
    rows = math.ceil(n / cols)
    x0, y0, x1, y1 = box
    for i in range(n):
        rr, cc = divmod(i, cols)
        in_row = min(cols, n - rr * cols)
        cw = (x1 - x0) / in_row
        cx = x0 + cw * (cc + 0.5)
        cy = y0 + (y1 - y0) / rows * (rr + 0.5)
        knob(d, cx, cy, r, accent, value=0.32 + ((i * 0.17) % 0.58))


def draw_stomp(spec, coat, accent):
    n = spec["knobs"]
    wide = n >= 5 or n == 3
    W, H = (336 if wide else 284), 396
    if spec.get("footswitches", 1) > 1:
        W, H = 348, 424
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    m = 16 * SS
    body = (m, 8 * SS, (W - 16) * SS, (H - 8) * SS)
    panel(d, body, coat, radius=13 * SS)
    for sx in (body[0] + 12 * SS, body[2] - 12 * SS):
        for sy in (body[1] + 12 * SS, body[3] - 12 * SS):
            screw(d, sx, sy, 5.4 * SS)
    jack(d, body[0] - 3 * SS, H * SS * 0.30, 9 * SS, True)
    jack(d, body[2] + 3 * SS, H * SS * 0.30, 9 * SS, False)

    badge(d, body[0] + 22 * SS, body[1] + 21 * SS, spec["badge"], accent)
    kr = (20 if n >= 5 else 26) * SS
    ktop = body[1] + 40 * SS
    krows = math.ceil(n / (3 if n >= 5 or n == 3 else 2)) if n else 0
    kh = krows * (kr * 2.9)
    knob_grid(d, spec, (body[0] + 20 * SS, ktop, body[2] - 20 * SS, ktop + kh), accent, kr)

    gy = ktop + kh + 26 * SS
    gh = 40 * SS
    GLYPHS[spec["glyph"]](d, (body[0] + body[2]) / 2, gy + gh / 2, (body[2] - body[0]) * 0.46, gh, accent)

    fs = spec.get("footswitches", 1)
    plate_y = body[3] - (fs > 1 and 104 or 92) * SS
    plate(d, (body[0] + 18 * SS, plate_y, body[2] - 18 * SS, plate_y + 26 * SS), spec["name"], accent, coat)
    if fs > 1:
        for i, sx in enumerate((-1, 1)):
            footswitch(d, (body[0] + body[2]) / 2 + sx * 58 * SS, body[3] - 48 * SS, 27 * SS)
            led(d, (body[0] + body[2]) / 2 + sx * 58 * SS, plate_y + 40 * SS, 4.2 * SS,
                accent if i else (255, 120, 110))
    else:
        led(d, (body[0] + body[2]) / 2, plate_y + 40 * SS, 5 * SS, accent)
        footswitch(d, (body[0] + body[2]) / 2, body[3] - 36 * SS, 22 * SS)
    brand(d, (body[0] + body[2]) / 2, body[3] - 14 * SS, accent)
    return img


def draw_head(spec, coat, accent):
    W, H = 480, 224
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    body = (10 * SS, 22 * SS, (W - 10) * SS, (H - 8) * SS)
    panel(d, body, shade(coat, 0.55), radius=8 * SS)
    # handle
    d.rounded_rectangle((W * SS * 0.42, 8 * SS, W * SS * 0.58, 26 * SS), radius=6 * SS,
                        outline=(46, 42, 38), width=4 * SS)
    # grille cloth
    grille = (28 * SS, 38 * SS, (W - 28) * SS, 122 * SS)
    d.rounded_rectangle(grille, radius=4 * SS, fill=shade(coat, 0.34), outline=mix(accent, (30, 28, 26), 0.45),
                        width=int(1.6 * SS))
    step = 4 * SS
    x = grille[0] + step
    while x < grille[2]:
        d.line((x, grille[1] + 2 * SS, x, grille[3] - 2 * SS), fill=shade(coat, 0.46), width=max(1, SS // 2))
        x += step
    y = grille[1] + step
    while y < grille[3]:
        d.line((grille[0] + 2 * SS, y, grille[2] - 2 * SS, y), fill=shade(coat, 0.28), width=max(1, SS // 2))
        y += step
    GLYPHS[spec["glyph"]](d, W * SS * 0.5, (grille[1] + grille[3]) / 2, W * SS * 0.24, 46 * SS,
                          mix(accent, (255, 255, 255), 0.15))
    # faceplate
    face = (28 * SS, 132 * SS, (W - 28) * SS, (H - 20) * SS)
    fg = vgradient((int(face[2] - face[0]), int(face[3] - face[1])), mix(accent, (255, 255, 255), 0.25),
                   shade(accent, 0.55))
    img.paste(fg, (int(face[0]), int(face[1])))
    d.rectangle(face, outline=shade(coat, 0.3), width=int(1.6 * SS))
    n = spec["knobs"]
    for i in range(n):
        cx = face[0] + (face[2] - face[0]) * (i + 0.5) / (n + 1.2)
        knob(d, cx, (face[1] + face[3]) / 2 + 2 * SS, 16 * SS, (40, 40, 44), value=0.3 + (i * 0.19) % 0.6,
             ticks=False)
    led(d, face[2] - 22 * SS, (face[1] + face[3]) / 2, 7 * SS, (255, 170, 60))
    f = F_PLATE(int(13 * SS))
    d.text((face[0] + 10 * SS, face[1] + 5 * SS), spec["name"], font=f, fill=shade(accent, 0.35))
    badge(d, face[0] + 10 * SS, face[3] - 16 * SS, spec["badge"], shade(accent, 0.4))
    return img


def draw_cab(spec, coat, accent):
    W, H = 480, 378
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    body = (36 * SS, 16 * SS, (W - 36) * SS, (H - 16) * SS)
    panel(d, body, coat, radius=7 * SS)
    grille = (56 * SS, 36 * SS, (W - 56) * SS, (H - 54) * SS)
    d.rectangle(grille, fill=(22, 20, 19), outline=mix(accent, (40, 34, 28), 0.4), width=int(2 * SS))
    step = 4 * SS
    x = grille[0]
    while x < grille[2]:
        d.line((x, grille[1], x, grille[3]), fill=(32, 29, 27), width=max(1, SS // 2))
        x += step
    y = grille[1]
    while y < grille[3]:
        d.line((grille[0], y, grille[2], y), fill=(14, 13, 12), width=max(1, SS // 2))
        y += step
    for sx in (-1, 1):
        ccx = (grille[0] + grille[2]) / 2 + sx * (grille[2] - grille[0]) * 0.24
        ccy = (grille[1] + grille[3]) / 2
        r = (grille[3] - grille[1]) * 0.3
        for i in range(int(r), 0, -3):
            t = i / r
            d.ellipse((ccx - i, ccy - i, ccx + i, ccy + i), fill=mix((44, 40, 38), (16, 15, 14), t))
        d.ellipse((ccx - r * 0.3, ccy - r * 0.3, ccx + r * 0.3, ccy + r * 0.3), fill=(26, 24, 23))
    GLYPHS["mic"](d, (grille[0] + grille[2]) / 2, (grille[1] + grille[3]) / 2, 96 * SS, 150 * SS,
                  mix(accent, (255, 255, 255), 0.3))
    for sx in (body[0] + 14 * SS, body[2] - 14 * SS):
        for sy in (body[1] + 14 * SS, body[3] - 14 * SS):
            screw(d, sx, sy, 5.2 * SS)
    plate(d, (W * SS * 0.3, body[3] - 34 * SS, W * SS * 0.7, body[3] - 10 * SS), spec["name"], accent, coat)
    badge(d, body[0] + 26 * SS, body[1] + 12 * SS, spec["badge"], accent)
    return img


def draw_rack(spec, coat, accent):
    W, H = 448, 246
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    body = (8 * SS, 26 * SS, (W - 8) * SS, (H - 26) * SS)
    panel(d, body, coat, radius=5 * SS)
    # rack ears
    for x in (body[0] + 22 * SS, body[2] - 22 * SS):
        for y in (body[1] + 22 * SS, body[3] - 22 * SS):
            screw(d, x, y, 6.4 * SS)
    d.line((body[0] + 44 * SS, body[1], body[0] + 44 * SS, body[3]), fill=shade(coat, 0.45), width=max(1, SS))
    d.line((body[2] - 44 * SS, body[1], body[2] - 44 * SS, body[3]), fill=shade(coat, 0.45), width=max(1, SS))
    # display window
    disp = (body[0] + 60 * SS, body[1] + 22 * SS, body[0] + 200 * SS, body[3] - 22 * SS)
    d.rounded_rectangle(disp, radius=4 * SS, fill=(11, 14, 16), outline=shade(coat, 1.4), width=int(1.4 * SS))
    GLYPHS[spec["glyph"]](d, (disp[0] + disp[2]) / 2, (disp[1] + disp[3]) / 2 - 6 * SS,
                          (disp[2] - disp[0]) * 0.66, (disp[3] - disp[1]) * 0.5, accent)
    f = F_LABEL(int(9 * SS))
    d.text((disp[0] + 8 * SS, disp[3] - 18 * SS), spec["badge"], font=f, fill=shade(accent, 0.6))
    n = spec["knobs"]
    top = body[1] + 30 * SS
    for i in range(n):
        cx = body[0] + 224 * SS + (i % 3) * 48 * SS
        cy = top + (i // 3) * 62 * SS
        knob(d, cx, cy, 19 * SS, accent, value=0.28 + (i * 0.21) % 0.62, skirt=False)
    led(d, body[2] - 26 * SS, body[1] + 16 * SS, 4.6 * SS, accent)
    plate(d, (body[0] + 60 * SS, body[3] + 2 * SS, body[0] + 200 * SS, body[3] + 20 * SS), spec["name"],
          accent, coat)
    brand(d, W * SS * 0.5, 8 * SS, accent)
    return img


def draw_treadle(spec, coat, accent):
    """A top-down expression/volume pedal, framed like `wah-auto-wah.webp`:
    the ribbed treadle fills most of the chassis, toe at the top."""
    W, H = 256, 428
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    base = (14 * SS, 10 * SS, (W - 14) * SS, (H - 10) * SS)
    panel(d, base, coat, radius=11 * SS)
    # treadle plate: a slight taper toward the toe, hinged at the heel
    tread = [(34 * SS, 316 * SS), ((W - 34) * SS, 316 * SS), ((W - 44) * SS, 40 * SS), (44 * SS, 40 * SS)]
    d.polygon(tread, fill=shade(coat, 1.32), outline=shade(coat, 0.42))
    d.polygon([(p[0], p[1]) for p in tread], outline=shade(coat, 0.35))
    ribs = 13
    for i in range(ribs):
        t = (i + 0.5) / ribs
        y = 316 * SS - t * 276 * SS
        inset = (34 + t * 10) * SS + 6 * SS
        d.line((inset, y, (W * SS) - inset, y), fill=shade(coat, 1.55), width=int(4.2 * SS))
        d.line((inset, y + 4.4 * SS, (W * SS) - inset, y + 4.4 * SS), fill=shade(coat, 0.6),
               width=int(2.4 * SS))
    GLYPHS["treadle"](d, W * SS * 0.5, 180 * SS, W * SS * 0.44, 92 * SS, accent)
    jack(d, base[0] - 3 * SS, 348 * SS, 9 * SS, True)
    jack(d, base[2] + 3 * SS, 348 * SS, 9 * SS, False)
    led(d, W * SS * 0.5, 336 * SS, 5 * SS, accent)
    plate(d, (38 * SS, 356 * SS, (W - 38) * SS, 384 * SS), spec["name"], accent, coat)
    badge(d, 38 * SS, 390 * SS, spec["badge"], accent)
    for sx in (base[0] + 13 * SS, base[2] - 13 * SS):
        screw(d, sx, base[3] - 13 * SS, 5.4 * SS)
        screw(d, sx, base[1] + 13 * SS, 5.4 * SS)
    brand(d, W * SS * 0.5, 404 * SS, accent)
    return img


FAMILIES = {"stomp": draw_stomp, "head": draw_head, "cab": draw_cab, "rack": draw_rack, "treadle": draw_treadle}


def render(spec) -> Image.Image:
    coat, accent = rgb(spec["coat"]), rgb(spec["accent"])
    img = FAMILIES[spec["family"]](spec, coat, accent)
    img = grain(img, 34, hash(spec["id"]) & 0xFFFF)
    img = img.resize((img.width // SS, img.height // SS), Image.LANCZOS)
    img = img.filter(ImageFilter.UnsharpMask(radius=1.1, percent=48, threshold=2))
    return img.convert("RGB")


# ---------------------------------------------------------------------------


def contact_sheet(images, path, cols=7):
    cell_w = max(i.width for i, _ in images) + 16
    cell_h = max(i.height for i, _ in images) + 30
    rows = math.ceil(len(images) / cols)
    sheet = Image.new("RGB", (cell_w * cols, cell_h * rows), (10, 10, 13))
    d = ImageDraw.Draw(sheet)
    f = ImageFont.truetype(os.path.join(FONT_DIR, "segoeui.ttf"), 12)
    for i, (im, spec) in enumerate(images):
        r, c = divmod(i, cols)
        x = c * cell_w + (cell_w - im.width) // 2
        y = r * cell_h + (cell_h - 22 - im.height) // 2
        sheet.paste(im, (x, y))
        label = f"{spec['id']}  ({spec['base']})"
        d.text((c * cell_w + cell_w / 2 - f.getlength(label) / 2, (r + 1) * cell_h - 18), label,
               font=f, fill=(170, 172, 180))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sheet.save(path)
    return sheet.size


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.abspath(os.path.join(here, "..", ".."))
    out_dir = os.path.join(repo, "DotCharter.UI", "Assets", "Rig", "Art")

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default=out_dir)
    ap.add_argument("--only", default="", help="comma-separated module ids")
    ap.add_argument("--quality", type=int, default=95)
    # The scratch folder is the owner's sibling `DotCharter-local`, which is
    # OUTSIDE the repo — walk up until it turns up (a worktree sits deeper).
    scratch = os.path.join(repo, "gear-art-contact-sheet.png")
    probe = repo
    while True:
        cand = os.path.join(os.path.dirname(probe), "DotCharter-local", "scratch")
        if os.path.isdir(cand):
            scratch = os.path.join(cand, "gear-art-contact-sheet.png")
            break
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    ap.add_argument("--sheet", default=scratch)
    ap.add_argument("--no-sheet", action="store_true")
    ap.add_argument("--sheet-only", action="store_true")
    ap.add_argument("--print-cs", action="store_true", help="print the RigArtwork rows for these specs")
    args = ap.parse_args()

    wanted = {s.strip() for s in args.only.split(",") if s.strip()}
    specs = [s for s in SPECS if not wanted or s["id"] in wanted]

    rendered = []
    for spec in specs:
        im = render(spec)
        rendered.append((im, spec))
        if not args.sheet_only:
            path = os.path.join(args.out, spec["base"] + ".webp")
            im.save(path, "WEBP", quality=args.quality, method=6, lossless=False, exact=False)
            print(f"{spec['id']:14s} -> {spec['base']}.webp  {im.width}x{im.height}  "
                  f"{os.path.getsize(path) // 1024} KB")

    if not args.no_sheet:
        print("contact sheet:", args.sheet, contact_sheet(rendered, args.sheet))

    if args.print_cs:
        print("\n// Illustrations")
        for s in specs:
            print(f'        ["{s["id"]}"] = "{s["base"]}",')
        print("\n// Finishes")
        for s in specs:
            print(f'        ["{s["id"]}"] = (0x{s["coat"]:06x}, 0x{s["accent"]:06x}),')
    return 0


if __name__ == "__main__":
    sys.exit(main())
