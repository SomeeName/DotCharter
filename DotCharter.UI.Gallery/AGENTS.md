# DotCharter.UI.Gallery Agent Guide

Standalone Avalonia desktop app (assembly `DotCharter.Gallery`) that hosts UI
screens bound to FAKE view models. No audio, no JUCE engine, no DI composition
root. It is where the UI redesign is built screen by screen before each screen
is swapped into the real app. **Permanent, not a throwaway.**

References `DotCharter.UI` (views, styles), `DotCharter.Presentation` (the screen
contracts) and `DotCharter.Presentation.Fakes` (their fakes, each with a static
`Variants` list). This project and the tests are the only hosts allowed the fakes;
`DotCharter.UI` may never reference them (`Infrastructure/AssemblyLayeringTests`).

## Files

| File | What it is |
|---|---|
| `Program.cs` | Entry point + `AppBuilder`. Mirrors `DotCharter.UI.Program`'s font setup (Inter + `Platform.TextFallbackFonts`). Parses `--capture <dir>`. |
| `App.axaml` / `App.axaml.cs` | `GalleryApp` — its own tiny `Application`. NOT `DotCharter.UI.App`. |
| `GalleryWindow.axaml` | The shell: section/page nav on the left, variant strip on top, page host on the right. |
| `GalleryViewModel.cs` | `GallerySection` → `GalleryPageItem` → `GalleryVariantItem`. Selecting a variant REBUILDS the control. |
| `GalleryCapture.cs` | `--capture` implementation. Renders each page × variant at 1600×900 to PNG. |
| `Pages/GalleryPage.cs` | `GalleryPage`, `GalleryVariant`, `GalleryCatalog.All` — the hand-written design-system pages, then every screen derived from the catalog. |
| `Pages/Screens/ScreenPages.cs` | **DERIVED, never listed by hand**: one page per `DotCharter.UI/Views/Shell/ScreenCatalog.cs` entry, one variant per entry in its fake's `Variants` (`FakeScreens.VariantsOf`). A dialog is framed as `DialogHost.Preview` frames it (ground + shared scrim). `ScreenPages.Build` is shared with `Perf/ScreenGoldenUiPerfSmokeTests`, so a gallery page and its golden are the same render. |
| `Pages/<Area>/<Area>Pages.cs` | Hand-written pages that are NOT catalog screens (`DesignSystemPages` today) — the "overrides" of target-architecture.md § 2. |
| `Pages/Tokens/DesignTokensPage.axaml(.cs)` | First page: colour swatches, type scale, spacing bars, button classes, input controls. |
| `Pages/Tokens/ThemeTokenSources.cs` | Reads the product's token dictionaries back at runtime. |

## Theme sharing — the rule

- **Every style, control theme and token the gallery renders is the PRODUCTION
  one from `DotCharter.UI`.** Nothing designed here has to be ported.
- The colour tokens live in `DotCharter.Tone.UI/Styles/ColorTokens.axaml`. BOTH
  `App.axaml` files merge that one dictionary through
  `ResourceDictionary.MergedDictionaries` + `ResourceInclude`.
- `ThemeService` writes `Application.Current.Resources[key]`. An entry on the
  dictionary itself is looked up before its merged dictionaries, so that write
  shadows the merged default and runtime theme switching still works —
  `Perf/ThemeTokenOverrideUiPerfSmokeTests` proves it end to end (token AND the
  brush over it).
- **Never fork a style, a token or a control theme into this project.**
  `UI/ThemeTokenDictionaryTests` compares the two `App.axaml` files' include
  URIs as a SET and their `Application.Styles` children in ORDER, so the two
  hosts cannot drift.
- The gallery's markup obeys the same hard rules as `DotCharter.UI`:
  `DynamicResource` tokens only, no literal colours, no literal `FontSize`,
  `Padding*`/`Gap*` for thickness, `Space*` for spacing.

## Adding a page — the recipe

**A redesign view's FILE NAME must be unique across `Views/**`** (e.g.
`ResultsRedesignView`, not a second `ResultsView`): the UI
sweep pairs a view type with its `.axaml` by file name, so a duplicate binds the
SHIPPED view model to the redesign and every command binding dies. Linted by
`UI/ViewFileNamesAreUniqueTests`.

**A SCREEN is not added here at all.** Give it a contract, a fake with
`Variants` and one line in `DotCharter.UI/Views/Shell/ScreenCatalog.cs`
(`Screen<IResultsScreen, ResultsScreenView>(PlayMode, "Results")`); the page,
its smoke test and its goldens follow. Page and variant names come from the
catalog and the fake (`Default`, `Empty`, `LongText`, `NoMatch`, …).

For a page that is NOT a catalog screen (a token sheet, a control study):

1. Write a `Control` under `Pages/<Area>/`.
2. Add one `GalleryPage` to that area's list, `Pages/<Area>/<Area>Pages.cs`; a
   NEW area is one more `..<Area>Pages.All` spread in `GalleryCatalog.All`.
3. That is all. `Perf/GalleryPagesUiPerfSmokeTests` enumerates the catalogue, so
   the new page gets its construction + attach + layout smoke test and its
   capture PNG with no second registration anywhere.

**A variant is a FACTORY, never a cached instance** — the same control cannot
hang off two parents, and the capture run and the window each build their own.

## Capture

```powershell
# the standalone tool (its own process, real Skia)
dotnet build DotCharter.sln --nologo -v:m
.\DotCharter.UI.Gallery\bin\Debug\net10.0\DotCharter.Gallery.exe --capture $env:TEMP\gallery-capture

# the same pictures out of the headless suite
$env:DOTCHARTER_UI_SWEEP_DUMP = "1"
$env:DOTCHARTER_RIG_CAPTURE_DIR = "$env:TEMP\gallery-capture"
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless -NoBuild -FilterClass "*GalleryPagesUiPerfSmokeTests"
```

Both write `gallery-<section>-<page>-<variant>.png` at 1600×900, from the same
`GalleryCapture.FileName`, whose slugs are `ScreenCatalog.Slug` — the goldens'
`<area>/<screen>--<variant>.png` names the same screen with the same words
(`gallery-play-mode-song-select-no-match.png` ↔ `play-mode/song-select--no-match.png`).
The APPROVED pictures are the goldens: `Invoke-Gate.ps1 -Goldens`, see
`DotCharter.Tests/AGENTS.md`.

## Gotchas

- `--capture` work is POSTED onto the dispatcher. Calling `desktop.Shutdown()`
  straight from `OnFrameworkInitializationCompleted` throws
  `"Cannot perform requested operation because the Dispatcher shut down"` when
  the main loop then starts.
- A page is rendered inside a real `Window`, never detached: a detached subtree
  has no style scope and every `DynamicResource` resolves against nothing.
- `DotCharter.UI` grants this assembly `InternalsVisibleTo` for
  `Platform.TextFallbackFonts` only. Do not reach further into the app — a
  gallery that needs a service is a gallery that has stopped being fake.
- `Infrastructure/DocReferenceLintTests` fails the default gate on a dead file
  path or a dead `*Tests` name in this file.
