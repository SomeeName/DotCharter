# DotCharter.RigLab Agent Guide

**DotCharter RigLab**: the rig on its own — the Amp & FX rig, the audio device,
NAM captures (bundled + TONE3000) and a VST3 chain on the guitar input. For a
player who wants the rig and none of the charting app. Assembly
`DotCharter.RigLab`, WinExe.

## The one rule

**References `DotCharter.Core`, `.Localization`, `.Tone`, `.Tone.UI` and
JuceSharp — NEVER `DotCharter.UI` or `DotCharter.Services`.** Either one drags
in stem separation (ONNX Runtime), note detection, psarc, export, the AI SDKs.
`Tests/RigLab/RigLabDependencyTests` walks the whole transitive assembly closure
and pins it, plus the csproj. If RigLab needs something that lives in Services,
move it to `DotCharter.Tone` (see its guide) — never add the reference.

## Layout

| File | What |
|---|---|
| `Program.cs` | `WritablePaths.UseAppFolderName("DotCharter RigLab")` FIRST, then the Avalonia builder (same font fallback as the app) |
| `App.axaml(.cs)` | theme = `ColorTokens` + Fluent + `Tone.UI/Styles/RigTheme.axaml`; `ViewLocator`; builds the host and the window |
| `RigLabHost` | the composition root, by hand: engine, DSP chains, presets, live-rig store (fixed directory, no profiles), TONE3000 + NAM library, the rig view models. `StartAsync` = open device → install bundled captures → restore rig → restore VST chain (that order — see its comment) → start a configured MIDI controller (program changes and the scene CC must work from the first song, not from the first time a unit's details are opened). `Dispose` order matters |
| `RigLabConfig`, `RigLabConfigService` | `riglab.json` (a `RigConfig` + VST chain, language, window). Absent/corrupt/unreadable handled like every JSON store here; writes coalesced |
| `RigLabShellViewModel`, `RigLabWindow` | four tabs: Rig (the shared `AmpFxRigView`), Plugins, Audio, AI. The window maps `ToneDesignCoreViewModel` to the core panel (on the WINDOW, so the headless test runs the product's mapping) |
| `AiSettingsViewModel`/`View` | the AI tab: provider (None / Claude API / OpenAI-compatible with LM Studio, Ollama, OpenAI presets / Claude Code CLI / Codex CLI), model (Haiku 5.5 / Opus 5.5 / Sonnet 5.5 presets for the Claude kinds, GPT-6 Sol / Luna for Codex; a picked kind seeds `AiDefaultModels`), Reasoning (`AiEffortRow`, #644: DotCharter's Settings ▸ AI row — same levels, resx keys and support rule through `DotCharter.Tone/Ai/AiReasoningText`; hidden with None, disabled with its reason for a Claude model older than the effort parameter; saved as `RigConfig.Ai.Effort`, which the tone models send), address or CLI path, API key (DPAPI `ai-secrets.bin`, never riglab.json), Test; and TONE3000 (client id, sign in/out) so a design can download its amp capture. Every change applies at once |
| `AudioSettingsViewModel`/`View` | driver, devices, jack, live input, buffer, rate, ASIO panel, language — every change saved and re-applied at once, off the UI thread |
| `VstRackViewModel`, `PluginsView` | the guitar VST3 chain: rescan, add, reorder, bypass, GUI, remove — pushes the WHOLE chain on every change, like DotCharter's transport. The view model implements `IVstRack`; `PluginsView` is the tab's heading + rescan around the SHARED rack `Views/PlayMode/VstRackView` (in `DotCharter.Tone.UI`), the same view DotCharter's Tools popover and Amp/FX panel host. Reorder is the rack's drag (`MoveSlot`); the ▲▼ buttons went 2026-09-27 |
| `InputPeakMeter` | the rig header's input meter from the capture window (no live-play session here) |

## Differences from the rig in DotCharter

- The AI tone designer is the shared core only: no "Match from audio" /
  "Match by section" (ONNX) and no library anchors (psarc). `RigLabHost` builds
  the `ToneDesignService` with a `Tone3000CaptureSource`, so while TONE3000 is
  signed in a design can fetch the amp capture it names. The "ChatGPT (copy &
  paste)" route works here with NO provider set up: `RigLabHost.Clipboard` (the
  shared `AvaloniaSystemClipboard`) is handed to the designer for "Copy prompt".
- No player profiles, no song context. Live input is ON by default.
- `Tone3000ClientId` defaults to the value baked in at publish
  (`Publish-RigLab.ps1 -Tone3000ClientId`, a PUBLISHABLE key — never the secret
  one); empty in a plain build, set on the AI tab.
- Its own settings/log folder (`%APPDATA%\DotCharter RigLab`); the NAM capture
  library is the shared `%APPDATA%\DotCharter\models\nam`.
- Bundled captures: linked from `DotCharter.UI/NamCaptures` (one copy in the repo).

## Tests

Default gate: `Tests/RigLab/*` (dependency closure, config store, the two tabs'
view models, the theme order). Headless: `Perf/RigLabConstructionUiPerfSmokeTests`
(window + every tab measured non-zero, the rig theme loads) and
`Perf/AmpFxRackUiPerfSmokeTests` (the Plugins tab over a full two-seat chain). Publish:
`tools/release/Publish-RigLab.ps1` builds a self-contained win-x64 folder and
fails if a charting-stack binary is in it.
