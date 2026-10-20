# Fire — where the layer lives, the spec it was corrected against, and typography

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

## Where it lives

- `DotCharter.Tone.UI/Styles/FireTokens.axaml` — brushes, fonts, ladders. Loaded
  right after `Resources.axaml`.
- `DotCharter.Tone.UI/Styles/FireMenuChrome.axaml` — a plain `ResourceDictionary`
  restating FLUENT's popup keys in Fire values. Merged by
  `Views/Shell/ShellChromeStyles.axaml` and (0.50)
  `Views/Editor/InspectorStyles.axaml`, i.e. scoped to the two converted
  surfaces that open dropdowns — a resource lookup from inside a popup walks up
  to the control that opened it, so merging it into a grammar scopes it to that
  grammar's views. It exists because a dropdown's fill cannot be reached by a
  style selector at all; read its header before touching a menu, a flyout or a
  ComboBox popup.
- `DotCharter.Tone.UI/Styles/FireControls.axaml` — the tokens the four shared
  CUSTOM-DRAWN controls need on top of that ladder: the **eight fixed
  gear-category identity colours** and the knob dial. Its own file because it
  and `FireTokens.axaml` were authored concurrently and two blocks appended to
  one `<Styles.Resources>` merge cleanly in git while producing a duplicate
  `x:Key` — a clean build that crashes at `App.Initialize()`. Merged
  dictionaries resolve last-wins instead.
- `DotCharter.Tone.UI/Styles/Fire.axaml` — the component classes and the four
- `DotCharter.Tone.UI/Styles/Fire.axaml` — the component classes and the
  `ControlTheme`s. Loaded **last** in `Theme.axaml`, so a `Fire*` class wins
  over a generic one on a control wearing both.
- `DotCharter.UI/Controls/Fire/` — the four shared custom-drawn controls (see
  [custom-controls.md](custom-controls.md)).
- `DotCharter.UI/Assets/Fonts/` — six TTFs + three OFL licences.
- `DotCharter.Tone.UI/Converters/FireChamferClipConverter.cs` — the primary action's
  chamfered silhouette (see [vocabulary.md](vocabulary.md) § "The chamfer"). The
  layer's only piece of code.

## The written spec, and what it corrected

`New Menu/Implementation Spec.dc.html` was the owner's own extraction of the
mockups — the values, and the rules behind them. **Where it and this layer
disagreed, it won.**

**That file no longer exists (verified 2026-08-23)** — see
[fire-design-system.md](../fire-design-system.md). The table below is now the
surviving record of what it said, so it can no longer be re-read to settle a
disagreement: treat these values as authoritative rather than as a summary of
something checkable.

Eleven token values were corrected in place. Each was invisible — it built,
bound and rendered; only a side-by-side with the mockup showed it:

| Token | was | now | why |
|---|---|---|---|
| `FireBorder` | white .10 | **.08** | the spec's ONE hairline, "default 1px border everywhere" |
| `FireBorderStrong` | *(absent)* | **.12** | the spec's second weight: inputs, icon buttons, dividers. One weight was doing both jobs |
| `FireBorderRow` | .04 | **.05** | the data-list row's bottom border |
| `FireRailFill` | .55 → .40 | **.66 → .72** | Surface/glass. The port faded DOWNWARD; the mockups get denser toward the floor |
| `FireAccentBorderSoft` | `a78bfa` .34 | **`8b5cf6` .34** | the spec's hover edge is violet MID |
| `FireSuccessTint` | .07 | **.08** | under the spec's 8–14 % chip band, so a green pill read as chrome |
| `FireShadowPanel` | `0 26 60 -22` @70 % | **`0 26 56 -28` @100 %** | the spec's card elevation |
| `FireGlowAction` | `0 14 36 -14` | **`0 24 60 -20`** | the spec's ONE glow. It existed twice at two sizes (`FireGlowActionHero`, now gone), so a lit button's brightness depended on which key a screen picked |
| `FireSpaceMd` | 9 | **10** | 9 is on no step of the spec ladder |
| `FirePadRow` (on a data row) | 12,10 | **`FirePadListRow` 22,13** | the spec's data-list inset. A nav row's inset is not a board row's |
| `FireRadiusCut` `0,16,0,16` | — | **deleted** | the chamfer is a clip geometry now, not a rounding |

Added, because nothing carried them: `FireSurfaceChrome` / `FireSurfaceChromeAlt`,
`FireGlassInset`, the focus set (`FireFocusRingThickness` / `FireFocusRingOffset`
/ `FireFocusRowFill` / `FireFocusRowBorder`), the **fixed identities**
(`FireString{6E,5A,4D,3G,2B,1e}` and `FireGear{Dynamics,Amps,Cabinets,Drive,Eq,
Modulation,DelayReverb,Utility}`), `FireOverlayFill`, the five missing spacing
steps, `FireSizeNano{,Sm}` / `FireSizeCardTitle` / `FireSizeDisplayLg`,
`FireTrackMicro` / `FireTrackAction`, `FireColValue{Sm,,Lg}`, `FireChipHeight`,
the `FireMedal*` metals, `FireSheen{Hover,Pressed}` and the `FireMotion*`
durations.

**The spec's token names are not the keys here.** Most already had a home under
a job-shaped name and were not duplicated; `FireTokens.axaml`'s header carries
the twenty-row mapping table. Two traps in it: `FireSurfaceRaised` (.03) is NOT
the spec's "Surface / raised" (that is `FireSurfaceField`, .04), and
`FireSizeMicro` (10) is the spec's mono/STATUS step, not its mono/micro one
(that is `FireSizeNano`, 8.5).

### The fixed identities

Six string colours, eight gear-category colours and — since 0.51 — seven
SEPARATED-STEM colours (`FireStem{Guitar,Bass,Drums,Vocals,Piano,Other,Mix}`,
from `Chart Editor.dc.html`'s mixer). The first two sets are marked "FIXED, DO
NOT REASSIGN" in the spec.

The stem set reuses four of those values under its own names on purpose: a
mixer row is a third positional domain — a labelled fader in one column — where
nothing competes for the meaning, exactly the argument the spec makes for Amps
sharing amber with the G string and EQ sharing green with the B string. Naming
them separately is what stops a reader wondering why the bass stem is the D
string, and lets either set be corrected without moving the other. The mapping
is `Converters/StemIdentity.TokenFor(name, isMasterMix)` and it keys on the
PAIR: the master strip's display name is the literal "Original", and an unknown
stem (anything the user dropped into a project's own `stems/` folder) takes the
neutral slate rather than borrowing an instrument's identity. Pinned by
`UI/StemIdentityTests`, including that the seven are seven distinct colours and
that every token the mapping can return is declared here — a missing key paints
nothing at all. They are **identities, not palette entries**: a string is
its colour on the highway, in the gutter, in the tuner and on a chord chart; a
gear category is its colour on the board, in the gear list and on every tone
chip. Amps share amber and EQ shares green with the signal colours on purpose —
the rig reads them positionally, where nothing competes for the meaning.
Exact values pinned by `DotCharter.Tests/UI/FireTokenValueTests`, which also
proves the six strings are six DISTINCT colours.

## Typography

| Family | Job | Never |
|---|---|---|
| **Chakra Petch** | screen titles, big figures, the wordmark, primary-button captions, the big segmented tab captions | running text |
| **Space Grotesk** | every piece of running UI text — row titles (600), descriptions (400), button labels (600) | numbers ≥16 px |
| **IBM Plex Mono** | eyebrows, figures, metadata, tags, status text. **Always uppercase, always letter-spaced.** | a sentence |

Correcting a common misreading: Chakra Petch is **not** display-only — it also
labels tabs and CTAs at 600 with `.08em`–`.16em` tracking. And big figures are
**Chakra 700 with `line-height:1`**, not mono; mono owns the *medium* numerics
(scores, deltas, day numbers, 11–13.5 px).

### Non-Latin text: the fallback chain (`Platform/TextFallbackFonts`)

All three families are Latin-only, so every non-Latin character in a song title,
artist or album is resolved by Avalonia's font manager, not by the theme.
`Program.BuildAvaloniaApp` hands it `TextFallbackFonts.Options()`.

**Two things measured on Windows 11 / Avalonia 12.1.1, 2026-08-21 — do not
re-derive them from first principles:**

- **The chain is NOT what makes CJK render, and claiming it fixes tofu is
  wrong.** Skia's matcher already resolves `八`/`ヨ` to Yu Gothic UI and `한` to
  Malgun Gothic with **no chain configured at all**. The chain was added while
  investigating an owner report of "mangled" Japanese metadata and that report is
  *not* explained by it — the tags were read correctly (`artist='ヨルシカ'` in
  the log) and CJK resolves either way.
- **What it does fix is the arbitrary pick for everything else, and the pick is
  CACHE-ORDER dependent.** Asked cold, the matcher returns Segoe UI for `é`;
  asked right after a Hangul match — the ordinary case once the app has rendered
  one Japanese title — it returns **Malgun Gothic**, a Korean face, for both `é`
  and `Ж`. The chain makes it deterministic.

**Order is priority and the matcher takes the FIRST family that HAS the glyph, so
broad Latin/Cyrillic faces go first** (Segoe UI carries no CJK and cannot steal
one) **and the CJK faces follow.** CJK-first is the trap: those faces carry
accented Latin and Cyrillic too, so they capture them — which is exactly what the
first cut of this chain did. `UI/TextFallbackFontTests` pins the ordering in the
default gate; `Perf/TextFallbackUiPerfSmokeTests` is the runtime half and needs
`DOTCHARTER_UI_SWEEP_DUMP=1` (headless *drawing* has a stub font manager with no
system faces, so every match fails there).

### The bundled cuts, and why there are only two per family

`Assets/Fonts` ships **Regular + Bold** of each family and nothing else.
Google's static Medium/SemiBold instances declare nameID 1 =
`"Space Grotesk SemiBold"` — a family of their **own** — so dropping them in
would look like adding two weights while `FontWeight="SemiBold"` still resolved
to the same face. Two cuts that always resolve beat four that resolve
inconsistently.

The mockups' 500/600 therefore render as **Bold**, named once as
`FireWeightUi` / `FireWeightDisplay` so a future four-weight bundle is a
one-line change.

Guarded twice, because a missing font is the quietest failure in the theme (no
warning, no exception, the text just renders in the fallback):
`DotCharter.Tests/UI/FireFontAssetTests` (default gate — parses each TTF's own
`name` table and matches it against what the token asks for, in both
directions) and `DotCharter.Tests/Perf/FireFontResolutionUiPerfSmokeTests` (the
real `FontManager`; Bold and Regular must come back as different faces).

**Nothing is fetched at runtime.** The mockups `<link>` a font CDN; a lint in
the asset test fails if any `.axaml` ever does.

