# DotCharter usage guide

This guide shows how to chart a song and how to play one, and it explains every screen.

1. [Getting started](getting-started.md) covers the start screen, the Library and the app's layout.
2. [Charting a song](charting.md) covers the editor, every menu, the toolbar, the Inspector and the 3D
   preview. It also has a walkthrough from audio to an exported chart.
3. [Playing a song](playing.md) covers Song Select, Get ready, the highway, the pause menu and results.
4. [Tone rig](tone-rig.md) covers amps, cabinets and pedals for your guitar, and RigLab, the rig as an
   app of its own.
5. [Settings reference](settings.md) says what each settings page holds.

## How the pictures are made

The screenshots are generated. Nobody takes them by hand. Each page has a manifest in
[`manifests/`](manifests/) that lists its screenshots. For every numbered marker, the manifest names a
control by its `x:Name` in the app's XAML. For a menu row, it uses the localization key of the row's
header. The manifest also holds the sentence that explains the marker. A capture run renders the real
views headlessly over a sample song, finds each control, draws the numbered markers and writes the PNGs
to [`images/`](images/). It then rewrites the numbered list under each picture from the same manifest,
so the picture and the text always agree.

A test in the default gate (`DotCharter.Tests/Guide/GuideManifestLintTests`) fails when a control a
manifest names no longer exists in the XAML. It also fails when a page's list differs from its
manifest, or when an image is missing or too large. If you rename a control, the build fails. The
arrow doesn't just disappear without notice.

## Regenerating the images

Run this after a UI change and before each release. It is opt-in and is never part of the gate.

```powershell
dotnet build DotCharter.sln --nologo -v:m
$env:DOTCHARTER_UI_SWEEP_DUMP = "1"       # real Skia drawing in the headless session
$env:DOTCHARTER_GUIDE_CAPTURE = "1"       # turns the capture on
$env:DOTCHARTER_HEADLESS_CULTURE = "en-US"
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -NoBuild -FilterClass "*GuideCaptureUiPerfSmokeTests"
```

- `DOTCHARTER_GUIDE_ONLY=<prefix>` renders only the shots whose id starts with the prefix, for
  example `menu-` or `play-`.
- `DOTCHARTER_GUIDE_OUT=<folder>` writes the pages and images to another folder instead of `docs/guide`.
- The editor's timeline and 3D preview are drawn offscreen with Vulkan. Without a Vulkan device
  those two panes stay empty. Everything else still renders.
- Beside the test binaries, `TestResults/guide/` gets `<id>.names.txt` and `<id>.raw.png`. The first
  lists every visible named control and its box. The second is the frame without markers. Use them to
  pick the control for a new callout.

To add a callout, add `{ "name": "<x:Name>", "title": "...", "text": "..." }` to the shot in the
manifest, put an empty `<!-- guide:callouts <id> -->` … `<!-- /guide:callouts -->` block under a new
shot's picture, and run the capture. Commit the manifest, the page and the images together.
