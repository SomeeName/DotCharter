# Graphite — calm studio neutral

The app's one theme. Its tokens are materialised in `ThemePresets.Default` (`DotCharter.UI/Theming/ThemePresets.cs`); this file is the spec they were drawn from, and the design-system guide ([../fire-design-system.md](../fire-design-system.md)) lists the rules that override the Fire mockups.

**Character.** A mixing-desk / DAW look: cool graphite greys with almost no hue, off-white (not white) text, and ONE sky-blue accent that only marks selection and the primary action. Flat: no glow, no wash, no drop shadow under panels (only popovers keep a small one). The quietest of the four — the chart and the highway are the colourful things on screen, the chrome steps back.

## Palette

| Role | Value | Role | Value |
|---|---|---|---|
| ground (screen) | `#111317` | text primary | `#E8EBEF` |
| chrome (rails, bars) | `#15181D` | text body | `#D3D8DF` |
| card | `#1B1F25` | text secondary | `#AEB6C1` |
| elevated / popover | `#232830` | text muted | `#929BA8` |
| strong surface | `#323946` | text dim | `#7A8390` |
| hairline border | ink `#E8EEF6` at 9 % | text faint | `#687080` |
| accent | `#7AB6FF` | on accent | `#0A1522` (dark ink) |
| accent strong / deep | `#5E9FF2` / `#3C7BD1` | success / warning / danger | `#5BD08F` / `#F2B557` / `#F07A7A` |
| strings E A D G B e | `#F0625E` `#F2CF4A` `#4F9BF2` `#F2913A` `#4FCB70` `#A67CF2` | info | `#7AB6FF` |

## Contrast (WCAG 2.x, on ground / chrome / card / popover)

| Tier | Ratio | Target |
|---|---|---|
| primary | 15.55 / 14.88 / 14.57 / 12.39 | 4.5 |
| secondary | 9.09 / 8.69 / 8.51 / 7.24 | 4.5 |
| muted | 6.62 / 6.33 / 6.20 / 5.27 | 4.5 |
| dim | 4.85 / 4.64 / 4.54 / 3.86 | 3.0 (passes 4.5 on ground) |
| faint | 3.73 / 3.57 / 3.50 / 2.98 | 3.0 on ground (current look: 2.38) |
| accent / danger as text | 8.82 / 6.88 on ground | 3.0 |
| text on the primary action | 8.71 | 4.5 |

Nothing below target. The only sub-3 number is faint on a popover (2.98), which is the disabled tier.

## Type

- **Atkinson Hyperlegible** (Braille Institute, SIL OFL 1.1, from google/fonts `ofl/atkinsonhyperlegible`) for display AND running text — drawn for low-vision readers, with unambiguous `Il1`, `0O`, open counters. Chosen for "legible at a glance from the guitar stool".
- **JetBrains Mono** (JetBrains, SIL OFL 1.1, from github.com/JetBrains/JetBrainsMono) for figures, times, tunings — tabular, slashed zero, reads well at 11 px.
- Scale (Fire ladder): eyebrow 9.5 → 11, mono 10.5 → 11.5, row 13.5 → 14.5, base 13 → 14, screen title 22 → 24, stat 28 → 30. Every sub-11 px step is raised to ≥ 10 px; line heights raised with them. Tracking cut to 0.3–0.6 px (tracked caps are a "Fire" signature, not a need).

## Shape

Soft-square: card 8, panel 10, screen 12, control 8, field / row / segment 6, tag 4, micro 3; pills stay pills. The lit action is an 8 px rounded rectangle (no chamfer, no glow, no hover lift). Borders 1 px hairline at 9 % / 14 %. Focus ring 2 px accent-pale.

## Rationale and trade-offs

Built for long sessions in a dim room: low chroma everywhere except the one accent and the data colours, so the eye is not pulled by chrome; highest body-text legibility of the set; no glow to bloom on a dark screen. **Good at**: dense editor surfaces, long lists, reading numbers mid-song, the three languages (Atkinson has full Latin incl. ß/umlauts; CJK falls back to Yu Gothic UI, which sits comfortably next to it). **Bad at**: excitement — it reads as a tool, not a game; the Results and Start screens lose some "moment". Blue accent is also the most common accent in dark apps, so it is the least distinctive.
