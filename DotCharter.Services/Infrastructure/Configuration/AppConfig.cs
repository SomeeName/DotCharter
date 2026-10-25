namespace DotCharter.Services.Infrastructure.Configuration;

public enum MinNoteDistanceMode
{
    Milliseconds,
    MeasureFraction,
    BeatFraction
}

public class AppConfig : RigConfig
{
    public string LastProjectPath { get; set; } = string.Empty;

    /// <summary>
    ///     "Open Recent" — most recent first, capped at
    ///     <see cref="RecentProjectsList.Cap" />. Written only by the project
    ///     open/save funnel (<c>ProjectSessionService</c>); entries whose files
    ///     do not exist are dropped on read. Paths are user data — never log
    ///     them at Info level.
    /// </summary>
    public List<RecentProjectEntry> RecentProjects { get; set; } = [];

    /// <summary>
    ///     What the shell boots into. <see cref="Configuration.StartupScreen.PlayMode"/>
    ///     (default): the game main menu is the front door, charting one click
    ///     away. <see cref="Configuration.StartupScreen.Editor"/>: classic
    ///     editor-first startup. Set by the onboarding wizard's role choice and
    ///     by Settings ▸ General; the shell reads it through
    ///     <c>StartupRouting</c>.
    /// </summary>
    public StartupScreen StartupScreen { get; set; } = StartupScreen.PlayMode;

    /// <summary>
    ///     Back-compat ALIAS over <see cref="StartupScreen"/> — not a second
    ///     field. It stays serialized so a config written before the enum
    ///     existed still boots, and the Play Mode menu's
    ///     "show this menu at startup" toggle keeps writing it. Both members
    ///     are emitted on save and agree by construction; on read the last one
    ///     in the file wins, which is the same answer either way.
    /// </summary>
    public bool StartInPlayMode
    {
        get => StartupScreen == StartupScreen.PlayMode;
        set => StartupScreen = value ? StartupScreen.PlayMode : StartupScreen.Editor;
    }

    /// <summary>
    ///     True once the first-run onboarding wizard has been finished OR
    ///     dismissed — either way it never opens on its own again. "Run setup
    ///     again" under Settings ▸ General reopens it on demand.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    ///     The app version that completed (or dismissed) onboarding, for support
    ///     and for a later "new since you set up" prompt. Empty until then.
    ///     <para>
    ///         <b>Deliberately has no PRODUCT reader, and that is not a missing
    ///         feature.</b> Two sites write it — <c>MarkCompleted</c> (Finish and
    ///         dismiss) and <c>OpenAtStartupIfNeeded</c>'s grandfathering branch —
    ///         and the "never nagged twice" gate is the BOOLEAN
    ///         <see cref="OnboardingCompleted" /> beside it, never this string. The
    ///         reader is a human: <c>appconfig.json</c> travels verbatim inside the
    ///         diagnostics bundle, so "which version set this install up" is
    ///         answerable from a support zip
    ///         (<c>OnboardingCompletedVersionTests</c> pins that path). The
    ///         "new since you set up" prompt is a FUTURE feature and adding one is
    ///         a product decision — do not invent a reader to make the field look
    ///         used, and do not delete it as dead.
    ///     </para>
    /// </summary>
    public string OnboardingCompletedVersion { get; set; } = string.Empty;

    /// <summary>
    ///     True once the user accepted the one-time rights notice in front of the
    /// YouTube audio import: "only download audio you have the rights to
    ///     use". Until then nothing is downloaded from YouTube. Never reset by the app.
    /// </summary>
    public bool YoutubeRightsAccepted { get; set; }

    // `MenuDesign` lived here through 0.46 while the front door shipped six
    // candidate layouts behind an in-app picker. Design 6 won and the other
    // five were deleted, so the setting is gone; a config file still carrying
    // it is simply ignored on read (System.Text.Json drops unknown members).

    /// <summary>Last tuning the player confirmed on the pre-play screen —
    /// same-tuning songs show a verified badge instead of asking again.</summary>
    public string LastVerifiedTuning { get; set; } = string.Empty;

    /// <summary>Opt-in: always treat the tuning as unverified on the pre-play
    /// screen, even when it matches <see cref="LastVerifiedTuning"/>.</summary>
    public bool AlwaysAskTuning { get; set; }

    public string PsarcLibraryPath { get; set; } = string.Empty;
    public string LanguageCulture { get; set; } = "system";

    public int WasapiOutputDevice { get; set; } = -1; // -1 is Default
    public int AsioOutputDevice { get; set; }

    public string InputDriver { get; set; } = "ASIO";
    public int WasapiInputDevice { get; set; } = -1;
    public int AsioInputDevice { get; set; }

    // ------ Live Play practice suite ------------------------------------------
    /// <summary>Hit-window tightness: "Lenient" | "Normal" | "Strict".</summary>
    public string LivePlayHitWindow { get; set; } = "Normal";

    /// <summary>Chord voicing strictness: "Lenient" | "Standard" | "Strict".</summary>
    public string LivePlayChordStrictness { get; set; } = "Standard";

    /// <summary>String-aware verification (drop-pedal-aware pitch map).</summary>
    public bool LivePlayStringAware { get; set; }

    /// <summary>Detection v8: judge live play with the CQT chart verifier
    /// (Binary Match Mask) instead of the classic pitch-candidate matcher.
    /// **On by default.** Measured against real DI with
    /// ground truth (Guitar-TECHS, 14,250 charted sounds, the whole Live Play pipeline
    /// offline): the classic matcher confirmed 59.5 % of perfectly played sounds (40 % of
    /// the chord sets, 45 % of the songs), the CQT verifier 92.4 %. The "idle-noise"
    /// shakedown this default waited for is the streaming idle floor
    /// (<c>LiveCqtConfig.IdleSteadySeconds</c>). Existing configs are moved over once by
    /// <see cref="LivePlayCqtDefaultMigrated" />.</summary>
    public bool LivePlayUseCqtVerifier { get; set; } = true;

    /// <summary>One-time 0.57.4 migration latch: configs written while the CQT verifier
    /// was opt-in carry <c>false</c> as a DEFAULT, not a choice — they are switched on
    /// once (JsonConfigurationService.NormalizeAppConfig). A user who turns it off
    /// afterwards keeps it off: this flag stays true.</summary>
    public bool LivePlayCqtDefaultMigrated { get; set; }

    /// <summary>
    /// Judge a charted BEND from the pitch contour: a hit whose bend the contour
    ///     clearly does not show is downgraded to half score, never a miss. <b>On by default</b>:
    /// on real DI (Guitar-TECHS) 54 of 54 played bends stayed a full hit and 84–100 %
    ///     of unplayed ones were caught. Needs <see cref="LivePlayUseCqtVerifier" />.
    /// </summary>
    public bool LivePlayJudgeBends { get; set; } = true;

    /// <summary>Judge a charted SLIDE from the pitch contour. <b>Off by default</b>: there
    /// are no real-DI slide recordings to measure it on, and on rendered charts 26.6 % of played
    /// slides were downgraded. Needs <see cref="LivePlayUseCqtVerifier" />.</summary>
    public bool LivePlayJudgeSlides { get; set; }

    /// <summary>Judge a charted VIBRATO from the pitch contour. <b>Off by default</b>: one
    /// of the two real-DI players' vibrato read as not met 67.6 % of the time. Needs
    /// <see cref="LivePlayUseCqtVerifier" />.</summary>
    public bool LivePlayJudgeVibrato { get; set; }

    /// <summary>
    /// The 0.68 single switch for all three techniques, read from old configs only and
    ///     never written back (null after <c>JsonConfigurationService.NormalizeAppConfig</c>):
    ///     <c>true</c> was a choice and turns all three per-technique switches on; <c>false</c> was
    ///     the default, so the new defaults (bends on) apply.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LivePlayJudgeTechniqueContours { get; set; }

    /// <summary>Measured input→output round-trip subtracted from detection
    /// timestamps before judging. Set by the latency calibration wizard. Also the
    /// BASELINE that any auto-refined per-device offset is leashed to.</summary>
    public double LivePlayLatencyOffsetMs { get; set; }

    /// <summary>
    ///     Auto-refine the judging offset from how you actually played, so the one-shot
    ///     loopback measurement stops drifting when the audio setup changes. Off by
    ///     default: it adjusts how hits are judged, and a player who genuinely drags
    ///     looks like an under-compensated device — see
    ///     <c>LatencyBiasCalibrator</c> for the guards that keep the two apart.
    /// </summary>
    public bool LivePlayAutoCalibrateTiming { get; set; }

    /// <summary>
    ///     Auto-refined offsets, keyed by <c>AudioDeviceKey</c> (driver + devices +
    ///     actual rate + actual buffer). A measured offset is only valid for the setup
    ///     it was measured on, so switching interface or buffer size picks up that
    ///     setup's own learned value instead of carrying the old one over.
    /// </summary>
    public Dictionary<string, double> LivePlayLatencyOffsetsByDevice { get; set; } = [];

    /// <summary>Speed the practice ladder starts at (percent of real time).</summary>
    public int LivePlaySpeedLadderStartPercent { get; set; } = 60;

    /// <summary>Speed step per clean pass (percent points).</summary>
    public int LivePlaySpeedLadderStepPercent { get; set; } = 5;

    /// <summary>Metronome count-in beats before a practice loop pass.</summary>
    public int LivePlayCountInBeats { get; set; } = 4;

    public bool LivePlaySpeedLadderEnabled { get; set; }
    public bool LivePlayDynamicDifficulty { get; set; }

    // ------ Gameplay: count-in + metronome (0.14.4 C) --------------------------
    /// <summary>Audible count-in before Play Mode gameplay starts: 0 = off, else
    /// 4 or 8 metronome clicks at the start tempo (scaled by practice speed);
    /// measure downbeats are accented. Reuses the native metronome click.</summary>
    public int GameplayCountInBeats { get; set; }

    /// <summary>Tick the metronome on every beat during Play Mode gameplay
    /// (measure downbeats accented). Driven off the existing playback tick path
    /// (AudioPlaybackCoordinator beat-crossing) — no extra timer.</summary>
    public bool GameplayMetronomeEnabled { get; set; }

    /// <summary>
    ///     Click on every CHARTED NOTE during a Play Mode run (the editor's "claps",
    ///     armed for gameplay). Distinct from the metronome, which clicks every BEAT.
    ///     <para>Scheduled sample-accurately on the RAW transport axis (ABI v26
    ///     <c>dca_engine_schedule_click</c>), so a clap is written into the same output
    ///     samples as its note's audio and the two arrive together. That is what makes
    ///     it a valid reference for hearing whether you are playing early or late —
    ///     a clap that is itself one output latency late would just teach you the
    ///     wrong timing.</para>
    /// </summary>
    public bool GameplayClapsEnabled { get; set; }

    /// <summary>Default resume-rewind lead-in (ms).</summary>
    public const int DefaultGameplayResumeRewindMs = 1500;

    /// <summary>
    ///     Rocksmith-style pause rewind: on RESUME the song backs up this many ms so
    ///     the player re-orients before notes arrive again. 0 = off. Notes already
    ///     judged before the pause are never re-judged (they replay DIMMED and their
    ///     score stands).
    /// </summary>
    public int GameplayResumeRewindMs { get; set; } = DefaultGameplayResumeRewindMs;

    /// <summary>
    ///     Performance stakes ("rock meter", 0.24 game feel): a stage-energy bar
    ///     drains on misses, and a run whose meter collapses FAILS OUT with a
    ///     retry-from-section offer — Rocksmith-style. Opt-in (default OFF).
    ///     Hard-excluded in the session wiring regardless of this flag:
    ///     Riff-Repeater/practice flows and listen-back.
    /// </summary>
    public bool GameplayRockMeterEnabled { get; set; }

    /// <summary>
    ///     Silence the recorded player of your own part during gameplay, so you hear
    ///     the band instead of the guitarist you are replacing. Needs separated stems
    ///     for the song; the pre-play card offers to make them when they are missing.
    ///     Applies per run and never persists a mute into the project.
    /// </summary>
    public bool GameplayMuteMyInstrument { get; set; }

    /// <summary>
    ///     Dynamic backing mix (0.21 game-feel): the stem of the instrument you are
    ///     playing DUCKS when you miss and SWELLS back as your streak recovers — a
    ///     smooth, run-scoped gain follower on the guitar/bass stem. Needs separated
    ///     stems. Engine-only: it never writes a mute or a volume into the project,
    ///     and restores the stem on every exit path. Off by default.
    /// </summary>
    public bool GameplayDynamicStemMix { get; set; }

    /// <summary>
    ///     Measured stem-separation cost on THIS machine: seconds of work per
    ///     second of audio, folded across runs by
    ///     <see cref="Audio.StemSeparation.StemSeparationCost" />. <b>0 means
    ///     never measured</b>, and the pre-play screen must then show no estimate
    ///     at all rather than a guess — separation cost depends on the machine
    ///     and the model, so there is no honest constant to fall back to.
    /// </summary>
    public double StemSeparationSecondsPerAudioSecond { get; set; }

    // ------ In-game tones (0.16 RS-tone feature, Phase 3) ----------------------
    /// <summary>Rebuild each of a song's Rocksmith in-game tones on the in-house
    /// tone-rig (RsTonePlaybackCoordinator) and switch them live as the chart's
    /// ToneChanges fire, instead of the player's manual pedalboard. **Off by
    /// default.** No-op for songs without parseable gear tones (own charts, RS1
    /// imports) and on builds without the native DSP (NullDspChainService); the
    /// player's VST chain is never touched.</summary>
    public bool UseInGameTones { get; set; }

    /// <summary>Show the active tone name in the gameplay HUD (a small corner
    /// label on the 3D highway) and flash it when the tone switches. **On by
    /// default.** Mirrors to the <c>GameplayToneHud</c> render channel at
    /// startup and on flip. Independent of <see cref="UseInGameTones"/>: with
    /// in-game tones off it shows the chart's own tone, with them on it shows
    /// the actually-applied preset.</summary>
    public bool ShowGameplayToneHud { get; set; } = true;

    /// <summary>Show the minimap strip under the chart timeline. **On by
    /// default.** A layout preference rather than a session one: the strip
    /// costs 40 px of permanent vertical budget, so a charter who closes it
    /// means it, and having to close it again every launch is the complaint
    /// this exists to prevent. Owned by
    /// <c>MainWindowViewModel.IsMinimapVisible</c>; the gameplay presentation
    /// hides the strip WITHOUT touching this.</summary>
    public bool ShowMinimap { get; set; } = true;

    /// <summary>Per-song override of the run's tone source (see
    /// <see cref="SongTonePreference"/>), keyed by the song identity the Live
    /// Play practice profiles use ("lib:&lt;SongKey&gt;" for library songs; read as such from the "psarc:" key earlier builds wrote, the
    /// project path for own charts). Songs without an entry follow the global
    /// <see cref="UseInGameTones"/> toggle; the Play Mode pre-play tones card
    /// writes entries and removes them again when set back to 'Global'.</summary>
    public Dictionary<string, SongTonePreference> SongTonePreferences { get; set; } = [];

    /// <summary>The songs the player plays through Play Mode's drop tuner, keyed by the
    /// same song identity as <see cref="SongTonePreferences"/> and compared case-insensitively.
    /// A song is listed only while its Get Ready switch is on; off is the default and is never
    /// written.</summary>
    public List<string> DropTunerSongs { get; set; } = [];

    // ------ Take recorder (0.17 foundations) -----------------------------------
    /// <summary>Persist the dry pre-VST input capture of every Live Play run
    /// as a take file attached to the session record. **Off by default**;
    /// config-file-only for now (no Settings UI yet).</summary>
    public bool LivePlayTakeRecorderEnabled { get; set; }

    /// <summary>Duration cap per take (minutes, clamped 1..60).</summary>
    public int LivePlayTakeMaxMinutes { get; set; } = 12;

    /// <summary>Total size cap of the profile's takes directory (MB). Cleanup
    /// deletes orphaned takes first, then the oldest ones over the cap.</summary>
    public int LivePlayTakeCacheMaxMB { get; set; } = 512;

    public int StreamingMirrorDevice { get; set; } = -1; // -1 = system default WASAPI output

    // When true the app doesn't try to dictate sample rate / buffer size to the driver.
    // Instead it starts the engine with the last known values, reads back what the driver
    // actually negotiated, and keeps polling so external changes (e.g. user changes the
    // buffer in Focusrite Control) are reflected in the UI automatically.
    public bool AutoFollowDeviceAudioSettings { get; set; }
    public string MidiOutputDeviceName { get; set; } = string.Empty;

    // AutoApplyMeasuredAudioDelay was removed. It promised to copy the measured
    // output latency into AudioDelayMs on every engine restart — but the playback
    // service ALREADY subtracts the device's output latency from every position
    // report (ApplyLatencyCompensation = EstimatedOutputLatencyMs + AudioDelayMs),
    // live, reading the device's current value. Copying it in again makes the
    // total 2 × L and pushes the playhead a full output latency BEHIND the audio.
    // The setting could not be "implemented properly": its premise was the bug.
    // (It also never did anything — nothing in the app ever read it.)

    // the chart synth plays through the player's own rig instead of the
    // chart's tones (Mixer > MIDI synth > Synth tone). Off = the chart's tones,
    // the default. Read by TransportViewModel; acted on by the editor's
    // ChartTonePreviewController.
    public bool ChartSynthPlaysOwnRig { get; set; }

    // Last-used VST selections; restored on startup after ScanVstPlugins completes.
    // LastGuitarVstPath is a legacy single-slot field kept for backward-compatibility
    // migration into GuitarVstChain (see TransportViewModel.RestoreLastVstSelections).
    public string LastGuitarVstPath { get; set; } = string.Empty;
    public string LastInstrumentVstPath { get; set; } = string.Empty;

    // DAW-style ordered VST3 effect chain applied to the live guitar input
    // signal. Persisted with stable per-slot ids so reorder/bypass don't reload
    // the underlying plugin instance. The instrument synth slot is intentionally
    // single-VSTi and continues to use LastInstrumentVstPath above.
    public List<VstChainEntry> GuitarVstChain { get; set; } = [];

    // ------ Editing (0.35, General settings) ---------------------------------

    /// <summary>
    /// The keyboard note-entry pen (Stage 2 of the editing rework): bare
    /// <c>1..9</c> toggle a string into the sound at the playhead, <c>Alt</c>+
    /// digits type multi-digit frets ("1 then 2" = fret 12) and, with no string
    /// mask set, transpose the whole shape instead. OFF by default: it ships
    /// alongside the pointer path rather than replacing it, and the two do not
    /// conflict (one is keyboard, one is pointer). Turning it off restores the
    /// pre-0.35 single-digit <c>Alt+0..9</c> exactly and makes the bare digits
    /// inert again.
    /// </summary>
    public bool UseFastChartingInput { get; set; }

    /// <summary>
    ///     Re-fingering or reshaping a chord also moves the hand shape drawn over
    ///     it onto the new template. ON by default, because the alternative is
    ///     silent corruption rather than a different style.
    ///     <para>
    ///         A chord and its hand shape normally SHARE one template, so an
    ///         in-place template edit reaches both for free. But a fingering edit
    ///         resolves through <c>FindIdenticalTemplate</c> and MINTS a private
    ///         template when the frets did not move — and the hand shape was only
    ///         carried along if it happened to be selected too. Selecting a chord
    ///         does not select the bar above it, so the ordinary gesture left that
    ///         bar describing the replaced shape, which is what the
    ///         3D highway then draws fingers from.
    ///     </para>
    ///     <para>
    ///         Off restores the pre-0.40 behaviour: only hand shapes the user
    ///         explicitly selected are repointed.
    ///     </para>
    ///     <para>
    ///         The same switch covers the highway and timeline edits that repoint a chord (fret and
    ///         string verbs, string placement, a drag-drop merge) and the two hand-shape verbs that
    ///         rebuild a bar's grip (split, wheel resize): see <c>ChordHandShapeSync</c>.
    ///     </para>
    /// </summary>
    public bool SyncHandShapeToChordTemplate { get; set; } = true;

    /// <summary>
    ///     How many frets below a TAPPED HARMONIC the hand holds — the ghost gem the
    /// tapped-harmonic shortcut puts in the hand shape: 12 is the octave, the
    ///     harmonic tapped twelve frets above the fretted note. Settings ▸ Editor; a negative
    ///     value reads as 0.
    /// </summary>
    public int TappedHarmonicFretOffset { get; set; } = 12;

    /// <summary>
    ///     After a fingering edit, move the governing FHP anchor so Rocksmith
    ///     derives the fingers the chart actually authored.
    ///     <para>
    ///         OFF by default, and that asymmetry with
    ///         <see cref="SyncHandShapeToChordTemplate" /> is deliberate: a hand
    ///         shape describes the same decision as its chord, whereas an anchor
    ///         says where the player's HAND goes — a much bigger claim, often
    ///         shared with neighbouring sounds the fingering edit never touched.
    ///         Inspector rule E04 reports the disagreement either way, so leaving
    ///         this off costs a warning, not a silent defect.
    ///     </para>
    /// </summary>
    public bool SyncAnchorToChordFingering { get; set; }

    /// <summary>
    ///     Leave two-string shapes without an automatic chord name, and strip any
    ///     they already carry on export.
    ///     <para>
    ///         <b>On by default.</b> Two pitches do not determine a chord, so a
    ///         detected name for a double stop is a guess the charter cannot tell
    ///         apart from a real one. Editor on Fire ships the same switch
    ///         (<c>eof_dont_auto_name_double_stops</c>) and rscharter never
    ///         auto-names at all; both draw a blank name as nothing.
    ///     </para>
    ///     <para>
    ///         It gates the AUTOMATIC namer only — the inspector's candidate list
    ///         is never filtered, so a double stop the charter wants labelled can
    ///         still be labelled by hand, and a hand-written name survives the
    ///         export strip. See <see cref="DoubleStopNameOptions" />.
    ///     </para>
    /// </summary>
    public bool SuppressDoubleStopChordNames { get; set; } = true;

    /// <summary>
    ///     Name a chord template the editor makes (a new chord, a reshape, an accepted hand-shape
    ///     suggestion, Generate fingerings, a Guitar Pro chord its file leaves unnamed) from the
    ///     pitch-based suggester. Off leaves every new chord unnamed; a name a source carries is
    ///     kept either way. On by default. Read through <c>NewChordNames</c>.
    /// </summary>
    public bool AutoNameNewChords { get; set; } = true;

    /// <summary>
    ///     Exempt root-and-fifth shapes from
    ///     <see cref="SuppressDoubleStopChordNames" />, so <c>G5</c> keeps its name
    ///     while an ambiguous two-note shape does not. Off by default, which is
    ///     EOF's behaviour exactly.
    /// </summary>
    public bool KeepPowerChordNames { get; set; }

    /// <summary>
    ///     How the chord panel's Suggest pills and "Auto-fill fingerings &amp; names"
    /// spell a chord name. <see cref="ChordNamingStyle.Eof" /> (the
    ///     default) is Editor on Fire's spelling, which the owner's finished
    ///     charts carry (<c>A#min7</c>); <see cref="ChordNamingStyle.Musical" />
    ///     spells in the song's key with slash chords (<c>Bbm7</c>, <c>Cm7/Bb</c>).
    /// </summary>
    public ChordNamingStyle ChordNamingStyle { get; set; } = ChordNamingStyle.Eof;

    /// <summary>
    ///     The AI chat's hand-position/fingering task the charter last PICKED on a guitar
    /// arrangement. Null until they pick one, which means the measured default
    ///     (<see cref="Ai.Chart.AiFingeringDefaults" />: fingering only, FHP stays the
    ///     algorithm's). An explicit pick always wins and is kept here.
    /// </summary>
    public Ai.Chart.AiChartJob? AiFingeringJobGuitar { get; set; }

    /// <summary>The same on a bass arrangement, where the measured default keeps the
    /// model's hand positions OFF (the algorithm beat every model).</summary>
    public Ai.Chart.AiChartJob? AiFingeringJobBass { get; set; }

    /// <summary>
    ///     Auto-synced lyrics: span the last syllable of a WORD toward the next
    ///     word's start (minus the minimum note distance) instead of ending it
    ///     where the alignment model stopped hearing it. ON by default — the raw
    ///     model ends leave short pills with dead gaps between words that are
    ///     sung legato. Syllables INSIDE a word always span; this gates only the
    ///     word-to-word boundary within a phrase.
    /// </summary>
    public bool LyricSpanWordGap { get; set; } = true;

    /// <summary>
    ///     Auto-synced lyrics: span the last syllable of a PHRASE toward the
    ///     next phrase's first syllable. OFF by default, and that asymmetry with
    ///     <see cref="LyricSpanWordGap" /> is deliberate: a phrase break usually
    ///     IS a breath or an instrumental gap, so holding the last syllable
    ///     across it reads as a sustain that was never sung.
    /// </summary>
    public bool LyricSpanPhraseGap { get; set; }

    public MinNoteDistanceMode MinNoteDistanceMode { get; set; } = MinNoteDistanceMode.MeasureFraction;
    public double MinNoteDistanceMs { get; set; } = 10.0;
    public int MinNoteDistanceDivisor { get; set; } = 64;

    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupIntervalMinutes { get; set; } = 5;

    /// <summary>How many auto-backup zips to keep per project, newest first; 0 keeps
    /// every one. Without a cap an hour of editing added 12 zips per project forever.</summary>
    public int AutoBackupKeepCount { get; set; } = 50;

    // ------ Crash recovery (0.12.0) ------------------------------------------
    // Frequent crash snapshots of the open project into ConfigRoot/recovery.
    // Complements the zip auto-backups above: backups are long-lived history
    // next to the project, recovery snapshots are a single self-deleting
    // "work since the last save" copy whose presence at startup means the
    // previous session died mid-edit.
    public bool RecoveryEnabled { get; set; } = true;
    public int RecoveryAutosaveSeconds { get; set; } = 60;

    // New-project audio padding: prepend silence before the song and append silence after it
    // so users have time to position the first beat and breathe after the last note.
    public bool PadSilenceOnNewProject { get; set; } = true;
    public double LeadingSilenceSeconds { get; set; } = 10.0;
    public double TrailingSilenceSeconds { get; set; } = 2.0;

    // Configurable normal Ctrl+V paste contents. Each toggle controls whether
    // that object class is included in a normal paste; everything stays on
    // the clipboard regardless, so disabled types are simply skipped on apply.
    public bool PasteIncludeNotes { get; set; } = true;
    public bool PasteIncludeChords { get; set; } = true;
    public bool PasteIncludeHandShapes { get; set; } = true;
    public bool PasteIncludeFretAnchors { get; set; } = true;
    public bool PasteIncludeEvents { get; set; } = true;
    public bool PasteIncludeTones { get; set; } = true;

    // 3D preview overlays (off by default). Toggleable via hotkey.
    public bool Show3DFingering { get; set; }

    /// <summary>
    ///     Home's hero moves: the aurora drifts, the beams sway, the lit band sweeps the dot wall,
    ///     the cover tilts toward the pointer. Off (the default) draws the same image once and
    ///     never repaints it (owner).
    /// </summary>
    public bool AnimatedHomeHero { get; set; }

    // ------ 3D graphics (0.14.2, Settings → Graphics) -------------------------
    // Display settings feed the Vulkan renderer; the effect/readability toggles
    // mirror into ProjectState.UserSettings at startup and on Settings save
    // (the 3D snapshot reads UserSettings each frame). Defaults preserve the
    // pre-0.14.2 behaviour: 100% scale, uncapped, FIFO (vsync), 4× MSAA.

    /// <summary>3D render resolution as a percentage of the panel size
    /// (50–200, clamped on apply). 100 = render directly at panel size.</summary>
    public int RenderScalePercent { get; set; } = 100;

    /// <summary>Upper bound for the 3D render loop in frames/second.
    /// 0 = unlimited (every loop iteration renders).</summary>
    public int MaxFps { get; set; }

    /// <summary>Paused-loop trickle rate in Hz (1–10, clamped on apply): how
    /// often the parked 3D preview still refreshes as the safety net for
    /// state without a wake signal. 10 = the pre-0.14.2 constant.</summary>
    public int IdleFps { get; set; } = 10;

    /// <summary>Swapchain present mode: "Fifo" (vsync), "Mailbox" (fast
    /// vsync) or "Immediate" (vsync off). Unsupported values fall back to
    /// FIFO — the only mode Vulkan guarantees. Default "Mailbox" since
    /// v0.32.5: pixel-identical, tear-free output like FIFO but the present
    /// never blocks on vblank, so the fps-capped 3D loop can actually reach a
    /// high-refresh panel's rate (FIFO quantized the loop to whole vblanks —
    /// 200 fps measured on a 280 Hz monitor). Clamped by
    /// SwapchainPolicy.ChoosePresentMode where Mailbox is unsupported.</summary>
    public string PresentMode { get; set; } = "Mailbox";

    /// <summary>One-time v0.32.5 migration latch: existing configs that still
    /// carry the historical "Fifo" DEFAULT are moved to "Mailbox" once (see
    /// JsonConfigurationService.NormalizeAppConfig). A user who re-selects
    /// Fifo afterwards keeps it — this flag stays true.</summary>
    public bool PresentModeMailboxMigrated { get; set; }

    /// <summary>MSAA sample count for the 3D highway: 1 (off), 2, 4 or 8.
    /// Clamped to what the device's framebuffer supports on apply.</summary>
    public int MsaaSamples { get; set; } = 4;

    /// <summary>3D preview camera "fill" — how much of the frame the neck fills,
    /// as a percent of the tuned default (80–120, clamped on apply; 100 = the
    /// default framing). Higher fills more (camera closer); lower pulls the
    /// camera back so the board reads smaller. Surfaced as the "camera distance"
    /// slider (the inverse: more distance = smaller board).</summary>
    public int Preview3DCameraFill { get; set; } = 100;

    // Highway/showlight effect toggles (persisted here, not
    // session-only state in UserSettingsState).
    public bool EnableNoteHitExplosions { get; set; } = true;
    public bool EnableHighwayNearFade { get; set; } = true;
    public bool EnableShowlightBeams { get; set; } = true;
    public bool EnableShowlightFog { get; set; } = true;

    // Gameplay-readability aids (0.14.2 B): dimmed upcoming-anchor fret
    // numbers + floor sweep toward the upcoming anchor zone.
    public bool EnableAnchorPreview { get; set; } = true;
    public bool EnableAnchorSweep { get; set; } = true;

    /// <summary>How many ms before an anchor (FHP) change the upcoming-anchor
    /// preview starts fading in (200–3000, clamped on apply).</summary>
    public int AnchorPreviewLeadMs { get; set; } = 1000;

    // ---- highway pace + draw distance ---------------------------------------

    /// <summary>
    ///     How far AHEAD of the audio playhead the picture is drawn, in ms (0–50,
    ///     clamped on apply), to cancel display latency: frame submit → DWM commit →
    ///     scanout, typically one to two refresh intervals.
    ///
    ///     <para>This is the VIDEO half of the A/V alignment; <see cref="RigConfig.AudioDelayMs"/>
    ///     is not a substitute, because that shifts the transport clock the Live Play
    ///     matcher judges against, whereas this is applied in the renderers only and
    ///     cannot bias judging.</para>
    ///
    ///     <para>Default 0 = unchanged. The composed 3D path measures its own present
    ///     latency and adds this on top as a trim; the native path (the shipping
    ///     default) has no measurement, so this is the whole compensation.</para>
    /// </summary>
    public double VisualLeadMs { get; set; }

    /// <summary>
    ///     Force <see cref="HighwayScrollSpeed"/> on every song instead of
    ///     honouring the value the chart's author stored. Off by default, so an
    ///     imported CDLC scrolls the way it was charted.
    /// </summary>
    public bool HighwayScrollSpeedOverrideEnabled { get; set; }

    /// <summary>
    ///     The global scroll-speed override, in ROCKSMITH UNITS — <b>lower is
    ///     faster</b> (0.5–3.0, clamped on apply; 1.3 is Rocksmith's default).
    ///     Only consulted when <see cref="HighwayScrollSpeedOverrideEnabled"/>
    ///     is set. A per-playthrough value chosen on the pre-song card still
    ///     beats it.
    /// </summary>
    public double HighwayScrollSpeed { get; set; } = 1.3;

    /// <summary>
    ///     How far down the highway the player can see, in world units (20–80,
    ///     clamped on apply; 35 is the historical board length). Unlike scroll
    ///     speed this has no chart-authored source, so it is a plain preference
    ///     and always applies.
    /// </summary>
    public double HighwayDrawDistanceZ { get; set; } = 35.0;

    // Visual quality effects (0.14.2 C), each individually toggleable.
    public bool EnableBeatPulse { get; set; } = true;
    public bool EnableDepthFog { get; set; } = true;
    public bool EnableVignette { get; set; } = true;
    public bool EnableEnhancedHitFeedback { get; set; } = true;
    public bool EnableCrowdBackground { get; set; } = true;

    // 0.15.6 highway fidelity (plan 5.6): in-shader directional lighting.
    public bool EnableHighwayLighting { get; set; } = true;

    // 0.15.6 highway fidelity (plan 5.5): bloom post chain. Default OFF —
    // real GPU cost (extra passes), unlike the vertex-level effects.
    public bool EnableBloom { get; set; }

    // 0.15.6 highway fidelity (plan 5.7): stateless ambience/feedback,
    // both suppressed during Live Play.
    public bool EnableStringShimmer { get; set; } = true;
    public bool EnableHitSparks { get; set; } = true;

    // 0.15.6 highway fidelity: composite-time ACES-approx filmic tonemap.
    // Default OFF — regrades the whole picture.
    public bool EnableFilmicTonemap { get; set; }

    /// <summary>The editor's String order (Settings ▸ Editor): <c>true</c> is Tab (low string
    /// at the bottom), <c>false</c> Rocksmith (low string on top). It drives the 2D timeline,
    /// its hit-testing and string-move keys, and the editor's 3D preview. Mirrored into
    /// ProjectState/UserSettingsState like the graphics toggles above. Default OFF.</summary>
    public bool InvertStrings { get; set; }

    /// <summary>The String order of the highway during a Play Mode run (Settings ▸ Play):
    /// <c>true</c> is Tab. Its own setting, so a charter who reads tab can still play in the
    /// game's layout. Default OFF (Rocksmith).</summary>
    public bool PlayModeInvertStrings { get; set; }

    /// <summary>One-time migration latch: a config written while one String order served the
    /// editor and Play Mode alike gets that choice copied into <see cref="PlayModeInvertStrings" />
    /// once (JsonConfigurationService.NormalizeAppConfig), so nobody's run changes layout.</summary>
    public bool StringOrderSplitMigrated { get; set; }

    // ------ 2D timeline visuals (0.14.3, Settings → Graphics) ------------------
    // Consumed by the OpenGL editor timeline via the Timeline2DVisuals static
    // channel (mirrored at startup and on Settings load/save, like the 3D
    // toggles above). Conservative defaults: polish on, lane tinting off.

    /// <summary>Master toggle for the 0.14.3 decorative timeline pass (note
    /// gem gradients/rims, waveform gradient body, beat-grid fade ramp,
    /// playhead glow boost + scrub trail, ribbon chip shadows). Off restores
    /// the pre-0.14.3 flat look.</summary>
    public bool Timeline2DPolishEnabled { get; set; } = true;

    /// <summary>Whisper-faint per-string lane wash (string colour at ~4%
    /// alpha) on the 2D timeline. Off by default; always off in Marks LOD.</summary>
    public bool Timeline2DLaneTint { get; set; }

    // Main-timeline ribbon rows (the labeled toggles in the track header
    // strip). Applied to whichever arrangement is ACTIVE; stacked rows keep
    // their own in-memory per-row flags.
    public bool RibbonShowTones { get; set; } = true;
    public bool RibbonShowSections { get; set; } = true;
    public bool RibbonShowPhrases { get; set; } = true;
    public bool RibbonShowLyrics { get; set; } = true;
    public bool RibbonShowMeasureInfo { get; set; } = true;
    public bool RibbonShowLinks { get; set; } = true;
    // Timeline lane menu: global, like the five row toggles above.
    public bool RibbonAutoHideEmpty { get; set; } = true;
    public bool RibbonCompactRows { get; set; }

    // ------ Stem separation -------------------------------------------------
    // The Active stem model id (from StemModelCatalog), read by both backends. Empty =
    // no explicit choice: the user is on the DEFAULT (StemDefaultModel - BS-RoFormer SW 13 s
    // on an 8 GB GPU, the 4 s SW on a 6 GB one, htdemucs-6s-onnx on the CPU, fetched on
    // demand and never written here).
    public string HtDemucsActiveModelId { get; set; } = string.Empty;
    // The default-model upgrade (StemDefaultModel.UpgradeTarget: the 13 s BS-RoFormer SW for a
    // default user who already separates with another default) the user said no to, so Separate
    // Stems does not ask again. Empty = never declined. Never changes the Active model.
    public string StemDefaultUpgradeDeclinedId { get; set; } = string.Empty;
    // Pickled-checkpoint import consent: URL -> SHA-256 of the file the user allowed to
    // run code ("" until the first download pins it). See CheckpointPickleConsent.
    public Dictionary<string, string> StemCheckpointPickleConsent { get; set; } = [];
    // Execution provider the managed ONNX stem-separation backend should use.
    // Values: "Auto", "DirectML", "Cpu". Case-insensitive. Defaults to Auto.
    // Auto and DirectML resolve to DirectML on the chosen (or fastest) DirectX 12 GPU,
    // else the CPU (StemExecutionDecision). This build's ONNX Runtime has no CUDA; a
    // "Cuda" stored by an older version reads as Auto.
    public string StemSeparationExecutionProvider { get; set; } = "Auto";
    // The GPU stem separation runs on, as "deviceId:adapter name"
    // (StemGpuRanking.KeyFor). Empty = the fastest usable adapter. Set from the GPU
    // picker Settings > Stem models shows on a machine with two or more GPUs.
    public string StemSeparationGpuAdapter { get; set; } = string.Empty;
    // NOTE: StemSeparationMaxConcurrent and StemSeparationEnableCpuFallback are
    // gone with the JuceSharp orchestrator they configured, which never separated
    // anything. An older appconfig.json that still carries them loads: the
    // serializer ignores unknown members and the next save drops them
    // (JsonConfigurationServiceTests).
    // NOTE: there is deliberately NO StemMixerVisible here. The Stem Mixer
    // is not a dock pane (it is an INSPECTOR TAB — see
    // MainWindow.Docking.InitializeBottomDock), so MainWindowViewModel
    // .IsStemMixerVisible is now a one-shot "select the Mixer tab" trigger, not
    // a restorable pane state. Persisting it would force the Mixer tab open on
    // every launch. Real pane visibility rides in DockLayout below.

    // Appearance / Theme
    //
    // `ThemePresetId` is GONE (0.62). The app has one theme, so there is
    // nothing to select and nothing to persist; a config written before that
    // still carries the property and System.Text.Json ignores it, which is
    // exactly the behaviour wanted - no migration, no latch, no reader.
    // The per-token overrides below are a different feature and stay: they
    // customise THE theme rather than choosing between themes.
    public Dictionary<string, string> ThemeColorOverrides { get; set; } = [];

    // Accessibility: named colour-blind-safe override for the seven fretboard
    // string colours (see DotCharter.UI StringColorPresets). "default" uses the
    // theme's own string colours. Applied on top of ThemeColorOverrides for the
    // String* tokens.
    public string StringColorPresetId { get; set; } = "default";

    // Accessibility: global UI scale applied as a LayoutTransform on the shell
    // root (1.0 = 100%). Clamped to a sane range on apply. 1.0 is a no-op.
    // This is the MANUAL arm's value and is only consulted when UiScaleAuto is
    // false; it is KEPT across a spell in Auto so switching back restores it.
    public double UiScale { get; set; } = 1.0;

    // Accessibility: derive the UI scale from the shell window's client height
    // instead of using the number above (DotCharter.UI's AutoUiScale — 1080
    // DIPs => 1.0, quantised to 0.05, clamped 1.0..2.0, with hysteresis).
    //
    // DEFAULTS TRUE, which is the whole point of the setting: every surface is
    // laid out in fixed pixels against a ~1080p design, so on a 1440p or 4K
    // monitor the app is correct and SMALL. Defaulting to false would leave
    // that behind a control the affected user has no reason to go looking for.
    public bool UiScaleAuto { get; set; } = true;

    // Main window layout (persisted splitter positions).
    // Bottom dock (Inspector + 3D Preview) row height in pixels. The timeline
    // row above the minimap is `*` and absorbs the remaining space, so saving
    // the dock height alone is enough to fully restore the vertical layout.
    public double? LayoutDockHeight { get; set; }

    // The timeline area (sheet strip + timeline + song map, MainSplitGrid row 0)
    // at the same moment. Saved WITH the dock height, the pair restores the
    // user's splitter as an exact ratio; the dock height alone cannot, because the
    // space the two rows share is not the window height minus a constant.
    public double? LayoutTimelineAreaHeight { get; set; }

    // Inspector column width in pixels.
    public double? LayoutInspectorWidth { get; set; }

    // The AI chat pane: whether its right-hand column shows, and its width in pixels.
    public bool LayoutAiChatVisible { get; set; }

    public double? LayoutAiChatWidth { get; set; }

    // Sheet-music strip: splitter-resizable height in pixels and the user's
    // zoom factor (1.0 = fit the strip height; >1 enlarges notation/tab text
    // and scrolls vertically).
    public double? LayoutSheetHeight { get; set; }
    public double SheetMusicZoom { get; set; } = 1.0;
    // Which staves the sheet strip lays out: 0 = notation + tab,
    // 1 = notation only, 2 = tab only (fewer staves render larger).
    public int SheetMusicStaves { get; set; }

    // Sheet layout: false = single horizontal STRIP (the synced-follow editing
    // default), true = GP8-style PAGE view (systems wrap top-to-bottom, scrolls
    // vertically, playhead-follow scrolls to the active system).
    public bool SheetMusicPageMode { get; set; }

    // Dock.Avalonia: full pane-layout snapshot (docked/floating/pinned/closed
    // per pane, float bounds, active tabs). Version-guarded; null or a
    // mismatched version falls back to the default layout.
    public DockLayoutState? DockLayout { get; set; }

    // Per-purpose remembered file picker directories. Each picker uses its own
    // history so opening an audio file doesn't clobber the GP folder etc.
    public string LastRscpDir { get; set; } = string.Empty;
    public string LastAudioDir { get; set; } = string.Empty;
    public string LastEofDir { get; set; } = string.Empty;
    public string LastGpDir { get; set; } = string.Empty;
    public string LastRsXmlDir { get; set; } = string.Empty;
    public string LastMidiDir { get; set; } = string.Empty;
    public string LastLintFolderDir { get; set; } = string.Empty;
    public string LastProjectFolderDir { get; set; } = string.Empty;
    public string LastExportDir { get; set; } = string.Empty;
    public string LastAlbumArtDir { get; set; } = string.Empty;

    // ------ External media tools (YouTube audio import) ----------------------
    // Optional overrides for the yt-dlp / ffmpeg executables. Empty = resolve
    // the bundled copy next to the app, then the dev packer cache (see
    // MediaToolResolver; PATH only on the explicit DOTCHARTER_TOOLS_FROM_PATH=1 opt-in).
    public string YtDlpPath { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;

    // ------ Guitar Pro / tab import ------------------------------------------

    // NOTE (v0.33.2): GpImportDeadStrumsAsOpenMutes was REMOVED. It zeroed the
    // frets of a fully fret-hand-muted strum, which destroyed the authored shape
    // and forced a bogus all-open chord template; EOF and Charter both preserve
    // the shape, so preserving it is now unconditional (GpPostProcessor.
    // CleanDeadChordNotes). Old config files still carrying the key are fine —
    // System.Text.Json ignores unknown properties on read.

    /// <summary>
    ///     Ported EOF setting <c>eof_gp_import_truncate_short_notes</c>: a single
    ///     note whose authored sustain is SHORTER than one quarter note has its
    ///     sustain dropped on import. Default on (EOF default) — this is also the
    ///     app's pre-existing behaviour, so ON changes nothing; turning it OFF is
    ///     the new capability (short single-note sustains are preserved). Flows
    ///     through <c>GpImportSettings</c> to <c>GpPostProcessor</c>, so
    ///     gpif, legacy binary and Songsterr all honour it. Sounds that need a
    ///     sustain to render a technique (bend / slide / vibrato / tremolo /
    ///     link-next) are never truncated.
    /// </summary>
    public bool GpImportTruncateShortNotes { get; set; } = true;

    /// <summary>
    ///     Ported EOF setting <c>eof_gp_import_truncate_short_chords</c>: a chord
    ///     whose authored sustain is shorter than a quarter note has its sustain
    ///     dropped. Default OFF (EOF default) because the tab-import handshape
    ///     convention already carries a chord's duration on its hand-shape bar and
    ///     renders the chord as a short strike — turning this ON additionally
    ///     drops the sustain of short chords that a sustain-visible technique
    ///     would otherwise keep.
    /// </summary>
    public bool GpImportTruncateShortChords { get; set; }

    /// <summary>
    ///     Honour the SHORT RESTS a tab writes between repeated attacks, which
    ///     Rocksmith has no way to spell. Default ON.
    ///     <para>
    ///     Rocksmith drops a sub-quarter sustain, so a riff written as
    ///     eighth-note / eighth-rest / eighth-note (owner report,
    ///     measures 40-41 of a real GP8 file at 220 bpm) arrives as a run of
    ///     bare strikes with the articulation gone. RS has no staccato mark, and
    ///     the charting convention is to spell staccato as an ACCENT — so a note
    ///     followed by a rest of at most its own value (and at most a
    ///     quarter) is imported accented.
    ///     </para>
    ///     <para>
    ///     CHORDS take the other half of the same rule and need no accent: a
    ///     hand-shape bar already carries duration, so a rest between two
    ///     identical strums simply BREAKS the shared bar instead of being
    ///     swallowed by it (<c>ChordHandShapeBuilder</c>'s 2-beat merge gap).
    ///     </para>
    /// </summary>
    public bool GpImportPreserveShortRests { get; set; } = true;

    /// <summary>
    ///     When a new project's audio tags are in a non-Latin script, ask
    ///     MusicBrainz for the official Latin-script artist and title. Default
    ///     ON — the request is anonymous, rate-gated and fire-and-forget, and
    ///     NOTHING is applied automatically: the answer arrives as a suggestion
    ///     the charter accepts with one click, because the artist half is
    ///     reliable and the title half is best-effort.
    /// </summary>
    public bool OnlineLatinNameLookup { get; set; } = true;

    /// <summary>
    ///     Ported EOF setting: a beat text of "T", "S" or "P" (case-insensitive,
    ///     trimmed) marks that note as tap / slap / pop. Default on. gpif and
    ///     legacy binary honour it; Songsterr has no beat-text channel.
    /// </summary>
    public bool GpImportBeatTextTechniques { get; set; } = true;

    /// <summary>
    ///     Ported EOF setting: Guitar Pro section markers (and qualifying beat
    ///     texts) import as RS phrases, and — when the marker name is a valid RS
    ///     section name — also as section EventPoints. Default OFF. gpif only.
    /// </summary>
    public bool GpImportMarkersToSections { get; set; }

    /// <summary>
    ///     How far (in frets) below/above the target a materialized GP slide-in
    ///     starts when the file names no origin fret (GP <c>Slide</c> 16/32). The
    ///     owner default is 2 (Log Charter's choice); EOF uses 1. Valid 1–3 — the
    ///     importer clamps out-of-range values.
    /// </summary>
    public int GpImportSlideInFretDistance { get; set; } = 2;

    /// <summary>
    ///     Ported EOF setting: import the Guitar Pro time signatures into the
    ///     project's beat map. Default on. When off, the beat map is built as 4/4
    ///     regardless of the file's meters.
    /// </summary>
    public bool GpImportTimeSignatures { get; set; } = true;

    /// <summary>
    ///     Ported EOF setting: discard tempo changes that do not fall on a beat
    ///     when building the beat map. Default OFF (EOF default).
    /// </summary>
    public bool GpImportDropMidBeatTempoChanges { get; set; }

    // ------ Rocksmith package export -----------------------------------------
    // Last-used export dialog values (the 0.9.0 export presets).
    public string LastExportAppId { get; set; } = "248750";
    public double ExportAudioVolume { get; set; } = -8.0;
    public double ExportPreviewVolume { get; set; } = -7.0;
    public bool ExportIncludeVocals { get; set; } = true;

    // ------ Save companions --------------------------------------------------
    // Artifacts written alongside the .dcproj on every save, so a project is
    // directly openable by Editor on Fire and loadable by Rocksmith without a
    // manual export step. Both are best-effort: a failure here is reported but
    // NEVER fails the project save.

    /// <summary>Write an Editor on Fire .eof next to the project on every save.
    /// Cheap (a few ms) — it is a plain binary write.</summary>
    public bool SaveCompanionEof { get; set; } = true;

    /// <summary>Transcode the song audio to a sibling .ogg when writing the .eof,
    /// so EOF finds its audio instead of prompting. Needs ffmpeg; done once and
    /// cached.</summary>
    public bool SaveCompanionEofAudio { get; set; } = true;

    /// <summary>Open a Rocksmith song (.psarc) with only its hardest difficulty level. Off keeps the
    /// official ladder, which re-exports with it and feeds Live Play's dynamic difficulty; on
    /// squashes every arrangement to one level on load, so the editor and its checks see only the full chart.</summary>
    public bool PsarcOpenHardestLevelOnly { get; set; }

    /// <summary>Build a Rocksmith .psarc in the background after every save.
    /// Never blocks the save; rapid saves coalesce into one build.</summary>
    public bool SaveCompanionPsarc { get; set; } = true;

    /// <summary>
    ///     Codec for the companion psarc's audio: "Vorbis" (the default; the codec
    ///     the game itself ships, written by Wwise when installed and otherwise by
    /// JuceSharp's own encoder) or "Pcm" (~11x larger). A stored
    ///     "ImaAdpcm", an older default, reads as Vorbis: the game does not
    ///     play IMA-ADPCM, the song never starts (<c>SaveCompanionService.ParseCodec</c>).
    /// </summary>
    public string SaveCompanionPsarcCodec { get; set; } = "Vorbis";

    /// <summary>
    ///     Write a DLC Builder project (<c>.rs2dlc</c>) beside the project on
    ///     every save. Cheap (one small JSON file) — it just names the XML the
    ///     bundle already wrote, plus the audio and the album art.
    /// </summary>
    public bool SaveCompanionRs2Dlc { get; set; } = true;

    /// <summary>
    /// "RS XML for 6-string tools": a 7-/8-string guitar saved as <c>.xml</c> is
    ///     written as a six-string arrangement (the extra strings' notes dropped), because
    ///     EOF aborts the import on a <c>string</c> above 5. Off by default, which keeps the
    ///     spec XML with all strings. The DTRS XML bundle and the <c>.rs2dlc</c> always use
    ///     the six-string projection (DLC Builder is their consumer).
    /// </summary>
    public bool RsXmlSixStringProjection { get; set; }

    /// <summary>
    ///     "RS XML phrases between beats as moveR": write a phrase that starts on a note
    ///     between two beats as moveR&lt;N&gt; on the beat before it (the XML save and the DTRS
    ///     XML bundle). Off by default — the real time, which DLC Builder's checks accept and
    ///     which needs no improvement step to land; the package export always uses it.
    /// </summary>
    public bool RsXmlMoverPhrases { get; set; }

    /// <summary>
    ///     "Generate dynamic difficulty for single-level arrangements": every Rocksmith
    ///     export (the <c>.psarc</c>, the save companions and the DTRS XML bundle the
    ///     <c>.rs2dlc</c> names) runs DLC Builder's level generator on a COPY of each
    ///     arrangement that has exactly one difficulty level, as DLC Builder itself does on
    ///     every build (<c>Export/DynamicDifficulty/DynamicDifficultyExport</c>). The chart
    ///     keeps its one level. On by default.
    /// </summary>
    public bool ExportGenerateDynamicDifficulty { get; set; } = true;

    /// <summary>
    ///     "Apply DLC Builder's fixes": every Rocksmith export runs DLC Builder's arrangement
    ///     improvements (<c>ArrangementImprover.applyAll</c>, its default; port in
    ///     <c>Export/DynamicDifficulty/DdImprover</c>) on a COPY of every arrangement, before
    ///     the levels are generated, as DLC Builder does. The chart is not changed. On by default.
    /// </summary>
    public bool ExportApplyDlcBuilderFixes { get; set; } = true;

    /// <summary>
    ///     How many levels each phrase gets when dynamic difficulty is generated: "Simple"
    ///     (DLC Builder's default) or "MLModel" (its trained model). Anything else reads as Simple.
    /// </summary>
    public string ExportDdLevelCountMode { get; set; } = "Simple";

    /// <summary>The user's Rocksmith keys, empty by default and always
    /// written so the file shows where to paste them. No UI; secrets, redacted
    /// in every bundle. See <see cref="RocksmithKeysConfig" />.</summary>
    public RocksmithKeysConfig RocksmithKeys { get; set; } = new();

    /// <summary>
    ///     Every top-level member of <c>appconfig.json</c> this build does not declare,
    /// kept as it was read and written back unchanged. A newer build's
    ///     setting therefore survives this one's saves, so running an older build, a
    ///     downgrade or a rollback deletes nothing. Null when the file held nothing
    ///     unknown. Builds before this one drop such members; nothing can fix those.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownSettings { get; set; }

    /// <summary>Charter/author name pre-filled on new projects and used as the
    /// psarc "Package Author" when a chart has none set.</summary>
    public string DefaultCharterName { get; set; } = "";

    // Where the export dialog's Test export writes (DLC Builder's test folder);
    // blank = a "test" folder beside the output file. Export-preset members an
    // older build stored are not read; they ride along in UnknownSettings.
    public string ExportTestFolder { get; set; } = string.Empty;

    // User-authored "what does ALIKE mean" patterns for select-like and the
    // pattern search, each carrying its own keybinds. Managed by
    // Editing.Selection.Patterns.MatchPatternStore; authored in the Pattern
    // Builder dialog. MatchPatternsSeeded is what makes the starter set arrive
    // exactly once — an empty list is a legitimate state (the user deleted them
    // all) and must not be re-seeded on the next launch.
    public List<Editing.Selection.Patterns.MatchPattern> MatchPatterns { get; set; } = [];
    public bool MatchPatternsSeeded { get; set; }

    public string ActiveTonePresetId { get; set; } = string.Empty;

    // Persisted main window size/position.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // ------ Online accounts + scoreboard: REMOVED --------------------
    // `OnlineApiUrl`, `OnlineAnonKey`, `OnlineLiveStatsEnabled` and
    // `RoomBoardNoticeDismissed` lived here before the online backend
    // was removed. A config file written by an older build still loads: the
    // deserializer ignores unknown JSON properties, so the four keys are simply
    // dropped on the next save. Do not re-add these names for anything else —
    // an old file would silently supply a value.

    // ------ Serverless LAN scoreboard: REMOVED ---------------------
    // `LanDisplayName`, `LanPeerId` and `AllowPeersDownloadSongs` lived here before
    // the LAN scoreboard (and LAN multiplayer) were removed (see git history). An older config file still loads — unknown JSON
    // properties are ignored and dropped on the next save
    // (JsonConfigurationServiceTests.LoadAppConfig_WithKeysOfRemovedFeatures_StillLoads).
    // Do not re-add these names for anything else.

    // ------ Auto-update + GitHub sign-in (0.12.5) -----------------------------
    // Fire-and-forget update check against GitHub releases on startup;
    // result is surfaced non-modally via the status message line.
    public bool UpdateCheckOnStartup { get; set; } = true;

    // The GitHub sign-in's client id is the update feed's (IUpdateFeed.SignInClientId),
    // never a setting: an older config's "GitHubOAuthClientId" key is ignored on load and
    // dropped on the next save. The GitHub token itself is NOT stored here: it lives
    // DPAPI-encrypted in github-token.dat.

    // ------ Crash reporting (opt-in, local only) ------------------------------
    /// <summary>
    /// Opt-in: on an unhandled exception, write a redacted local crash bundle
    /// (exception + stack + recent log tail + minimal system info) under
    /// <c>&lt;ConfigRoot&gt;/crash-reports/</c>. **Off by default.** The bundle is
    /// NEVER auto-uploaded — it stays on disk for the user to inspect and attach
    /// to a bug report. A redaction pass strips the OS user name (incl. inside
    /// file paths), tokens and credential-ish strings before anything is written.
    /// </summary>
    public bool CrashReportOptIn { get; set; }
}
