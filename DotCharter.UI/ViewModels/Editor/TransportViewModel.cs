// TransportViewModel root: the injected services and every backing field, the constructor and VST restore, play/pause/stop/seek, grid snap and the A/B loop + audition.
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.Services.Audio.Coordination;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.UI.ViewModels.Editor;

public partial class TransportViewModel : ViewModelBase, IEditorTransport
{
    private static readonly string[] GmInstruments =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1",
        "Electric Piano 2", "Harpsichord", "Clavi",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica",
        "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)",
        "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass", "Slap Bass 1",
        "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "SynthStrings 1", "SynthStrings 2", "Choir Aahs", "Voice Oohs",
        "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "SynthBrass 1", "SynthBrass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)",
        "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)",
        "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)",
        "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bag pipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause",
        "Gunshot"
    ];

    private readonly IAudioPlaybackService _audioService;
    private readonly IConfigurationService _configService;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private readonly ProjectState _projectState;

    [ObservableProperty] private double _bandPassFreq = 20;
    [ObservableProperty] private double _bandPassQ = 0.707;

    [ObservableProperty] private double _bpm = 120.0;
    [ObservableProperty] private bool _clapsEnabled;

    [ObservableProperty]
    private AvaloniaList<Point> _eqCurveData =
    [
        new Point(0, 100), new Point(0, 0), new Point(100, 0), new Point(100, 100),
    ];
    [ObservableProperty] private int _gridDivisor = 4;

    /// <summary>The toolbar picker's rows — the historical divisor ladder with
    /// honest beat-fraction labels (see <see cref="GridSnapOption" />).
    /// <see cref="GridDivisor" /> stays the stored value.</summary>
    public static IReadOnlyList<GridSnapOption> GridSnapOptions { get; } =
        new[] { 4, 8, 12, 16, 24, 32, 48, 64, 96 }.Select(GridSnapOption.For).ToArray();

    /// <summary>ComboBox-facing projection of <see cref="GridDivisor" /> — a
    /// divisor from outside the ladder (config drift, tests) still renders as
    /// its own honest label instead of an empty selection.</summary>
    public GridSnapOption SelectedGridSnapOption
    {
        get => GridSnapOptions.FirstOrDefault(o => o.Divisor == GridDivisor)
               ?? GridSnapOption.For(GridDivisor);
        set
        {
            if (value is not null) GridDivisor = value.Divisor;
        }
    }

    partial void OnGridDivisorChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectedGridSnapOption));
    }
    [ObservableProperty] private ObservableCollection<VstPluginInfo> _availableVstPlugins = [];
    [ObservableProperty] private VstPluginInfo? _addGuitarVstSelection;

    [ObservableProperty] private double _highPassFreq = 20;
    [ObservableProperty] private double _highPassQ = 0.707;
    private double _inputVolume;
    [ObservableProperty] private bool _instrumentVstGuiOpen;
    // Mirrors IAudioPlaybackService.IsInstrumentVstLoading. UI uses this to
    // disable the "Open editor" toggle and show a "Loading…" hint while
    // big amp sims (BIAS FX, Ample Guitar, …) take 20+ seconds to scan
    // their sample libraries on first attach.
    [ObservableProperty] private bool _isInstrumentVstLoading;
    // True once a plugin handle is live and the editor toggle is meaningful.
    [ObservableProperty] private bool _isInstrumentVstReady;
    [ObservableProperty] private ObservableCollection<VstPluginInfo> _instrumentVstPlugins = [];
    [ObservableProperty] private bool _isInputMuted;
    [ObservableProperty] private bool _isInputSoloed;

    // Mute & Solo States
    [ObservableProperty] private bool _isMasterMuted;
    [ObservableProperty] private bool _isMidiMuted;
    [ObservableProperty] private bool _isMidiSoloed;
    [ObservableProperty] private bool _isPlaying;

    [ObservableProperty] private double _lowPassFreq = 20000;
    [ObservableProperty] private double _lowPassQ = 0.707;
    [ObservableProperty] private bool _metronomeEnabled;

    /// <summary>Runtime gate for the Play Mode gameplay metronome (persisted as
    /// <c>AppConfig.GameplayMetronomeEnabled</c>, armed by the Play Mode shell
    /// for the duration of a run). ORed with <see cref="MetronomeEnabled"/> in
    /// <c>AudioPlaybackCoordinator</c> so gameplay ticks without touching the
    /// editor's toolbar toggle. Not bound — read on the audio timer thread.</summary>
    public bool GameplayMetronomeActive { get; set; }

    /// <summary>Claps armed for a Play Mode run (a click on every charted note).
    /// Separate from the editor's <see cref="ClapsEnabled"/> toolbar toggle so a
    /// run cannot leave the editor's toggle flipped, and vice versa.</summary>
    public bool GameplayClapsActive { get; set; }

    [ObservableProperty] private bool _midiEnabled;

    [ObservableProperty] private ObservableCollection<string> _midiInstruments = [];
    private double _midiVolume;
    private double _midiOffsetMs;
    private double _monitorVolume;
    private int _inputPitchShiftSemis;
    private double _songVolume;
    [ObservableProperty] private bool _isSongMuted;
    [ObservableProperty] private bool _isSongSoloed;

    [ObservableProperty] private double _pan;
    [ObservableProperty] private double _pitchShift;
    private double _playbackSpeed;
    [ObservableProperty] private VstPluginInfo? _selectedInstrumentVst;
    [ObservableProperty] private int _selectedMidiInstrumentIndex;
    [ObservableProperty] private string? _selectedMidiInstrumentName;

    private double _volume;
    [ObservableProperty] private bool _vstEnabled;

    // Tracks whether the initial VST scan has finished restoring last-used
    // selections. Change notifications fire during restore; without this flag
    // we'd persist whatever "None" value the ComboBox had before matching up.
    private bool _vstRestoreComplete;
    private bool _hasEverLoadedInstrumentVst;
    private bool _suppressInstrumentEditorToggle;

    /// <summary>The editor's A/B loop + selection audition. Null in the
    /// headless/minimal view-model constructions (the same shape as
    /// <see cref="_ui" />/<see cref="_logger" /> here and the optional stores on
    /// <c>LivePlayViewModel</c>); every loop command reports
    /// <c>CanExecute == false</c> without it, so the toolbar control disables
    /// rather than doing nothing.</summary>
    private readonly DotCharter.Services.Editing.Playback.IEditorLoopService? _loop;

    public TransportViewModel(
        IAudioPlaybackService audioService,
        ProjectState projectState,
        IConfigurationService configService,
        DotCharter.Localization.ILocalizationService localization,
        IUiDispatcher? ui = null,
        IAppLogger? logger = null,
        DotCharter.Services.Editing.Playback.IEditorLoopService? loop = null,
        DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator? chartSynthInstruments = null)
    {
        _localization = localization;
        _audioService = audioService;
        _projectState = projectState;
        _configService = configService;
        _ui = ui ?? new Tone.UI.Platform.AvaloniaUiDispatcher();
        // Null coordinator in a test transport built without one: the pickers then
        // write the service directly and nothing is saved.
        ChartSynthInstrument = new Inspector.ChartSynthInstrumentViewModel(ChartSynth, chartSynthInstruments,
            _localization, _ui);
        ChartSynthInstrument.Refreshed += OnChartSynthInstrumentRefreshed;
        _logger = logger;
        _loop = loop;

        _volume = _audioService.Volume;
        _songVolume = _audioService.SongVolume;
        _inputVolume = _audioService.InputVolume;
        _midiVolume = _audioService.VstInstrumentVolume;
        _monitorVolume = _audioService.MonitorVolume;
        _playbackSpeed = _audioService.PlaybackSpeed;
        _midiOffsetMs = _audioService.MidiOffsetMs;

        // Drop-tune pedal: restore the persisted semitone shift and push it
        // into the engine (survives engine rebuilds via the service).
        // Seat 0 only: AppConfig carries one integer, and the higher seats are
        // session state rather than the install's (ABI v31 — see
        // InputPitchShiftSemis).
        _inputPitchShiftSemis = Math.Clamp(_configService.LoadAppConfig().InputPitchShiftSemis, -12, 0);
        _pitchShiftSemisBySeat[0] = _inputPitchShiftSemis;
        if (_inputPitchShiftSemis != 0)
            _audioService.InputPitchShiftSemitones = _inputPitchShiftSemis;

        // The inspector's six-band EQ (ABI v29). Constructed here rather than in
        // the view so it survives a float/redock of the Mixer tab — an inspector
        // VIEW is thrown away and rebuilt on every one of those, and an EQ that
        // reset each time would be a control that lies about being persistent.
        Eq = new Inspector.EqModuleViewModel(_audioService, _localization);

        var noneVst = new VstPluginInfo { Name = "None", PluginPath = "" };
        AvailableVstPlugins.Add(noneVst);
        InstrumentVstPlugins.Add(noneVst);
        SelectedInstrumentVst = InstrumentVstPlugins[0];

        foreach (var patch in GmInstruments) MidiInstruments.Add(patch);
        SelectedMidiInstrumentIndex = 27;
        SelectedMidiInstrumentName = GmInstruments[27];

        // Fire-and-forget, so the body MUST NOT throw: an unobserved exception here
        // (a stale VST3 folder ⇒ UnauthorizedAccessException / DirectoryNotFoundException)
        // would leave the plugin dropdowns empty and the saved chain unrestored
        // for the whole session, with zero diagnostics.
        VstStartupTask = Task.Run(() =>
        {
            try
            {
                ScanAndRestoreVstSelections();
            }
            catch (Exception ex)
            {
                // Warning (not Error): the app is fully usable without VSTs. It
                // still surfaces in the status bar's message list.
                _logger?.Warning(AppLogCategory.Audio,
                    "Scanning VST plugins / restoring the saved chain failed; the plugin lists stay empty " +
                    "for this session. Check the VST3 folders in Settings → Audio.", ex);
            }
        });

        // React to native-side VST load completion so the UI can disable
        // the editor / FX-chain controls until the plugin is actually
        // ready, and re-enable them the moment it attaches.
        _audioService.InstrumentVstLoadChanged += OnAudioInstrumentVstLoadChanged;
        _audioService.GuitarVstSlotLoadChanged += OnAudioGuitarVstSlotLoadChanged;
        // Native X-button closes must clear the bound editor toggles, or the
        // next click toggles the wrong way ("editor won't reopen").
        _audioService.GuitarVstSlotEditorClosed += OnAudioGuitarVstSlotEditorClosed;
        _audioService.InstrumentVstEditorClosed += OnAudioInstrumentVstEditorClosed;

        // An engine (re)build — startup's lazy open, a device change, a failed
        // rebuild — moves whether the chart can sound at all.
        ChartSynth?.ChartSynthAvailabilityChanged += (_, _) => _ui.Post(RefreshChartSynthAvailability);

        UpdateEqCurve();
    }

    /// <summary>Test seam: the startup VST scan/restore worker. Never faults (the
    /// body catches + logs); production code fires and forgets it.</summary>
    internal Task VstStartupTask { get; }

    /// <summary>The startup VST worker (background thread): scan, push the persisted
    /// selections into the engine, then hand the plugin lists to the UI thread.</summary>
    private void ScanAndRestoreVstSelections()
    {
        var plugins = _audioService.ScanVstPlugins();
        // VST3-only host. VST2 plugins are not scanned
        // or hosted; the chain and instrument dropdowns accept .vst3 only.
        //
        // We deliberately do NOT probe each plugin to determine whether it
        // is an instrument vs effect: that would require instantiating the
        // plugin on a thread-pool (MTA) worker, which corrupts COM
        // apartment state for plugins like BIAS FX and Helix Native and
        // makes their editor open as a frozen white window when later
        // loaded on the proper STA thread. Instrument-only filtering is
        // tracked separately and will be implemented via STA-serialised
        // probes with a persistent cache.

        // Push the persisted selections to the audio service straight from
        // this worker. The service queues both requests while the engine is
        // still being built (ReplaceEngine drains them the moment it is
        // up), so the multi-second native plugin loads start as early as
        // possible instead of waiting for the UI thread to finish
        // inflating MainWindow and run the restore continuation below.
        var (savedChain, savedInstrumentPath) = LoadPersistedVstSelections();
        // Only a path the scanner reported is handed to the engine. The chain comes out
        // of a settings file, and "the path exists" is not a reason to load native code
        // from it: a restored backup or a copied config could name a plug-in anywhere.
        // The rest stay in the chain as slots that say they are not loaded.
        var loadable = savedChain.Where(e => IsScannedPlugin(plugins, e.PluginPath)).ToList();
        if (loadable.Count > 0)
            _audioService.SetGuitarVstChain(loadable);
        var instrumentKnown = savedInstrumentPath != null && plugins.Any(p =>
            string.Equals(p.PluginPath, savedInstrumentPath, StringComparison.OrdinalIgnoreCase));
        if (instrumentKnown)
            _audioService.LoadInstrumentVst(savedInstrumentPath!, openEditorAfterLoad: false);

        _ = _ui.InvokeAsync(() =>
        {
            foreach (var p in plugins)
            {
                if (string.IsNullOrEmpty(p.PluginPath))
                    continue;

                var extension = Path.GetExtension(p.PluginPath);
                var isVst3 = string.Equals(extension, ".vst3", StringComparison.OrdinalIgnoreCase);
                if (!isVst3) continue;

                AvailableVstPlugins.Add(p);
                InstrumentVstPlugins.Add(p);
            }

            RestoreLastVstSelections(savedChain, loadable, instrumentKnown ? savedInstrumentPath : null);
            _vstRestoreComplete = true;
        });
    }

    private static bool IsScannedPlugin(IReadOnlyList<VstPluginInfo> scanned, string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           scanned.Any(p => string.Equals(p.PluginPath, path, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedMidiInstrumentIndexChanged(int value)
    {
        if (value >= 0 && value < 128) _audioService.SetMidiInstrument(value);
    }

    partial void OnSelectedMidiInstrumentNameChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var index = Array.IndexOf(GmInstruments, value);
        if (index >= 0)
        {
            SelectedMidiInstrumentIndex = index;
            _audioService.SetMidiInstrument(index);
        }
    }

    /// <summary>
    /// Compensates for the MIDI synth's output latency: a positive value
    /// fires NoteOns this many milliseconds ahead of the audio playhead so
    /// the synth's delayed sound lines up with the song. Persisted on the
    /// app config so the user only has to dial it in once per device.
    /// Surfaced from the mixer so users can adjust it without diving into
    /// the audio settings tab.
    /// </summary>
    public double MidiOffsetMs
    {
        get => _midiOffsetMs;
        set
        {
            var clamped = Math.Clamp(value, -500.0, 500.0);
            if (Math.Abs(clamped - _midiOffsetMs) < 0.001) return;
            _midiOffsetMs = clamped;
            _audioService.MidiOffsetMs = clamped;
            PersistMidiOffset(clamped);
            OnPropertyChanged();
        }
    }

    private void PersistMidiOffset(double value)
    {
        var cfg = _configService.LoadAppConfig();
        if (Math.Abs(cfg.MidiDelayMs - value) < 0.001) return;
        cfg.MidiDelayMs = value;
        _configService.SaveAppConfig(cfg);
    }

    public void SeekTo(double timeMs) => _audioService.SeekTo(timeMs);

    /// <summary>Plays one metronome click immediately (used by the gameplay
    /// count-in pre-roll). <paramref name="accent"/> marks a measure downbeat.
    /// Reuses the native click via the same path as the playback metronome.</summary>
    public void PlayMetronomeClick(bool accent) => _audioService.PlayMetronomeTick(accent);

    [RelayCommand]
    private void Play()
    {
        if (_projectState.TotalAudioLengthMs <= 0) return;
        _audioService.Play();
        IsPlaying = true;
        // Starting playback re-locks the decoupled viewport onto the
        // playhead (0.16 6.6) — Play means "watch it play".
        _projectState.TimelineFollowsPlayhead = true;
    }

    [RelayCommand]
    private void Pause()
    {
        _audioService.Pause();
        IsPlaying = false;
    }

    void IEditorTransport.Pause() => PauseCommand.Execute(null);

    [RelayCommand]
    private void Stop()
    {
        _audioService.Stop();
        IsPlaying = false;
        _projectState.CurrentTimeMs = 0;
        // An audition is a ONE-SHOT pass that disarms itself when it REACHES its
        // end. Stopping mid-pass means it never will, and the flag left behind
        // turned the next ordinary Play into a loop that ran to the audition's
        // end and paused — with nothing on screen saying why.
        _loop?.EndAudition();
    }

    /// <summary>
    ///     ONE play/pause door, and it asks the ENGINE which way it goes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The keybind and the command palette must not branch on
    ///         <see cref="IsPlaying" /> — this view model's own cached copy — and
    ///         write the other half. That copy is only ever written by the three
    ///         commands above, so it does not survive the engine stopping ITSELF:
    ///         <c>JuceAudioPlaybackService</c>'s position tick pauses the
    ///         transport when the playhead reaches the end of the audio, and
    ///         nothing tells this view model. The flag then still said "playing",
    ///         so the first Space after a song ended executed <c>Pause</c> — into
    ///         an already-paused engine — and the user had to press it twice.
    ///     </para>
    ///     <para>
    ///         Reading <see cref="IPlaybackTransport.IsPlaying" /> makes the
    ///         decision from the only state that cannot go stale, and re-syncs
    ///         the bound flag on the way past. Same reason
    ///         <see cref="SyncPlayingStateFromEngine" /> exists for the playback
    ///         publish.
    ///     </para>
    /// </remarks>
    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_audioService.IsPlaying) PauseCommand.Execute(null);
        else PlayCommand.Execute(null);
    }

    /// <summary>
    ///     Mirrors the engine's transport state onto the bound
    ///     <see cref="IsPlaying" /> flag. Call from the UI thread only — it is a
    ///     bound property.
    /// </summary>
    /// <remarks>
    ///     A stale <c>true</c> is not only a wrong glyph: <c>ChartRenderer</c> and
    ///     <c>Preview3DRenderDriver</c> both gate their CONTINUOUS render loop on
    ///     <c>audioService.IsPlaying || Transport.IsPlaying</c>, so a song that
    ///     ran to its end left both surfaces redrawing every frame for the rest of
    ///     the session — until the user pressed play/pause again.
    /// </remarks>
    public void SyncPlayingStateFromEngine()
    {
        var playing = _audioService.IsPlaying;
        if (IsPlaying != playing) IsPlaying = playing;
    }

    // === The editor loop region (v0.35, editing rework stage 3) ==============
    //
    // These MARK a position the user picked, so they read the LATENCY-COMPENSATED
    // ProjectState.CurrentTimeMs — the playhead that is drawn and heard. That is
    // the other side of the audio rule: never SCHEDULE against the compensated
    // playhead (the wrap itself aims at RawPlayheadMs, see AudioPlaybackCoordinator),
    // but a point the user is pointing at IS the compensated one, and marking on
    // the raw axis would land the punch one output latency past the playhead they
    // were looking at.

    private bool CanUseLoop => _loop != null;

    /// <summary>Punch-in at the playhead.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLoop))]
    private void SetLoopStart() => _loop?.SetStart(_projectState.CurrentTimeMs);

    /// <summary>Punch-out at the playhead; the loop turns on. Deliberately does
    /// NOT seek — an editor loop takes effect on the next pass instead of
    /// yanking a paused playhead away from what you were looking at.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLoop))]
    private void SetLoopEnd() => _loop?.SetEnd(_projectState.CurrentTimeMs);

    /// <summary>Loop on/off, keeping the punched region.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLoop))]
    private void ToggleLoop() => _loop?.Toggle();

    [RelayCommand(CanExecute = nameof(CanUseLoop))]
    private void ClearLoop() => _loop?.Clear();

    /// <summary>
    ///     Plays <paramref name="startMs" />–<paramref name="endMs" /> once through the same one-pass loop as
    /// <see cref="AuditionSelection" /> — the AI tone review's Audition, whose span is a tone's song
    ///     window rather than a selection. False (and nothing armed) when there is no loop service, the span
    ///     is too short, or there is no transport to play.
    /// </summary>
    public bool AuditionSpan(double startMs, double endMs)
    {
        if (_loop is null || !_loop.BeginAudition(startMs, endMs)) return false;
        SeekTo(startMs);
        _projectState.CurrentTimeMs = startMs;
        if (_audioService.IsPlaying) return true;

        PlayCommand.Execute(null);
        if (_audioService.IsPlaying) return true;
        _loop.EndAudition();
        return false;
    }

    /// <summary>
    ///     Plays the resolved edit target once — the per-selection audition,
    ///     heard through whatever sounding is on (the song, the metronome/claps,
    ///     and above all the existing MIDI/VSTi chart preview, which is what
    ///     makes it work on a chart whose audio is silent or muted). The pass
    ///     ends by seeking back to the start and pausing, so pressing the key
    ///     again replays the same span.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseLoop))]
    private void AuditionSelection()
    {
        if (_loop?.BeginAuditionOfTarget() is not { } seekTo) return;
        SeekTo(seekTo);
        _projectState.CurrentTimeMs = seekTo;
        if (_audioService.IsPlaying) return;

        PlayCommand.Execute(null);
        // Play REFUSES when there is no transport to run at all
        // (TotalAudioLengthMs <= 0 — a chart-only project whose silent-transport
        // fallback did not load). The audition was already armed by the line
        // above, and a one-shot only ends when its pass COMPLETES, so leaving it
        // armed handed the next real playback — after the user attached audio —
        // a loop nobody punched.
        if (!_audioService.IsPlaying) _loop.EndAudition();
    }
}
