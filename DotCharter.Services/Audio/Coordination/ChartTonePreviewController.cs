using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.State;
using DotCharter.Localization;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     The mixer facts the chart editor's tone preview has to be able to read and
///     put back. Implemented by <c>TransportViewModel</c>; an interface so the
///     preview's audibility rule is testable without a transport, a view or an
///     engine.
///     <para>
///         It is deliberately the four things that can silence a guitar that is
///         otherwise routed correctly — the mute, the solo matrix, the input gain
///         and the monitor gain. Everything else about the mix is the charter's.
///     </para>
/// </summary>
public interface IGuitarMonitorMixer
{
    bool IsInputMuted { get; set; }
    bool IsInputSoloed { get; set; }

    /// <summary>True when ANY channel is soloed — in which case a channel that is
    /// not soloed is silent, mute flag or no mute flag.</summary>
    bool IsAnythingSoloed { get; }

    double InputVolume { get; set; }
    double MonitorVolume { get; set; }
}

/// <summary>
///     "Preview tones" — the chart editor's guitar-input tone preview.
///
///     <para>
///         While it is on, the live guitar is heard through the tone the PLAYHEAD
///         sits in, and the rig is rebuilt as the playhead crosses each
///         <c>ToneChange</c> — so a tone switch is audible while charting it,
///         which is the whole point. It is Play Mode's in-game-tone path with a
///         different tone SOURCE: the open project's own authored
///         <see cref="ChartProject.ToneDefinitions" /> rather than a package's,
///         driven through the SAME
///         <see cref="RsTonePlaybackCoordinator" /> — one manual-chain snapshot,
///         one owner of the rig (<see cref="RsToneDriver" />).
///     </para>
///
///     <para>
///         <b>It never lies about being on.</b> When there is no project, no
///         native DSP rig (a build whose <c>dca_engine_dsp_*</c> entry points are
///         missing resolves <c>NullDspChainService</c>, which hosts no rig at
///         all), or the arrangement references no tone this project defines, the
///         toggle is UNAVAILABLE and <see cref="Tip" /> is the reason — never a
///         live-looking control over nothing.
///     </para>
///
///     <para>
///         <b>Audibility.</b> Monitoring is not something this feature turns on:
///         the engine has monitored the live input since it opened (the service's
///         monitor gain defaults to 1.0 and is replayed on every engine rebuild),
///         which is exactly why Play Mode needs no monitoring switch either. What
///         this DOES do is refuse to be a toggle that produces silence: if the
///         guitar is muted, un-soloed against another solo, or at zero gain, the
///         preview raises just those to unity for its duration and puts back
///         exactly what it changed — and only if the charter has not moved that
///         control themselves in the meantime.
///     </para>
/// </summary>
public sealed partial class ChartTonePreviewController : ObservableObject, IDisposable
{
    /// <summary>Below this a fader is "off" for the purposes of "can the charter
    /// actually hear this". Matches the mixer's own 0.05 snap step.</summary>
    public const double AudibleFloor = 0.05;

    private readonly RsTonePlaybackCoordinator _tones;
    private readonly ProjectState _projectState;
    private readonly IGuitarMonitorMixer? _mixer;
    private readonly IAppLogger? _logger;
    private readonly IHistoryService? _history;

    // The words of the toolbar tip.
    private readonly ILocalizationService _localization;

    private readonly PropertyChangedEventHandler _onProjectStateChanged;
    private readonly NotifyCollectionChangedEventHandler _onDefinitionsChanged;
    private readonly NotifyCollectionChangedEventHandler _onToneChangesChanged;
    private readonly Action _onHistoryChanged;

    private ObservableCollection<ToneDefinition>? _watchedDefinitions;
    private ObservableCollection<ToneChange>? _watchedChanges;
    private MonitorMixRestore? _restore;
    private bool _disposed;

    // The rig is driven for TWO reasons: the toolbar toggle (IsOn, which
    // also makes the live guitar audible) and the built-in chart synth, which
    // always plays through the chart's own tones. ONE coordinator preview serves
    // both; _driving says this controller holds it, and it is released only when
    // neither reason wants it any more.
    private bool _driving;
    private bool _synthWantsTones;

    // The synth's two inputs: whether it plays, and whether the player chose
    // their own rig for it. It wants the chart's tones only when it plays AND has
    // not chosen its own rig.
    private bool _synthPlaying;
    private bool _synthOwnRig;

    // Outside the chart editor the rig belongs to Play Mode: the synth's wish is
    // kept but not acted on until ResumeInTheEditor.
    private bool _outsideEditor;

    // The Tone designer's unsaved bodies, by tone key. Converted in place of
    // the committed ones while they last.
    private Dictionary<string, string>? _bodyEdits;

    public ChartTonePreviewController(
        RsTonePlaybackCoordinator tones,
        ProjectState projectState,
        ILocalizationService localization,
        IGuitarMonitorMixer? mixer = null,
        IAppLogger? logger = null,
        IHistoryService? history = null,
        EditorRocksmithTonePreview? rocksmith = null)
    {
        Rocksmith = rocksmith;
        _tones = tones;
        _localization = localization;
        _projectState = projectState;
        _mixer = mixer;
        _logger = logger;
        _history = history;

        _onProjectStateChanged = OnProjectStatePropertyChanged;
        // The two collections need DIFFERENT work: a tone BODY changed means
        // re-convert, a tone CHANGE means only the ms timeline moved.
        _onDefinitionsChanged = (_, _) =>
        {
            // A body committed (or a tone added / removed): the designer's live
            // edit is in the project now, or gone with it.
            _bodyEdits = null;
            Refresh();
        };
        _onToneChangesChanged = (_, _) => RefreshSchedule();
        _onHistoryChanged = RefreshSchedule;

        _projectState.PropertyChanged += _onProjectStateChanged;
        _history?.HistoryChanged += _onHistoryChanged;

        Refresh();
    }

    /// <summary>The toolbar toggle: the live guitar is heard through the tone at the
    /// playhead. The rig can also be driven without it, for the chart synth
    /// (<see cref="IsDriving" />).</summary>
    [ObservableProperty] private bool _isOn;

    /// <summary>The "Preview in: DotCharter rig / Rocksmith" choice (owned and disposed here); null without a tonelab session.</summary>
    public EditorRocksmithTonePreview? Rocksmith { get; }

    /// <summary>The chart's tones are on the rig — for the toggle, the chart synth
    /// or both.</summary>
    public bool IsDriving => _driving;

    /// <summary>
    /// The built-in chart synth started or stopped. While it plays, the
    ///     chart's own tones drive the rig whether or not the toggle is on — the
    ///     synth is there to hear the chart as it will sound. A project with no tone
    ///     this preview can convert (no tones, or tones without a body) leaves the
    ///     charter's rig exactly as it is: that is <see cref="IsAvailable" /> false.
    /// </summary>
    public void SetChartSynthPlaying(bool playing)
    {
        if (_disposed) return;
        _synthPlaying = playing;
        UpdateSynthWish();
    }

    /// <summary>
    /// The synth's tone source: true plays it through the player's own rig
    ///     — the chart's tones are left alone, and a rig they hold only for the synth
    /// is handed back; false (the default) is the chart's tones. The
    ///     toolbar toggle is not affected: while IT is on, the rig follows the chart's
    ///     tones for the live guitar, and the synth shares the rig.
    /// </summary>
    public void SetChartSynthUsesOwnRig(bool ownRig)
    {
        if (_disposed) return;
        _synthOwnRig = ownRig;
        UpdateSynthWish();
    }

    /// <summary>The synth's tone source as last set.</summary>
    public bool ChartSynthUsesOwnRig => _synthOwnRig;

    private void UpdateSynthWish()
    {
        var wants = _synthPlaying && !_synthOwnRig;
        if (_synthWantsTones == wants) return;
        _synthWantsTones = wants;
        if (!wants)
        {
            ReleaseIfUnwanted();
            return;
        }

        if (!_outsideEditor && IsAvailable && EnsureDriving())
            _logger?.Info(AppLogCategory.Audio, "Tone preview: the chart synth plays through the chart's tones.");
    }

    /// <summary>
    ///     Leaving the chart editor: give the rig back (Play Mode takes it for its own
    ///     run) and switch the toggle off. What the SYNTH wants is kept, so
    ///     <see cref="ResumeInTheEditor" /> picks the chart's tones up again.
    /// </summary>
    public void StopOutsideTheEditor()
    {
        if (_disposed) return;
        _outsideEditor = true;
        if (IsOn)
        {
            IsOn = false;
            RestoreMonitorMix();
        }

        if (!_driving) return;
        _driving = false;
        _tones.EndPreview();
    }

    /// <summary>Back in the chart editor: a synth that is still on wants the chart's
    /// tones again.</summary>
    public void ResumeInTheEditor()
    {
        if (_disposed) return;
        _outsideEditor = false;
        _ = Rocksmith?.ProbeAsync();
        if (_synthWantsTones && IsAvailable) EnsureDriving();
    }

    /// <summary>
    /// The Tone designer changed the body of tone <paramref name="key" />:
    ///     while the chart's tones drive the rig, the edit is converted and the tone
    ///     at the playhead re-applied at once — the Volume and every knob are heard as
    ///     they move. A tone not under the playhead is ready when the playhead gets there.
    /// </summary>
    public void PreviewToneEdit(string key, string json)
    {
        if (_disposed || string.IsNullOrEmpty(key)) return;
        (_bodyEdits ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = json;
        // Converted even while nothing drives, so a preview or synth started next
        // begins from the edited body.
        _tones.PrepareForProject(_projectState.ActiveProject, _projectState.ActiveArrangement, _bodyEdits);
        Rocksmith?.SetProjectTones(EditorRocksmithTonePreview.ParseTones(_projectState.ActiveProject, _bodyEdits));
        if (_driving) _tones.RefreshPreviewSchedule(reapplyCurrentTone: true);
    }

    /// <summary>The designer closed: back to the committed bodies.</summary>
    public void EndToneEditPreview()
    {
        if (_disposed || _bodyEdits is null) return;
        _bodyEdits = null;
        Refresh();
    }

    private bool EnsureDriving()
    {
        if (_driving) return true;
        if (!_tones.BeginPreview()) return false;
        _driving = true;
        Rocksmith?.PreviewStarted();
        return true;
    }

    private void ReleaseIfUnwanted()
    {
        if (!_driving || IsOn || _synthWantsTones) return;
        _driving = false;
        _tones.EndPreview();
        _logger?.Info(AppLogCategory.Audio, "Tone preview: OFF — the charter's own rig is restored.");
    }

    /// <summary>False ⇒ the toggle is disabled and <see cref="Tip" /> says why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isAvailable;

    /// <summary>The resx key behind <see cref="Tip" /> while unavailable; empty
    /// while the preview can run. Exposed for tests and diagnostics — the UI binds
    /// <see cref="Tip" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private string _unavailableReasonKey = "";

    /// <summary>What the toolbar button's tooltip shows: how the preview works
    /// when it can run, and WHY NOT when it cannot.</summary>
    public string Tip => _localization[TipKey];

    private string TipKey => IsAvailable || UnavailableReasonKey.Length == 0 ? "Toolbar_TonePreview_Tip" : UnavailableReasonKey;

    /// <summary>
    ///     Re-read the open project: convert its tones, re-answer availability
    ///     and, while the preview is on, rebuild the schedule against the current
    ///     arrangement (an arrangement switch changes both the tone slots and the
    ///     tone changes under it).
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;

        var project = _projectState.ActiveProject;
        var arrangement = _projectState.ActiveArrangement;
        WatchProject(project, arrangement);

        var summary = _tones.PrepareForProject(project, arrangement, _bodyEdits);
        Rocksmith?.SetProjectTones(EditorRocksmithTonePreview.ParseTones(project, _bodyEdits));
        var reason = ResolveReason(project, arrangement, summary);
        UnavailableReasonKey = reason;
        IsAvailable = reason.Length == 0;

        if (!_driving)
        {
            // The synth asked for the chart's tones before there were any to play
            // (a project opened, its tones just authored): start now.
            if (_synthWantsTones && IsAvailable && !_outsideEditor) EnsureDriving();
            return;
        }

        if (!IsAvailable)
        {
            // Nothing convertible left: the charter's own rig comes back, for the
            // toggle and the synth alike.
            _logger?.Info(AppLogCategory.Audio,
                $"Tone preview: switching off — {reason}.");
            if (IsOn)
            {
                IsOn = false;
                RestoreMonitorMix();
            }

            _driving = false;
            _tones.EndPreview();
            return;
        }

        if (!_tones.IsPreviewing)
        {
            // Something else took the rig (a Play Mode run). "Never lies about
            // being on" cuts both ways: a toggle left lit over a rig it no
            // longer drives is the same lie as a live-looking disabled one.
            _logger?.Info(AppLogCategory.Audio,
                "Tone preview: the rig has another owner now; switching off.");
            _driving = false;
            if (!IsOn) return;
            IsOn = false;
            RestoreMonitorMix();
            return;
        }

        // Still driving, but the schedule and the converted bodies may describe
        // the arrangement we just left. Re-arm IN PLACE — a stop + start would
        // restore the charter's board and rebuild the tone rig on top of it for
        // every project, arrangement or tone edit, which is precisely the
        // teardown DcRigApplier exists to avoid.
        _tones.RefreshPreviewSchedule(reapplyCurrentTone: true);
    }

    /// <summary>
    ///     Re-arm against the tone TIMELINE as it stands now: a tone change
    ///     placed, deleted, DRAGGED, or shifted by a tempo-map edit.
    ///     <para>
    ///         This is the cheap half of <see cref="Refresh" /> — it rebuilds the
    ///         ms-resolved schedule and nothing else, applying only when the tone
    ///         under the playhead actually changed. That is what makes it safe to
    ///         hang off <see cref="IHistoryService.HistoryChanged" />, which fires
    ///         on every undoable gesture and is the only signal a MOVED marker
    ///         raises at all: a marker's position is set on the object, so the
    ///         collection watcher never sees it. Until this existed, dragging a
    ///         tone change — the gesture the whole feature exists to make audible
    ///         — needed the toggle switched off and back on before it was heard.
    ///     </para>
    /// </summary>
    public void RefreshSchedule()
    {
        if (_disposed || !_driving) return;
        _tones.RefreshPreviewSchedule();
    }

    /// <summary>Flip the preview. Returns the state it ended in.</summary>
    public bool Toggle()
    {
        if (IsOn)
        {
            TurnOff();
            return false;
        }

        return TurnOn();
    }

    /// <summary>Start previewing. False when it could not (and the reason is
    /// logged and on <see cref="Tip" />) — never silently "on".</summary>
    public bool TurnOn()
    {
        if (IsOn) return true;
        // A game started since the editor opened becomes choosable (off the UI thread).
        _ = Rocksmith?.ProbeAsync();
        if (!IsAvailable)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Tone preview: refused — {(UnavailableReasonKey.Length == 0 ? "unavailable" : UnavailableReasonKey)}.");
            return false;
        }

        if (!EnsureDriving())
        {
            // The coordinator has the last word (a Play Mode run owns the rig, a
            // build with no DSP entry points). Re-answer availability from it
            // rather than leaving a toggle that claims to be usable.
            Refresh();
            return false;
        }

        MakeGuitarAudible();
        IsOn = true;
        _logger?.Info(AppLogCategory.Audio, "Tone preview: ON — the guitar is running through the chart's tone.");
        return true;
    }

    /// <summary>Stop previewing and put the charter's own board — and whatever the
    /// preview changed in the mix — back. Idempotent.</summary>
    public void TurnOff()
    {
        if (!IsOn) return;
        IsOn = false;
        RestoreMonitorMix();
        if (_synthWantsTones)
        {
            // The rig stays on the chart's tones for the synth; only the
            // guitar's audibility is handed back.
            _logger?.Info(AppLogCategory.Audio, "Tone preview: toggle off; the chart synth keeps the chart's tones.");
            return;
        }

        ReleaseIfUnwanted();
    }

    // ------------------------------------------------------------ availability

    private static string ResolveReason(ChartProject? project, Arrangement? arrangement, RsToneConversionSummary summary)
    {
        if (project is null || arrangement is null) return "Toolbar_TonePreview_NoProject";
        if (!summary.DspAvailable) return "Toolbar_TonePreview_NoRig";
        if (!summary.HasTones) return "Toolbar_TonePreview_NoTones";
        return "";
    }

    private void OnProjectStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ProjectState publishes the playhead through this same channel at up to
        // 60 Hz — everything here is behind two ordinal name compares.
        if (string.Equals(e.PropertyName, nameof(ProjectState.ActiveProject), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ProjectState.ActiveArrangement), StringComparison.Ordinal))
        {
            Refresh();
        }
    }

    /// <summary>
    ///     Follow the two collections that decide whether a tone preview has
    ///     anything to play: the project's tone BODIES and the arrangement's tone
    ///     CHANGES. Placing a tone change is the authoring gesture this feature
    ///     exists for, so it must not need an off/on to be heard.
    ///     <para>
    ///         They get DIFFERENT handlers because they cost different things: a
    ///         body edit means re-converting tone JSON (<see cref="Refresh" />), a
    ///         marker added or deleted means only that the ms timeline moved
    ///         (<see cref="RefreshSchedule" />).
    ///     </para>
    ///     <para>
    ///         Neither covers a marker that MOVED — its position is set on the
    ///         object, not on the collection — nor a tempo edit under one. Those
    ///         arrive through <see cref="IHistoryService.HistoryChanged" />
    ///         instead; watching every marker would mean a per-item subscription
    ///         on a collection the charter edits constantly.
    ///     </para>
    /// </summary>
    private void WatchProject(ChartProject? project, Arrangement? arrangement)
    {
        var definitions = project?.ToneDefinitions;
        if (!ReferenceEquals(definitions, _watchedDefinitions))
        {
            _watchedDefinitions?.CollectionChanged -= _onDefinitionsChanged;
            _watchedDefinitions = definitions;
            _watchedDefinitions?.CollectionChanged += _onDefinitionsChanged;
        }

        var changes = arrangement?.ToneChanges;
        if (ReferenceEquals(changes, _watchedChanges)) return;

        _watchedChanges?.CollectionChanged -= _onToneChangesChanged;
        _watchedChanges = changes;
        _watchedChanges?.CollectionChanged += _onToneChangesChanged;
    }

    // -------------------------------------------------------------- audibility

    /// <summary>What the preview changed in the mix so it could be heard, and what
    /// it wrote — so a control the charter has since moved is left alone.</summary>
    private sealed class MonitorMixRestore
    {
        public bool Unmuted;
        public bool Soloed;
        public double? InputVolumeWas;
        public double InputVolumeWrote;
        public double? MonitorVolumeWas;
        public double MonitorVolumeWrote;
    }

    private void MakeGuitarAudible()
    {
        _restore = null;
        if (_mixer is null) return;

        var restore = new MonitorMixRestore();

        // Read the solo matrix BEFORE touching anything: adding the input to an
        // existing solo group is purely additive (whatever was soloed stays
        // soloed and audible), which is why this is safe where clearing someone
        // else's solo would not be.
        var otherSoloed = _mixer.IsAnythingSoloed && !_mixer.IsInputSoloed;

        if (_mixer.IsInputMuted)
        {
            restore.Unmuted = true;
            _mixer.IsInputMuted = false;
        }

        if (otherSoloed)
        {
            restore.Soloed = true;
            _mixer.IsInputSoloed = true;
        }

        if (_mixer.InputVolume < AudibleFloor)
        {
            restore.InputVolumeWas = _mixer.InputVolume;
            _mixer.InputVolume = 1.0;
            restore.InputVolumeWrote = _mixer.InputVolume;
        }

        if (_mixer.MonitorVolume < AudibleFloor)
        {
            restore.MonitorVolumeWas = _mixer.MonitorVolume;
            _mixer.MonitorVolume = 1.0;
            restore.MonitorVolumeWrote = _mixer.MonitorVolume;
        }

        _restore = restore;
    }

    private void RestoreMonitorMix()
    {
        var restore = _restore;
        _restore = null;
        if (_mixer is null || restore is null) return;

        if (restore.Unmuted && !_mixer.IsInputMuted) _mixer.IsInputMuted = true;
        if (restore.Soloed && _mixer.IsInputSoloed) _mixer.IsInputSoloed = false;

        if (restore.InputVolumeWas is { } inputWas &&
            Math.Abs(_mixer.InputVolume - restore.InputVolumeWrote) < 1e-6)
        {
            _mixer.InputVolume = inputWas;
        }

        if (restore.MonitorVolumeWas is { } monitorWas &&
            Math.Abs(_mixer.MonitorVolume - restore.MonitorVolumeWrote) < 1e-6)
        {
            _mixer.MonitorVolume = monitorWas;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Disposed at SHUTDOWN, after the audio engine: abandon the preview
        // rather than TurnOff, whose restore would rebuild the board into chains
        // that are already detached. The monitor mix is plain managed state.
        if (_driving)
        {
            _driving = false;
            _tones.AbandonPreview();
        }

        if (IsOn)
        {
            IsOn = false;
            RestoreMonitorMix();
        }

        // After the abandon above: its DrivingStopped already asked the game to restore.
        Rocksmith?.Dispose();
        _projectState.PropertyChanged -= _onProjectStateChanged;
        _history?.HistoryChanged -= _onHistoryChanged;
        _watchedDefinitions?.CollectionChanged -= _onDefinitionsChanged;
        _watchedChanges?.CollectionChanged -= _onToneChangesChanged;
        _watchedDefinitions = null;
        _watchedChanges = null;
    }
}
