# DotCharter architecture

Two pages for a human. If you can explain everything here without looking, you
know the system well enough to change it. If a section surprises you, that is the
part of the code to go and read.

Why the big choices were made is in [adr/](adr/README.md); the index of all
guides is [README.md](README.md). Agents: this is the overview. The symbol index
is [agents/MAP.md](agents/MAP.md) and the rules are in the `AGENTS.md` tree.

## What the app is

A desktop app for **charting** songs (an editor that writes Rocksmith 2014 charts)
and **playing** them (a Rocksmith-style game with real guitar input and a tone
rig). Avalonia 12 on .NET 10 for the UI, a native C++ JUCE engine for every
sample of audio, Vulkan for the note highway. Windows first.

## The layers

```
UI ──► Presentation ──► Services ──► Tone ──► JuceSharp ──► native C++ engine
│         └───────────────────────────► Tone
│                          └───────► Analysis
├──► Tone.UI ──► Tone
└──► Rendering

Every layer also depends on Core. Core and Localization depend on nothing.
```

| Layer | Owns | Never |
|---|---|---|
| **Core** | The chart: arrangements, notes, chords, beats, tempo, timeline math | References anything |
| **Analysis** | Audio → draft notes and beat maps | References Services |
| **Rendering** | The Vulkan highway and 2D timeline | References Services |
| **Tone** | Talking to the audio engine, the tone rig, VST/NAM/MIDI | References Services or UI |
| **Services** | Everything with no screen: loading, saving, editing history, import/export, gameplay rules, stems, updates | References UI |
| **Presentation** | Screen contracts and screen view models for the rewritten screens | References Avalonia |
| **UI** | Views, the remaining view models, custom-drawn controls, DI wiring | — |

**Why these boundaries exist.** Core has no dependencies so the domain rules can be
tested without an engine, a window or a file system. Tone is its own assembly so
the rig can ship as a standalone app (`DotCharter.RigLab`) without the editor.
Presentation has no Avalonia so its screens can be built and tested on fakes in
the gallery before they replace a legacy screen. **None of this is a convention:**
`AssemblyLayeringTests` fails the build when a reference points the wrong way.

## How the app starts

`Program.Main` builds the Avalonia app. `App` (the composition root, split by area
into `App.*Services.cs`) registers about 200 services in Microsoft DI. The main
window opens on Home or the editor depending on the onboarding choice, then
**prewarms** hidden screens in the background so the first click on any of them
lays out in under 150 ms.

## Flow 1: play a song

1. **Home → Song Select.** `PlayModeViewModel` (one class across the
   `PlayModeViewModel.*.cs` files) drives a screen state machine:
   `Start → SongSelect → TuningCheck → Countdown → Playing ⇄ Paused → Results`
   (`PlayModeScreen`). The song list mixes library songs (`.dcsong`) and your own
   `.rscp` projects.
2. **Get ready.** Pick an arrangement, path and speed; the tuning check listens to
   your guitar; the tone for the song is loaded into the rig.
3. **Audio starts.** `JuceAudioPlaybackService` (Tone) tells the native engine to
   play the song, or its separated stems, through the device. The engine runs the
   guitar input through the tone rig at the same time. **No audio sample ever
   passes through C#.** C# only sends commands and receives positions (up to
   250 times a second), which are coalesced onto the UI thread.
4. **Judging.** `LivePlayViewModel` runs a worker loop that reads captured input,
   feeds `GuitarInputAnalyzer` (Services), and maps each detection onto chart time
   to judge notes. Scores, the rock meter and streaks come from `Services/Gameplay`.
5. **Drawing.** The highway is drawn by `DotCharter.Rendering` with Vulkan from a
   frame snapshot of the chart and the playhead.
6. **Results.** A finished run writes the take, replay and profile statistics.

## Flow 2: edit and save a chart

1. **Open.** `ProjectSessionService` loads a `.rscp` (XML, `chartFormatVersion`
   3) through the serializer registered for that extension, into `ProjectState`.
2. **Edit.** Every change is an `IChartAction` pushed through `HistoryService`, so
   every edit can be undone. Editors never mutate the chart behind the history's
   back.
3. **Save** (`Ctrl+S`, menu or window close all reach `ProjectSaver.SaveProjectCoreAsync`):
   deep-copy the project on the UI thread, then on a worker apply the spacing
   rules, write to `<file>.saving`, and atomically replace the real file. A crash
   mid-save can never leave a half-written project.
4. **Companions.** The same save writes derived files beside the project: the DLC
   Builder XML bundle, an Editor on Fire `.eof`, a `.rs2dlc`, and (after the save,
   in the background) a playable `.psarc`. A failing companion never fails the save.

## Where state lives

| State | Owner |
|---|---|
| The open chart | `ProjectState` + `HistoryService` (Services/State) |
| Unsaved changes | `ProjectDirtyTracker` |
| User settings | `AppConfig`, persisted as JSON by `JsonConfigurationService` |
| The live tone rig | the native engine; mirrored by `PedalboardViewModel` / `RigConfig` |
| Separated stems | one `.dcstems` archive per song, cached forever, never regenerated |

## Hard rules that hold the design together

- **Real-time audio stays native.** Nothing on the audio path in managed code.
- **Engine state is replayed on rebuild.** A device change rebuilds the engine;
  anything not replayed is silently lost.
- **Every edit goes through history.** Otherwise undo lies.
- **Saves are atomic.** Write aside, then replace.
- **A registered service must be used.** `ServiceRegistrationReachabilityTests`
  fails on dead registrations.
- **Files stay small.** `SourceFileSizeBudgetTests` fails when a file outgrows its
  budget; split into real types, not partial files.

## Test yourself

Answer without opening the code, then check:

1. What happens between clicking Play and hearing sound? Which assembly talks to
   the device?
2. Why can a crash during save not corrupt a project?
3. Why does Tone not reference Services?
4. Where would a new "export to format X" go, and what must it never do to a save?
5. What breaks if `MainWindowPrewarm` is deleted?
6. Which test fails if `DotCharter.Rendering` starts referencing `DotCharter.Services`?

If you got stuck on one, read that part of the code before your next feature —
that is the part that has become a black box.
