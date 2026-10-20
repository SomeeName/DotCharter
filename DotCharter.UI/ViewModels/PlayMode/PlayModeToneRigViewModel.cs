using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Tone.UI.ViewModels.Settings;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode's Amp &amp; FX rig overlay (§6.8) — the app's ONE tone-rig board,
///     exposed as <c>PlayModeViewModel.ToneRig</c>: the overlay's open state, the
///     shared chain view model, the gated rig SCREEN over it, and the AI tone
///     panel's host half ("Match from audio", "Match by section", "Apply as tone
///     set"). The chart editor's twin is <see cref="ToneRigViewModel" />.
///     <para>
///         <b>Why it stays an OVERLAY and did not become a
///         <see cref="PlayModeScreen" /> value.</b> The rig opens from three
///         places — the front door's FREE PLAY, the Get Ready footer and the
///         pause menu — and two of those are STATES, not screens you can return
///         to by name: leaving <see cref="PlayModeScreen.Paused" /> for a rig
///         screen and back would have to reconstruct the pause. An overlay keeps
///         all three entry points, the Esc walk-up and the existing prewarm
///         exactly as they are.
///     </para>
///     <para>
///         <b>One rig, one skin, one door.</b> §7 left "flat · textured · user
///         preference" open and this screen shipped BOTH — the Fire board and
///         the classic rack behind a FLAT/RACK switch. The owner read that as
///         the app still having two pedalboards, which is fair: a switch that
///         redraws the same chain in a different idiom is indistinguishable
///         from a second surface, and it doubled the maintenance of every rig
///         feature. The switch and the rack are gone.
///     </para>
///     <para>
///         <b>Everything that reaches the tone rig or the AI lane is a factory,
///         not an instance.</b> Play Mode is built with the shell, before the
///         window exists, and nothing on that path may construct the DSP chain,
///         the per-screen rig parts or the matchers. Each factory runs on first
///         use: the overlay opening, the prewarm asking for the rig screen, or
///         the AI panel pressing a button.
///     </para>
/// </summary>
public sealed partial class PlayModeToneRigViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly TransportViewModel? _transport;
    private readonly LivePlayViewModel? _livePlay;
    private readonly IAudioPlaybackService? _audio;
    private readonly IHistoryService? _history;
    private readonly Func<PedalboardViewModel?> _pedalboardFactory;
    private readonly Func<INamCaptureLibrary?> _namCaptures;
    private readonly Func<LiveInputChannelViewModel?> _inputChannel;
    private readonly Func<RigReampViewModel?> _reamp;
    private readonly Func<IToneMatcher?> _toneMatcher;
    private readonly Func<IStemIsolator?> _stemIsolator;
    private readonly Func<IRigEstimator?> _rigEstimator;
    private readonly Func<IRigEstimatorModelDownloadService?> _rigEstimatorModelDownloads;
    private readonly Func<IToneSectionProposer?> _toneSectionProposer;
    private readonly Func<IToneSetMatcher?> _toneSetMatcher;
    private readonly Func<IToneNarrator?> _toneNarrator;
    private readonly DeferredRigScreen _rigScreen = new();
    private PedalboardViewModel? _pedalboard;

    /// <summary>
    ///     Same centered, scrimmed overlay pattern as Amp/FX. The pedalboard drives
    ///     the native DSP chain (<c>dca_engine_dsp_*</c>) through
    ///     <c>IDspChainService</c>; the chain view model comes from its factory on
    ///     first use, so Play Mode still works on a build without the tone-rig
    ///     native surface (the service resolves to <c>NullDspChainService</c> then).
    /// </summary>
    [ObservableProperty] private bool _isPedalboardOpen;

    public PlayModeToneRigViewModel(
        ProjectState projectState,
        TransportViewModel? transport,
        LivePlayViewModel? livePlay,
        IAudioPlaybackService? audio,
        IHistoryService? history,
        Func<PedalboardViewModel?> pedalboard,
        Func<INamCaptureLibrary?> namCaptures,
        Func<LiveInputChannelViewModel?> inputChannel,
        Func<RigReampViewModel?> reamp,
        Func<IToneMatcher?> toneMatcher,
        Func<IStemIsolator?> stemIsolator,
        Func<IRigEstimator?> rigEstimator,
        Func<IRigEstimatorModelDownloadService?> rigEstimatorModelDownloads,
        Func<IToneSectionProposer?> toneSectionProposer,
        Func<IToneSetMatcher?> toneSetMatcher,
        Func<IToneNarrator?> toneNarrator)
    {
        _projectState = projectState;
        _transport = transport;
        _livePlay = livePlay;
        _audio = audio;
        _history = history;
        _pedalboardFactory = pedalboard;
        _namCaptures = namCaptures;
        _inputChannel = inputChannel;
        _reamp = reamp;
        _toneMatcher = toneMatcher;
        _stemIsolator = stemIsolator;
        _rigEstimator = rigEstimator;
        _rigEstimatorModelDownloads = rigEstimatorModelDownloads;
        _toneSectionProposer = toneSectionProposer;
        _toneSetMatcher = toneSetMatcher;
        _toneNarrator = toneNarrator;
    }

    /// <summary>The tone drawer's AI panel asks for Settings ▸ AI; only the shell
    /// knows where that is, so Play Mode forwards this as its own
    /// <c>OpenSettingsRequested</c>.</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>Raised when the overlay closes by ANY route (its ✕, Esc, Discard),
    /// before <see cref="IsPedalboardOpen" /> is announced. Play Mode ends a
    /// song-tone edit here: that edit lives exactly as long as the rig is open,
    /// and closing it gives the player's board back.</summary>
    public event Action? Closed;

    /// <summary>The loaded song's title and artist, for the AI "Design a tone"
    /// panel — the model designs a better tone for "Master of Puppets" than for
    /// "a metal tone". Set by Play Mode, which owns the flow song.</summary>
    internal Func<(string? Title, string? Artist)>? SongContextSource { get; set; }

    /// <summary>The tone-rig pedalboard VM (the DI singleton the chart editor also
    /// drives), built by its factory on first use.</summary>
    public PedalboardViewModel? Pedalboard => _pedalboard ??= _pedalboardFactory();

    /// <summary>
    ///     The Fire rig screen's own view model, over the shared rig. Gated for
    ///     the same reason <see cref="Pedalboard" /> is lazy: nothing about the
    ///     tone rig may be constructed on the boot path — and the overlay's
    ///     <c>DataContext</c> binding reads this property during the view's
    ///     populate, so a plain lazy getter WAS the boot path
    ///     (<see cref="DeferredRigScreen" />). Null until the overlay opens or
    ///     the prewarm asks (<see cref="EnsureRigScreen" />).
    /// </summary>
    public AmpFxRigViewModel? AmpFxRig => _rigScreen.Get(BuildRigScreenIfAny);

    /// <summary>
    ///     A rig that only a test can see: every factory yields nothing, so the
    ///     overlay works and shows its empty state until
    ///     <see cref="UseRigForTests" /> hands it a chain. What
    ///     <c>PlayModeViewModel</c> uses when it is constructed without DI.
    /// </summary>
    internal static PlayModeToneRigViewModel Detached(
        ProjectState projectState,
        TransportViewModel? transport,
        LivePlayViewModel? livePlay,
        IAudioPlaybackService? audio)
    {
        return new PlayModeToneRigViewModel(
            projectState, transport, livePlay, audio, null,
            static () => null, static () => null, static () => null, static () => null,
            static () => null, static () => null, static () => null, static () => null,
            static () => null, static () => null, static () => null);
    }

    /// <summary>Opens the overlay over cards that reflect chain state built
    /// elsewhere (the chart editor, a tone convert). A no-op without a rig.</summary>
    [RelayCommand]
    internal void OpenPedalboard()
    {
        if (Pedalboard is not { } rig)
        {
            return;
        }

        rig.Sync();
        IsPedalboardOpen = true;
    }

    /// <summary>
    ///     Builds the rig screen if it does not exist and tells the binding.
    ///     Called when the overlay opens and by <c>PlayModeView.HiddenSurfaces()</c>
    ///     from the post-first-paint prewarm drain, so the overlay is prewarmed
    ///     over a REAL screen without that screen being built at bind.
    /// </summary>
    internal void EnsureRigScreen()
    {
        if (_rigScreen.Want(BuildRigScreenIfAny))
        {
            OnPropertyChanged(nameof(AmpFxRig));
        }
    }

    /// <summary>Runs BEFORE the generated setter raises
    /// <see cref="IsPedalboardOpen" />, so the panel turns visible over a bound
    /// screen, not over null.</summary>
    partial void OnIsPedalboardOpenChanged(bool value)
    {
        if (value)
        {
            EnsureRigScreen();
        }
        else
        {
            Closed?.Invoke();
        }
    }

    private AmpFxRigViewModel? BuildRigScreenIfAny() => Pedalboard is { } rig ? BuildRigScreen(rig) : null;

    /// <summary>
    ///     The rig screen over <paramref name="rig" />, wired to the two things
    ///     only a host knows. The DI recorder / re-amp transport is one per
    ///     process (a take keeps playing whichever rig surface is up), so every
    ///     screen shares it. The screen's ✕ does not know which surface is
    ///     showing it, so it asks and this host closes itself.
    ///     <para>
    ///         The AI "Design a tone" panel lives in the tone drawer, on the
    ///         SHARED <see cref="PedalboardViewModel" />, so both rig hosts hand
    ///         it what only a host has. This one has the song, its tone changes
    ///         and the beat map; the panel never resolves a service for itself,
    ///         and a matcher whose factory yields nothing leaves its block off
    ///         screen rather than throwing on the way to the rig. The built-in
    ///         reference DI is the medley the retrieval library was rendered
    ///         over (<c>Assets/Ai/reference-di.wav</c> is embedded and never
    ///         deployed). A rig built without the AI panel (tests) skips all of it.
    ///     </para>
    /// </summary>
    private AmpFxRigViewModel BuildRigScreen(PedalboardViewModel rig)
    {
        var screen = new AmpFxRigViewModel(rig, _namCaptures(), _inputChannel())
        {
            InputPeakProvider = ReadInputPeak,
            DropTune = RigDropTune.Over(_transport),
            CallbackLoadProvider = _audio is { } audio ? RigCpuLoadViewModel.Over(audio) : null,
            Reamp = _reamp()
        };
        screen.CloseRequested += (_, _) => IsPedalboardOpen = false;

        if (rig.Browser.ToneDesign is ToneDesignViewModel design)
        {
            design.SongContextProvider = SongContextSource;
            design.OpenSettings = () => OpenSettingsRequested?.Invoke();
            design.ConfigureMatch(
                _toneMatcher,
                _stemIsolator,
                _rigEstimator,
                () => _audio,
                builtInReferenceDi: static () => DotCharter.Services.Ai.Match.Similarity.ReferenceDi.Medley(),
                modelDownloads: _rigEstimatorModelDownloads);
            design.MatchSourceProvider = ReadMatchSource;
            design.ConfigureSections(
                _toneSectionProposer,
                _toneSetMatcher,
                _toneNarrator,
                () => _audio?.CurrentTimeMs ?? 0.0,
                PlaceToneSet);
        }

        return screen;
    }

    /// <summary>The app's ONE capture tap — the same reading the pre-play signal
    /// meter and the chart editor's rig show.</summary>
    private float ReadInputPeak() => _livePlay?.ReadInputPeak() ?? 0f;

    /// <summary>
    ///     The span half of "Match from audio": only this host can reach the
    ///     active arrangement's tone changes and the beat map, and a match over
    ///     the WRONG span measures the intro's rhythm tone against a lead rig.
    ///     Best-effort: the transport knows its own file and the panel falls back
    ///     to the first 30 s.
    /// </summary>
    private ToneMatchSource ReadMatchSource()
    {
        var arrangement = _projectState.ActiveArrangement;
        var lengthMs = _audio?.GetTotalAudioLengthMs() ?? 0.0;
        var spans = ToneChangeSpans.Resolve(arrangement?.ToneChanges, _projectState.ActiveProject?.Beats, lengthMs);
        return new ToneMatchSource(_audio?.CurrentAudioPath, spans, _audio?.CurrentTimeMs ?? 0.0, lengthMs);
    }

    /// <summary>
    ///     "Apply as tone set", the half only a HOST can do: one
    ///     <see cref="ToneChange" /> per matched section, at that section's start
    ///     (through <see cref="TimelineMath.PositionFromMs" />, the app's ONE
    ///     ms → position conversion), in the ACTIVE arrangement.
    ///     <para>
    ///         <b>It goes through the undo pipeline.</b>
    ///         <c>AddToneChangesAction</c> under <c>IHistoryService.Do</c> is the
    ///         same path the event dialog's tone field takes, so a whole tone set
    ///         is one Ctrl+Z. A host with no history service still places them:
    ///         the undo stack is the chart editor's, and Play Mode is reachable
    ///         without one.
    ///     </para>
    ///     <para>
    ///         <b><c>null</c> means "there is no arrangement", not "placed
    ///         none".</b> The section list keeps the presets it just saved and
    ///         SAYS so — a song that is not in the library is precisely the case
    ///         this feature exists for.
    ///     </para>
    /// </summary>
    /// <param name="entries">Each matched section and the tone it became.</param>
    private int? PlaceToneSet(IReadOnlyList<ToneSetEntry> entries)
    {
        var arrangement = _projectState.ActiveArrangement;
        var beats = _projectState.ActiveProject?.Beats;
        if (arrangement is null || beats is null || beats.Count == 0)
        {
            return null;
        }

        var changes = entries
            .Select(e => new ToneChange
            {
                Position = TimelineMath.PositionFromMs(e.Section.From.TotalMilliseconds, beats),
                ToneName = e.Tone.Name
            })
            .ToList();

        if (changes.Count == 0)
        {
            return 0;
        }

        var action = new AddToneChangesAction(arrangement.ToneChanges, changes);
        if (_history is null)
        {
            action.Execute();
            return changes.Count;
        }

        return _history.Do(action) ? changes.Count : 0;
    }

    /// <summary>
    ///     Test seam: supplies the rig instead of building it through the factory.
    ///     <para>
    ///         The rig screen's regions are almost all ITEM TEMPLATES — the unit
    ///         card, the signal chip, the gear row — and a template is not built
    ///         until an item exists. So a layout smoke test over an unbound
    ///         screen would realize the chrome, measure it, pass, and never touch
    ///         the four templates where an unresolvable resource or a
    ///         <c>Space*</c>-in-<c>Padding</c> actually hides. This lets the test
    ///         hand in a chain and get the real cards.
    ///     </para>
    /// </summary>
    /// <param name="rig">The chain view model to host.</param>
    /// <param name="wantScreen">
    ///     <c>true</c> (the default) marks the screen wanted so the next read of
    ///     <see cref="AmpFxRig" /> builds it, as every layout test expects;
    ///     <c>false</c> leaves the boot-time gate as shipped, for the test that
    ///     pins the gate itself.
    /// </param>
    internal void UseRigForTests(PedalboardViewModel rig, bool wantScreen = true)
    {
        _pedalboard = rig;
        _rigScreen.Reset();
        if (wantScreen)
        {
            _rigScreen.Want(static () => null);
        }

        OnPropertyChanged(nameof(Pedalboard));
        OnPropertyChanged(nameof(AmpFxRig));
    }
}
