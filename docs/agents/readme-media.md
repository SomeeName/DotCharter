# README pictures

How the pictures in `README.md` (`docs/media/`) are made, so they can be redone after a UI change.
Everything is generated: no real song, title, cover, lyric or recording is involved.

1. **Demo content** - `DotCharter.Tests/Tools/ReadmeMedia/`: `ReadmeDemoSong` builds the chart
   project "DotCharter Demo" (a 6-string lead with slides, bends, hammer-ons, pull-offs, vibrato and
   fingered chords; a 7-string arrangement; sections, phrases, tones, lyrics), `ReadmeDemoAudio`
   renders its audio with a small additive synth, `ReadmeDemoContent` draws the cover and writes the
   project, a saved AI chat and five `.dcsong` packages (neutral invented titles) for the library.
   `ReadmeDemoContentTests` always generates into a temp folder and asserts the result is a chart worth
   photographing; with `DOTCHARTER_README_DEMO_DIR` set it writes there instead.
2. **Raw shots** - `tools/readme-media/Capture-ReadmeMedia.ps1` generates the content, launches the
   PUBLIC build (`Invoke-Gate.ps1 -Public -Build` first) against a fresh `DOTCHARTER_DATA_ROOT` with the
   perf autopilot in HOLD mode, mutes that process (`AppMute.ps1`) and drives it through
   `AppDriver.psm1` (UI Automation names and posted mouse messages, `PrintWindow` read-back). The window
   is placed on the non-primary monitor without activation; only the PID it started is ended. The click
   coordinates are measured on a 1920x1080 window: a moved button means re-measuring them.
3. **Editing** - `python tools/readme-media/Edit-ReadmeMedia.py <raw-dir> docs/media` crops, scales,
   rounds and shadows each shot into a WebP (needs Pillow). The Play Mode picture is one frame of a
   burst; pick one with 0 misses by editing the frame number in the script.

Check every image by eye before committing (no private path, no clipped or unstyled view) and keep
`docs/media/` small (under about 1 MB). The README text stays neutral: no game, official-song or
package claims (`PublicTreeHasNoPackageReaderTests` scans docs).
