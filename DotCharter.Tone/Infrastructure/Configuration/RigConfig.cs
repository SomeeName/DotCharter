using DotCharter.Tone.Ai;

namespace DotCharter.Tone.Infrastructure.Configuration;

[Flags]
public enum AppLogCategory
{
    None = 0,
    Application = 1 << 0,
    Project = 1 << 1,
    Import = 1 << 2,
    Editing = 1 << 3,
    Input = 1 << 4,
    Events = 1 << 5,
    History = 1 << 6,
    Settings = 1 << 7,
    Audio = 1 << 8,
    Errors = 1 << 9,
    External = 1 << 10,
    Performance = 1 << 11,
    Rendering = 1 << 12,

    All = Application | Project | Import | Editing | Input | Events | History | Settings | Audio | Errors | External
          | Performance | Rendering
}

/// <summary>
///     Log severity, ordered so a minimum level is a simple comparison.
///     <para>
///         <b>This exists because a category flag alone could not express "keep
///         the clicks, drop the scrubbing".</b> A category bit alone is not enough, because the
///         noise sits at the SAME category as the signal:
///         turning <see cref="AppLogCategory.Input" /> off to silence ~470
///         minimap-seek lines in one 32-minute session also silences every click,
///         selection change and marquee — the most useful record of how charting
///         actually goes. Severity and category are independent questions and
///         have independent answers.
///     </para>
/// </summary>
public enum AppLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}

/// <summary>
///     The settings the tone rig and the audio host read: the audio device and
///     its input channels, logging, the tone library, the TONE3000 client id.
///     <para>
///         DotCharter's <c>AppConfig</c> (DotCharter.Services) DERIVES from this,
///         so <c>appconfig.json</c> is unchanged: System.Text.Json writes the base
///         class's properties alongside the derived ones under the same names. The
///         standalone rig (DotCharter.RigLab) serializes a <see cref="RigConfig" />
///         of its own and never sees the charting settings.
///     </para>
///     <para>
///         <b>Add a property here only if code in DotCharter.Tone reads it.</b>
///         Everything else belongs on <c>AppConfig</c>.
///     </para>
/// </summary>
public class RigConfig
{
    public string AudioDriver { get; set; } = "WASAPI"; // "WASAPI" or "ASIO"
    // 0-based output channels of the master pair, for ASIO and WASAPI alike
    // (the names are historical): OutputChannelPairs turns them into the engine's mask.
    public int AsioOutputChannelLeft { get; set; }
    public int AsioOutputChannelRight { get; set; } = 1;
    // Stable device identifiers. Indices above are kept for back-compat only;
    // when a name is present it takes priority. This survives device list
    // re-ordering (USB plug/unplug, driver updates) which would leave the
    // dropdowns blank even though the engine was happily streaming to whatever
    // index 0 resolved to that boot.
    public string WasapiOutputDeviceName { get; set; } = string.Empty;
    public string AsioOutputDeviceName { get; set; } = string.Empty;
    public bool EnableInput { get; set; }
    /// <summary>
    ///     Drop-tune quality/latency: "tight" (10 ms splice-lag ceiling), "balanced"
    ///     (20 ms, default) or "smooth" (35 ms — fewest splices, best on sustained
    ///     chords and the octave, at the cost of more delay during sustain).
    ///     Attack latency (~3-4 ms) is the same in all three — the transient anchor
    ///     sets that, not this. The old "rubberband"/"timedomain" engine choice is
    ///     gone: there is one engine now (CSVD).
    /// </summary>
    public string DropTunerEngine { get; set; } = "balanced";
    /// <summary>
    ///     The guitar's input jacks (0-based) and whether one or two are open. The
    ///     engine masks them on ASIO and WASAPI alike; the names keep the saved
    ///     JSON keys.
    /// </summary>
    public int AsioInputChannelLeft { get; set; }
    public int AsioInputChannelRight { get; set; } = 1;
    public bool AsioInputMono { get; set; } = true;
    /// <summary>
    ///     The two open input channels carry TWO GUITARS, one each — not one
    ///     stereo instrument. Only meaningful while
    ///     <see cref="AsioInputMono" /> is false (that is what opens the second
    ///     channel), and it applies to WASAPI as well as ASIO: the channel COUNT
    ///     is driver-independent, only the channel INDEXES are ASIO-only.
    ///     <para>
    ///         <b>Explicit rather than inferred, on purpose.</b> Unticking Mono
    ///         has always meant "give the chain a real L and R", which is the case
    ///         the engine's single-seat stereo fill was written for; deducing "two
    ///         players" from it would silently collapse a genuine stereo pickup to
    ///         a centred mono. Nothing in the signal distinguishes them, so the
    ///         user says which they have — and this is what
    ///         <c>LiveInputSeatLayout.FromInputConfig</c> reads to raise the
    ///         engine's live-seat count outside a co-op run.
    ///     </para>
    /// </summary>
    public bool InputChannelsAreSeparateGuitars { get; set; }
    public string WasapiInputDeviceName { get; set; } = string.Empty;
    public string AsioInputDeviceName { get; set; } = string.Empty;
    // ------ Streaming mirror -------------------------------------------------
    // When enabled, the master mix (song + live input + VST chain) is also
    // pushed to a second WASAPI device in parallel. Streamers route this to
    // a virtual cable (VB-CABLE, VoiceMeeter) so OBS/Discord/Twitch pick up
    // both the guitar and the application audio even while DotCharter runs
    // on ASIO for low-latency monitoring.
    public bool EnableStreamingMirror { get; set; }
    public string StreamingMirrorDeviceName { get; set; } = string.Empty;
    // ------ Master-bus limiter (JuceSharp ABI v34; native default-OFF since v35)
    /// <summary>Keep the look-ahead soft limiter over the final device buffer in
    /// the path. <b>On by default</b>, and the app has to opt in for it to run at
    /// all: the engine ships the stage OFF (it sits on the live monitor path, so
    /// default-ON cost every player ~1 ms of latency on their own playing), while
    /// nothing else bounds the sum of the song bus, every seat's monitor mix, the
    /// click bus and both instrument legs — and the streaming mirror carries
    /// FLOAT, so samples past ±1.0 survive into OBS / Discord and wrap there.
    /// Config-file-only for now (no Settings UI yet), like the take recorder
    /// above. Switched off, the output is bit-for-bit the pre-v34 engine.</summary>
    public bool EnableMasterLimiter { get; set; } = true;
    /// <summary>Ceiling in dBFS, clamped to [−24, 0] by
    /// <c>Audio/Engine/MasterLimiterSetting</c> before it reaches the
    /// engine.</summary>
    public float MasterLimiterCeilingDb { get; set; } = -0.3f;
    public int AudioSampleRate { get; set; } = 44100;
    public int AudioBufferFrames { get; set; } = 256;
    public bool EnableHighProcessPriority { get; set; } = true;
    public double AudioDelayMs { get; set; }
    public double MidiDelayMs { get; set; }
    public int MidiOutputDevice { get; set; } = -1; // -1 = system MIDI mapper (GS Wavetable on Windows)

    /// <summary>
    /// LEGACY: the built-in chart synth's app-wide GUITAR — a
    /// <c>ChartSynthModel</c> NAME, a <c>ChartSynthPickup</c> NAME and the coil
    /// split. The instrument now belongs to the PLAYER PROFILE
    /// (<c>profiles/&lt;id&gt;/chart-synth-instrument.json</c>, DotCharter.Services
    /// <c>ChartSynthInstrumentCoordinator</c>), which MOVES these fields into the
    /// active profile once and then empties them. Empty = nothing to move; nothing
    /// else reads them.
    /// </summary>
    public string ChartSynthGuitarModel { get; set; } = "";
    /// <summary>See <see cref="ChartSynthGuitarModel" />.</summary>
    public string ChartSynthGuitarPickup { get; set; } = "";
    /// <summary>See <see cref="ChartSynthGuitarModel" />.</summary>
    public bool ChartSynthGuitarCoilSplit { get; set; }
    /// <summary>LEGACY: the app-wide BASS, as <see cref="ChartSynthGuitarModel" />.</summary>
    public string ChartSynthBassModel { get; set; } = "";
    /// <summary>See <see cref="ChartSynthBassModel" />.</summary>
    public string ChartSynthBassPickup { get; set; } = "";
    /// <summary>See <see cref="ChartSynthBassModel" />.</summary>
    public bool ChartSynthBassCoilSplit { get; set; }

    /// <summary>
    /// 0.12.0: host VST3 plugins in a sandboxed child process
    /// (juce_vst_host.exe) with audio bridged over shared memory, so a
    /// crashing/misbehaving plugin cannot take down or corrupt the app. When
    /// active, app shutdown also takes the clean close path instead of the
    /// TerminateNow fast-exit. Default ON (stress-validated: BIAS FX 2 +
    /// AGPF load/process/kill-restart in the sandbox; 20/20 clean app
    /// open/close cycles with zero shutdown faults). The Settings → Audio
    /// toggle switches back to legacy in-proc hosting if a plugin
    /// misbehaves across the process boundary.
    /// </summary>
    public bool OutOfProcessVst { get; set; } = true;
    public bool EnableConsoleLogging { get; set; } = true;
    public bool EnableConsoleColors { get; set; } = true;
    public bool EnableFileLogging { get; set; } = true;
    public bool CaptureExternalConsoleLogs { get; set; } = true;
    public bool CoalesceRepeatedLogs { get; set; } = true;
    public int LogRepeatCoalesceWindowMs { get; set; } = 2000;
    public int LogRepeatSummaryIntervalMs { get; set; } = 1000;
    public bool RotateLogFiles { get; set; } = true;
    public int LogFileMaxBytes { get; set; } = 10 * 1024 * 1024;
    public int LogFileMaxRollovers { get; set; } = 5;
    public string LogFilePath { get; set; } = "logs/dotcharter.log";
    public AppLogCategory EnabledLogCategories { get; set; } = AppLogCategory.All;
    /// <summary>
    ///     Nothing below this severity is written. <b>Default Info</b>, which is a
    ///     deliberate change from "everything".
    ///     <para>
    ///         Measured on one real 32-minute charting session: roughly 60% of the
    ///         log was noise that no question is ever asked of — ~470 minimap-seek
    ///         lines from three drags, 66 identical audio heartbeats, and a
    ///         "spacing enforcement completed" trace after every single edit.
    ///         All of it sits below Info; every click, selection, edit and history
    ///         action sits at or above it. Raising the floor keeps the record of
    ///         what the charter DID and drops the record of the pointer moving.
    ///     </para>
    ///     <para>
    ///         Drop it to <see cref="AppLogLevel.Trace" /> when chasing a specific
    ///         bug — or better, use <see cref="LogLevelOverrides" /> to do that for
    ///         one area without re-flooding the rest.
    ///     </para>
    /// </summary>
    public AppLogLevel MinimumLogLevel { get; set; } = AppLogLevel.Info;
    /// <summary>
    ///     Per-category floors, keyed by <see cref="AppLogCategory" /> NAME, that
    ///     win over <see cref="MinimumLogLevel" /> in BOTH directions.
    ///     <para>
    ///         Keyed by name rather than by the enum so the JSON stays readable and
    ///         survives the flags enum gaining members:
    ///         <c>{ "Audio": "Trace" }</c> turns one subsystem's firehose back on
    ///         without touching anything else, and <c>{ "Input": "Warning" }</c>
    ///         silences an area that is currently in the way. Empty by default.
    ///     </para>
    ///     <para>
    ///         A category naming several flags is not a valid key — the lookup is
    ///         per single flag, because that is the granularity a caller logs at.
    ///     </para>
    /// </summary>
    public Dictionary<string, AppLogLevel> LogLevelOverrides { get; set; } = [];
    // Player-saved DSP tone-rig profiles (name + category + ordered module list).
    // Managed by Audio.Dsp.Presets.UserTonePresetStore / DspTonePresetService;
    // surfaced as Save / Save-As in the pedalboard tone picker. Survives restart.
    public List<Audio.Dsp.Presets.UserTonePresetDto> UserTonePresets { get; set; } = [];
    // ------ The live tone rig, across restarts -------------------------
    // The pedalboard does not start EMPTY on every launch (a fresh install with no
    // amp at all, a rig the player spent ten minutes building gone at
    // exit unless they remembered to Save-As). Both are handled by
    // Audio.Dsp.ToneSessionService.
    //
    // MIGRATION-ONLY. The live rig is PER PLAYER PROFILE now — it
    // lives in tone-rig.json inside the active profile's data directory
    // (Audio.Dsp.ProfileToneRigStore), because a rig is a player's own settings
    // and two people sharing a machine were overwriting each other's board. The
    // two fields below are read exactly once, by ProfileToneRigStore's adoption
    // of the pre-0.45 global rig into whichever profile is active at that
    // moment, and are CLEARED as soon as that write succeeds. Nothing writes
    // them any more; do not add a reader.
    //
    // ActiveTonePresetId is the preset the rig came from, kept only so the
    // browser can show it highlighted; a hand-edited rig clears it. It has had
    // no reader or writer since it was added.
    public Audio.Dsp.Presets.UserTonePresetDto? ActiveToneRig { get; set; }
    /// <summary>
    ///     The pre-0.45 last live chain of co-op seats 2..N, keyed by zero-based
    ///     player index ("1", "2", …); seat 0 was <see cref="ActiveToneRig" />.
    ///     <b>Migration input only</b> — see the block comment above. The live
    ///     per-seat rigs are the active profile's <c>tone-rig.json</c>, and the
    ///     "a seat with no entry restores nothing rather than inheriting player
    ///     1's board" rule moved there with them.
    /// </summary>
    public Dictionary<string, Audio.Dsp.Presets.UserTonePresetDto> ActiveToneRigsByPlayer { get; set; } = [];
    /// <summary>
    ///     The factory preset a first run (or a reset) starts on. A
    ///     capture-backed twin on purpose: it is what makes a fresh install
    ///     sound like an amp rather than a DI. Blank disables the first-run
    ///     default entirely (the chain then starts empty, the pre-0.37
    ///     behaviour).
    ///     <para>
    ///         Plexi Crunch specifically because it is a SINGLE-capture rig. The
    ///         boosted presets put a pedal capture in front of the amp capture,
    ///         which doubles both the startup model load and the steady-state
    ///         cost — measured 13.4% of one core against 6.7% for a lone amp, and
    ///         that cost would then be paid by every user who never opens the
    ///         pedalboard. A mid-gain British crunch also cleans up with the
    ///         guitar's volume, so it is usable across far more of the library
    ///         than either a clean or a high-gain default.
    ///     </para>
    /// </summary>
    public string DefaultTonePresetId { get; set; } = "nam-crunch-plexi";
    /// <summary>Restore <see cref="ActiveToneRig" /> when the audio engine comes
    /// up (and after any engine rebuild, which wipes the native chain). Off
    /// means every session starts from the default preset instead.</summary>
    public bool RestoreToneOnStartup { get; set; } = true;
    // ------ TONE3000 NAM-capture browser (0.16) -------------------------------
    // OAuth client id for the TONE3000 (tone3000.com) capture browser sign-in.
    // Empty by default (config-file-only, no Settings UI): the owner mints a
    // developer/publishable key at tone3000.com/settings (registering the
    // loopback redirect http://localhost:47821/tone3000/callback/) and pastes
    // it here. When empty, capture SEARCH still works (public API) but
    // sign-in — and therefore downloads — shows a "not configured" hint.
    // The OAuth tokens are NOT stored here: they live DPAPI-encrypted in
    // tone3000-token.dat (on ConfigBackupRules' credential list).
    public string Tone3000ClientId { get; set; } = string.Empty;

    // Tone-rig pedalboard rack zoom (Play Mode Pedalboard overlay). Scales the
    // pedal chain via a LayoutTransform; clamped to 0.5..1.5 on apply. 1.0 = 100%.
    public double PedalboardZoom { get; set; } = 1.0;
    /// <summary>Optional MIDI control input, restored by device name after engine rebuilds.</summary>
    public string? RigMidiInputDevice { get; set; }

    /// <summary>The MIDI channel the rig's program changes and scene CC listen on:
    /// 0 = every channel (omni), 1..16 = that channel only. Knob assignments carry
    /// their own channel and are not filtered by this.</summary>
    public int RigMidiChannel { get; set; }

    /// <summary>Whether a PROGRAM CHANGE switches tones: program n is entry n of
    /// the active setlist (0 = the first), or with no setlist active the n-th of
    /// the player's own tones.</summary>
    public bool RigMidiProgramChange { get; set; } = true;

    /// <summary>The CC that selects a scene (value 0..7 = A..H); -1 = off. 69 is
    /// the Line 6 Helix snapshot CC, which is what most pedal boards already send.</summary>
    public int RigMidiSceneCc { get; set; } = 69;

    /// <summary>The player's setlists (named, ordered lists of tones for a gig).
    /// Per INSTALL, like the user tones they point at.</summary>
    public List<Audio.Dsp.Presets.RigSetlistDto> RigSetlists { get; set; } = [];

    /// <summary>The setlist ▲▼, PageUp/PageDown and MIDI program changes step
    /// through, by id; null = none (program changes then pick the player's own
    /// tones).</summary>
    public string? RigActiveSetlistId { get; set; }
    // Amp & FX rig: the height in pixels of the WORKSPACE below the routing
    // canvas — the module editor and the tone-library drawer share one host
    // (RigWorkspace) and one dragged divider, so this is one number for both.
    // The canvas row above it is `*` and absorbs the rest, which is what keeps
    // the canvas the same height whether the editor is open or not. Null =
    // never dragged; the workspace opens at
    // PedalboardViewModel.DefaultWorkspaceHeight. Clamped to
    // Min/MaxWorkspaceHeight on load AND on apply — a config edited by hand
    // must not be able to squeeze the canvas to nothing.
    public double? RigWorkspaceHeight { get; set; }
    // LEGACY: the tone drawer alone had a height and the
    // module editor had its own (RigDockHeight, now gone). Read once, as the
    // seed for RigWorkspaceHeight, so an existing install keeps the split it
    // had; never written. See PedalboardViewModel.LoadPersistedWorkspaceHeight.
    public double? RigToneBrowserHeight { get; set; }
    /// <summary>
    ///     The gear the player starred in the rig's gear library, as catalogue
    ///     module type ids (<c>"overdrive"</c>, <c>"namamp"</c>, …). Drives the
    ///     library's Favourites tab (<c>GearRailViewModel.ShowFavoritesOnly</c>).
    ///     <para>
    ///         Per INSTALL, deliberately not per profile: it is a shortcut to the
    ///         units this player reaches for, the same class of thing as the rack
    ///         zoom, and a second player's roster arriving must not re-order the
    ///         first one's list. An unknown id is inert — the rail matches against
    ///         the live catalogue, so a module that leaves it simply stops
    ///         appearing.
    ///     </para>
    /// </summary>
    public List<string> RigFavoriteGear { get; set; } = [];
    /// <summary>
    ///     SPILLOVER (ABI v37): a delay or reverb that a tone switch REMOVES keeps
    ///     ringing at the rig output instead of being cut. On by default — the
    ///     native engine starts with it off, so this is pushed to every seat's
    ///     chain at startup (<c>ToneSessionSet.Create</c>) and on every toggle,
    ///     and the chain service replays it onto every rebuilt engine.
    /// </summary>
    public bool RigSpillover { get; set; } = true;
    /// <summary>
    ///     Concert-pitch reference for the rig tuner, in Hz (A4). 440 is standard;
    ///     the range 415..466 covers historical and ensemble pitches
    ///     (<c>RigTunerMath.MinReferenceHz</c>/<c>MaxReferenceHz</c>) and is
    ///     clamped on load AND on apply, because a hand-edited config must not be
    ///     able to point the needle at a pitch no instrument tunes to.
    /// </summary>
    public double TunerReferenceHz { get; set; } = 440.0;

    /// <summary>
    ///     The tuning the rig tuner tunes TO (<c>TunerTunings</c> id, e.g.
    ///     "guitar:-2,0,0,0,0,0" for Drop D), or "chromatic" for none. An id this
    ///     build does not know reads as chromatic.
    /// </summary>
    public string TunerTuning { get; set; } = "chromatic";

    /// <summary>
    ///     Digital drop-tune pedal: semitone shift applied to the live input in
    ///     front of detection, the FX chain and monitoring (negative = drop),
    ///     −12…+12. Seat 0's; the rig footer and DotCharter's transport both edit
    ///     it. Moved here from <c>AppConfig</c> (same JSON name) when RigLab got
    ///     the control.
    /// </summary>
    public int InputPitchShiftSemis { get; set; }

    // ------ AI tone design (0.55) --------------------------------------------
    /// <summary>
    /// Which model backend answers "design me a tone", plus its model id and (for
    /// a local OpenAI-compatible server) its base URL. Defaults to
    /// <see cref="AiProviderSettings.Disabled"/> — the feature is off
    /// and the panel hidden until the user picks a provider under Settings → AI.
    /// <para>The API key is NOT here and never will be: it lives DPAPI-encrypted
    /// in <c>ai-secrets.bin</c> (<see cref="DpapiAiSecretStore"/>),
    /// which is on <c>ConfigBackupRules</c>' credential list, so a
    /// settings backup or an update payload can never carry it.</para>
    /// </summary>
    public AiProviderSettings Ai { get; set; } = AiProviderSettings.Disabled;
}
