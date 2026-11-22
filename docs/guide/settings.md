# Settings reference

Open Settings from the **Settings** row of the navigation rail or from **File ▸ Settings**. Pages are
grouped into Workspace, System and Configuration. Changes take effect when you press **Save
changes**. **Reset section** puts the current page back to its defaults. The search box finds any
setting on any page.

Play Mode has a smaller settings panel of its own. It applies changes live while you play. See
[Playing a song](playing.md#settings-while-playing).

## General

![General](images/settings-general.png)

<!-- guide:callouts settings-general -->
1. **Sections.** The settings pages, in three groups.
2. **Search.** Finds any setting on any page (Ctrl+F).
3. **Language & region.** Display language and the screen DotCharter opens at.
4. **Diagnostics.** Shows local crash reports. Nothing leaves your computer.
5. **Advanced.** Logging options.
<!-- /guide:callouts -->

## Editor

This page controls how the chart editor behaves while you edit and how the 2D timeline looks, including
string order, lane tint and polish. It also sets the defaults of a new project.

![Editor](images/settings-editor.png)

<!-- guide:callouts settings-editor -->
1. **Chart editing.** Keyboard note entry, how hand shapes and positions follow edits, and chord naming.
<!-- /guide:callouts -->

Further down are **Copy & paste**, **New project** and **Guitar Pro import**. **Copy & paste** sets what
a normal paste brings along. **New project** sets the default charter name and silence padding.

## Play

![Play](images/settings-play.png)

<!-- guide:callouts settings-play -->
1. **Gameplay.** Count-in, metronome, note claps and the rock meter in Play Mode.
<!-- /guide:callouts -->

## Appearance

![Appearance](images/settings-appearance.png)

<!-- guide:callouts settings-appearance -->
1. **Interface.** String colors and the size of the whole interface.
2. **String colors.** Color-blind-safe palettes for the strings, in the timeline and on the highway.
3. **Customize colors.** Change any color of the theme.
<!-- /guide:callouts -->

## Graphics

The 3D highway and the editor's timeline.

![Graphics](images/settings-graphics.png)

<!-- guide:callouts settings-graphics -->
1. **Display.** Render scale, frame rate, VSync and anti-aliasing.
2. **Hand-position preview.** Shows the next hand position before you need it.
3. **Highway view.** Scroll speed, draw distance and camera.
<!-- /guide:callouts -->

Further down are **Timing**, **Highway** effects, **Showlights** and the **Stage background**. **Timing**
sets the visual lead, which lines the picture up with the sound.

## Audio

Where the sound goes and where your instrument comes in. Set this up before you play.

![Audio](images/settings-audio.png)

<!-- guide:callouts settings-audio -->
1. **Device summary.** The output device, sample rate, buffer and latency in use.
2. **Output routing.** The driver and device the song plays through.
3. **Instrument input.** Turn on live input, and choose the device and channels your guitar is on.
<!-- /guide:callouts -->

Further down are **Calibration & sync**, a **Streaming mirror** and **MIDI output**. **Calibration & sync**
holds the sample rate, buffer size, audio delay and the latency calibration. The **Streaming mirror**
copies the mix to a second device.

ASIO gives the lowest latency when your audio interface has an ASIO driver. WASAPI works with every
device.

## Import & export

This page controls how DotCharter reads Guitar Pro files and song packages, what a save writes next to
the project, and backups.

![Import & export](images/settings-files.png)

<!-- guide:callouts settings-files -->
1. **Save companions.** Extra files that DotCharter writes next to the project on every save.
2. **Auto backups.** Timed ZIP backups of the open project.
<!-- /guide:callouts -->

## Keybinds

![Keybinds](images/settings-keybinds.png)

<!-- guide:callouts settings-keybinds -->
1. **Keyboard or MIDI.** Bind commands to keys, or to a MIDI controller.
2. **Bindings.** Every command and its key. Click a field and press the new key.
<!-- /guide:callouts -->

## AI & models: stem separation

Stem separation splits a song's audio into instruments, so Play Mode can mute the part you play.
Separated stems are kept, so a song is separated only once.

![Stem models](images/settings-stem-models.png)

<!-- guide:callouts settings-stem-models -->
1. **Models.** The stem-separation models. Pick the active one.
<!-- /guide:callouts -->

Further down are **GPU acceleration**, the **Stem cache** and a way to add a custom model. Stem
separation is a section of the **AI & models** page, under the provider and match-model cards.

Below it, **Lyric models** lists the models that transcribe and align lyrics. You can download or
repair each one. You can also see whether it last ran on the GPU or fell back to the CPU, and a button
tries the GPU again.

## Tone3000

![Tone3000](images/settings-tone3000.png)

<!-- guide:callouts settings-tone3000 -->
1. **Tone3000.** Sign in to browse and download community amp captures.
<!-- /guide:callouts -->

## AI & models

![AI & models](images/settings-ai.png)

<!-- guide:callouts settings-ai -->
1. **Provider.** Which AI answers tone, chart and chat requests, and which model it uses.
2. **Match model.** A local model that guesses a starting rig from a recording.
<!-- /guide:callouts -->

## Updates

![Updates](images/settings-updates.png)

<!-- guide:callouts settings-updates -->
1. **Application updates.** Your version, and the check for a newer one.
<!-- /guide:callouts -->
