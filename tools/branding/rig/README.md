# Rig hardware artwork

Generated with the built-in ImageGen tool on 2026-09-05. The owner's two amp-rig
screenshots informed the cabinet; the cabinet then supplied the amp's material
reference. Masters retain the generated alpha channel. These are artwork layers:
amp controls and all text are native UI.

`python tools/branding/build_rig_art.py` exports the amp at 1600px wide and the
cabinet at 256px wide to `DotCharter.UI/Assets/Rig/`. Pillow performs only PNG
format export and proportional resampling. The amp export is 1600x800 including
transparent margins; the visible chassis occupies approximately y=152..648 and
x=14..1588 (roughly 3.2:1). Its empty woven face accepts a native dark translucent
overlay and live controls. The generated screws are decorative hardware.

## Prompt specifications

- **Amp:** realistic straight-on orthographic empty guitar amplifier enclosure,
  wide 5:1 silhouette, black pebbled tolex case, rounded reinforced black corners,
  thin muted champagne-metal trim, recessed charcoal textured face; subtle studio
  edge highlights with faint violet reflection. Empty central face for native
  controls. No knobs, holes, jacks, pilot lights, switches, labels, text, branding,
  cables, feet, handles or cast shadow. Genuine transparent background.
- The requested 5:1 framing was approximate: the accepted transparent generation
  returned a deeper chassis with a woven face. Attempts to edit it to a smooth
  metal face flattened alpha to a painted checkerboard and were rejected. The
  genuine-alpha enclosure is preserved intact rather than synthesizing a mask.
- **Cabinet:** square front-view guitar speaker cabinet occupying 90% of canvas,
  black pebbled tolex, reinforced rounded corners, fine black woven grille with
  one speaker cone subtly visible behind it, matching champagne trim and subdued
  studio highlights. Bold silhouette readable at 48px. No brand, text, controls,
  cables, feet, handle, shadow or other objects. Genuine transparent background.

The source assets are raster artwork, not vector paths. Export checks dimensions
and alpha; appearance and absence of baked controls are visually reviewed.

## Final amp generation prompt

> Generate a transparent PNG product asset with actual alpha transparency, using
> the supplied transparent speaker cabinet solely as the material/style reference.
> A guitar AMPLIFIER HEAD front panel enclosure: a wide horizontal rectangle FIVE
> times wider than tall, black pebbled leather/tolex exterior, thin champagne piping
> around a dark charcoal EMPTY textured control face, reinforced rounded black
> corners. Straight-on front elevation, entirely flat orientation, perfectly
> horizontal. No speaker, no grille, no circle, no knobs, no controls, no switches,
> no holes, no labels, no logo, no text, no handle, no feet. High fidelity real
> materials matching the reference. The central panel must be wholly empty so
> application controls can be drawn on top. One object centered on a square
> TRANSPARENT canvas; only the chassis opaque, all surrounding pixels alpha=0.
> Do not draw a white or checkered background.
