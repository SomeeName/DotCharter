# DotCharter app mark

The amber guitar pick contains a dark violet chart highway and one chart dot.
`app-icon-master.png` is the approved transparent ImageGen output. Keep it as
the source; do not flatten the alpha channel or replace it with a screenshot.

Run `python tools/branding/build_icons.py` (Pillow required) to export the shared
256px PNG and nine Windows ICO frames, from 16px to 256px. The existing app,
window, launch screens and About dialog already reference these assets.
The exporter also writes an ignored review sheet to `artifacts/app-icon-preview.png`.
It validates frame sizes and transparency. Visual identity is reviewed at actual
16/24/32/48/64px sizes; there is no new application behavior to unit-test.

## Generation provenance

Built-in `image_gen` tool, `logo-brand`, 2026-09-05. The generated source is raster;
no traced vector is claimed. Final prompt:

> Use case: logo-brand. Create one final production-ready app icon for DotCharter,
> a professional guitar chart editor and rhythm-playing desktop app. A bold,
> distinctive amber guitar-pick silhouette containing an extremely simple
> dark-violet negative-space angular chart/highway motif: three broad descending
> stepped lanes, with one round amber chart dot integrated in the negative space.
> The silhouette and carved motif should suggest a subtle D, not a literal letter
> pasted on. Flat crisp vector-like geometry, excellent optical balance, minimal
> number of shapes, memorable at 16 and 32 pixels. Warm amber #FFBE57 and very dark
> violet #171320 only, no gradients, glow, shadows, bevel, texture or lettering.
> Center a single mark filling 85% of a square canvas, with generous separation
> between internal shapes. Actual transparent background outside the pick; no
> enclosing square tile, no mockup, no palette, no caption, no thin guitar strings
> or generic music notes. Render 1024x1024.

The tool returned a 1254px RGBA image with subtle amber variation; it was retained
as delivered. Export performs only format conversion and resampling.
