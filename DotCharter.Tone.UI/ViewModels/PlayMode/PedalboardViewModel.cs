// PedalboardViewModel root: the injected services, the constructor, the owned collaborators (tuner, workspace, setlists, macro bank, tempo, meters), rack zoom, the panel toggles, master bypass, Sync + the board-column rebuild, and pedal selection.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The tone-rig pedalboard: an ordered, reorderable chain of DSP modules the
/// guitar input runs through. Drives <see cref="IDspChainService"/> and mirrors
/// its chain as an <see cref="ObservableCollection{T}"/> of pedal cards. Each
/// card's parameter sliders are built generically from
/// <see cref="DspParamInfo"/> metadata, so a new native module needs zero UI
/// code here.
/// </summary>
public sealed partial class PedalboardViewModel : ViewModelBase
{
    /// <summary>
    ///     The chain this surface is currently editing. NOT readonly:
    ///     picking another seat repoints it at <c>IPlayerDspChains.For(n)</c> and
    ///     calls <see cref="ResyncAll" />, which rebuilds every
    ///     <c>PedalNodeViewModel</c> against the new seat (they hold their own
    ///     reference, handed to them by <c>CreateNodeVm</c> — that is what makes
    ///     a repoint sufficient, and why the repoint may never take the
    ///     incremental <see cref="Sync" /> path).
    /// </summary>
    private IDspChainService _dsp;

    /// <summary>
    ///     The <see cref="IDspChainService.EngineAttached" /> handler, held as one
    ///     delegate instance so a seat repoint can unsubscribe it from the chain it
    ///     is leaving.
    /// </summary>
    private readonly Action _onEngineAttached;

    /// <summary>Every seat's chain. A build with no native per-player surface gets
    /// the null chains (<c>PlayerDspChains.CreateNull</c>); a single-seat set keeps
    /// the seat picker absent and seat 0 behaving exactly as it always did.</summary>
    private readonly IPlayerDspChains _chains;
    private readonly IDspTonePresetService _presets;
    private readonly IRigConfigService _config;
    private readonly IAudioPlaybackService _audio;
    private readonly DotCharter.Tone.Infrastructure.Storage.IFileStorageService _files;
    private readonly DotCharter.Tone.Diagnostics.IUiDispatcher? _dispatcher;
    private readonly INamCaptureLibrary _namLibrary;
    private readonly ILocalizationService _loc;

    /// <summary>Capture path → its library manifest, materialized ONCE per
    /// <see cref="Sync"/> and dropped at the next one. A neural amp's faceplate
    /// asks per card, and <see cref="INamCaptureLibrary.GetAll"/> is a directory
    /// scan plus one JSON read per capture — cheap once, wasteful per card.
    /// Null = not built yet for this rebuild; empty = built and the library is
    /// empty/absent (so it is never rebuilt in a loop).</summary>
    private Dictionary<string, NamCaptureManifest>? _captureIndex;

    /// <summary>Rack zoom bounds for the pedalboard's <c>LayoutTransform</c>.</summary>
    public const double MinZoom = 0.5;
    public const double MaxZoom = 1.5;
    private const double ZoomStep = 0.1;

    public PedalboardViewModel(
        IDspChainService dsp,
        IDspTonePresetService presets,
        ILocalizationService localization,
        IRigConfigService config,
        ITone3000Service tone3000,
        ITone3000AuthService tone3000Auth,
        INamCaptureLibrary namLibrary,
        IAudioPlaybackService audio,
        DotCharter.Tone.Infrastructure.Storage.IFileStorageService fileStorage,
        IPlayerDspChains chains,
        // Opens a capture's attribution page in the browser (the NAM browser's link).
        IShellLauncher shell,
        // Kept optional: null is the INLINE mode (searches, macro posts and preset
        // applies run on the calling thread). Both hosts (DotCharter's DI, RigLab)
        // pass one; unit tests omit it so the applied tone is readable on the next
        // line. Making it required needs a separate apply-worker seam.
        DotCharter.Tone.Diagnostics.IUiDispatcher? dispatcher = null,
        // Subscribes a callback to "the active player profile changed" (DotCharter
        // wires IPlayerProfileService.ActiveProfileChanged). Null: no profiles.
        Action<Action>? subscribeProfileSwitch = null,
        // The AI design panel, built by the host from the browser's seams
        // (ToneDesignHostSeams) — threaded through rather than resolved inside the
        // browser, so a headless fixture can lay the panel out CONFIGURED (the
        // state W4's layout guard is about). Null: no panel (the standalone rig).
        Func<ToneDesignHostSeams, object?>? toneDesignPanel = null)
    {
        _dsp = dsp;
        _loc = localization;
        _onEngineAttached = OnChainEngineAttached;
        _dsp.EngineAttached += _onEngineAttached;
        _chains = chains;
        _presets = presets;
        _config = config;
        _audio = audio;
        _files = fileStorage;
        _dispatcher = dispatcher;
        _namLibrary = namLibrary;
        // The tone rig follows the PROFILE (ProfileToneRigCoordinator reloads
        // every seat's chain on the switch) and this board listened to nothing,
        // so a switch from the shell's popover left the old player's pedals
        // drawn over the new player's chain. POSTED, so the rebuild runs after
        // the coordinator's synchronous reload whatever the subscription order.
        subscribeProfileSwitch?.Invoke(() =>
        {
            if (_dispatcher is not null) _dispatcher.Post(ResyncAll);
            else ResyncAll();
        });
        AvailableModules = dsp.Catalog;
        GearRail = new GearRailViewModel(AvailableModules, localization, type => AddGear(type),
            (type, paramId, index) => AddGearWithModel(type, paramId, index),
            searchPost: dispatcher != null ? dispatcher.Post : null,
            // The starred gear is per INSTALL (AppConfig.RigFavoriteGear), so the
            // rail persists it itself rather than through this view model.
            config: config);
        // The TONE3000 NAM-capture section. Loading a capture into the rig rebuilds
        // the board AND arms the bounded status poll (the native load is
        // asynchronous). () => _dsp, not the ctor's `dsp`: the seat picker REPOINTS
        // _dsp, and a captured chain would make "Load into rig" drop a neural amp on
        // player 1's board while player 2's was on screen.
        var namBrowser = new NamCaptureBrowserViewModel(tone3000, tone3000Auth, namLibrary, () => _dsp, localization,
            onRigChanged: OnNeuralAmpModelChanged,
            openUrl: url => TryOpenUrl(shell, url),
            config: config,
            searchPost: dispatcher != null ? dispatcher.Post : null);
        // The setlists apply through the browser (so the active tone, the picker and
        // the apply queue are the ones every other pick uses); built first so the
        // browser can offer them on its rail. The lambdas run only after both exist.
        Setlists = new RigSetlistsViewModel(presets, config, localization,
            (preset, scene) => Browser!.ApplyPresetOnScene(preset, scene),
            () => Browser?.ActivePreset);
        Browser = new PresetBrowserViewModel(presets, localization, BuildCurrentRig, OnPresetApplied, namBrowser,
            searchPost: dispatcher != null ? dispatcher.Post : null,
            // A card click lands on the seat the picker points at, like every
            // other edit on this surface (and the SCENE strip's compare).
            selectedPlayer: () => SelectedPlayer,
            // …and it lands on a WORKER. Every RemoveNode/MoveNode blocks its
            // caller in the native bypass crossfade (fadeOutAndWait, ≈47 ms at
            // 512/48 kHz), so an eight-module preset swap driven inline is
            // hundreds of milliseconds of frozen UI. The board re-sync comes
            // back through the dispatcher. Keyed on the dispatcher like
            // `searchPost` above and for the same reason: with no UI thread to
            // protect there is nothing to offload FROM, and a test that has to
            // read the applied tone on the next line should not have to poll.
            applyWorker: dispatcher != null ? (action => Task.Run(action)) : null,
            applyPostBack: dispatcher != null ? dispatcher.PostNormal : null,
            // The tone-FILE exchange (export the live rig / import somebody
            // else's) needs the OS pickers; a host without them ships the pair
            // disabled rather than half-working.
            files: fileStorage,
            toneDesignPanel: toneDesignPanel,
            // The AI design panel prints an ordered chain in the CATALOGUE's own
            // words ("Rat → Power Amp → Cab"). `() => _dsp` for the seat-repoint
            // reason above; an unknown id falls back to itself rather than to a
            // blank, because a module this build does not know is information.
            moduleDisplayName: id =>
                _dsp.Catalog.FirstOrDefault(t => t.Id == id)?.DisplayName is { Length: > 0 } label
                    ? label
                    : id,
            // every design request says what the player's strings are tuned to
            // (read per request: the tuner is built just below)
            currentTuning: () => RigTuner?.DesignTuning,
            setlists: Setlists);
        // The tuner's concert pitch is per install (AppConfig.TunerReferenceHz),
        // so it takes the config service and persists it itself.
        RigTuner = new RigTunerViewModel(audio, localization, routing: null, config: config);
        Workspace = new RigWorkspaceViewModel(config);
        _zoom = LoadPersistedZoom();
        Sync();
        // the board as it opened is the first undo point (Sync's own rebuild
        // noted an "edit" that is no edit at all)
        ResetHistory();
    }

    /// <summary>The words the rig prints: the host's localization, handed down to
    /// every child view model and to the screens built over this rig.</summary>
    public ILocalizationService Localization => _loc;

    /// <summary>The in-rig tuner panel (strobe + needle, fed by the managed
    /// tuner suite off the input-capture tap). Opened from the board toolbar,
    /// the flow strip's dial glyph, or automatically when a Pitch-family pedal
    /// is selected (AmpliTube parity).</summary>
    public RigTunerViewModel RigTuner { get; }

    /// <summary>The height of the workspace below the routing canvas (the module
    /// editor and the tone drawer share it): one persisted number for both rig
    /// hosts.</summary>
    public RigWorkspaceViewModel Workspace { get; }

    /// <summary>
    ///     The setlists: edited in the Tones mode's Setlists panel
    ///     (<c>RigSetlistPanel</c>), stepped through by the scene bar's ◀ ▶,
    ///     PageUp / PageDown and a MIDI program change. On the rig, not on a view,
    ///     so both hosts share one active setlist.
    /// </summary>
    public RigSetlistsViewModel Setlists { get; }

    private RigMacroBankViewModel? _controls;

    /// <summary>The shared macro bank: one owner for both rig screens and the MIDI
    /// UI bridge.</summary>
    public RigMacroBankViewModel Controls => _controls ??= new(this, () => _dsp,
        action => { if (_dispatcher is null) action(); else _dispatcher.Post(action); });

    private RigTempoViewModel? _tempo;

    /// <summary>Live tempo controls; they follow <see cref="Nodes" /> as the edited
    /// seat or preset changes.</summary>
    public RigTempoViewModel Tempo => _tempo ??= new(Nodes);

    private RigNodeMetersViewModel? _moduleMeters;

    /// <summary>The selected module's meters; they follow the edited seat and the
    /// visible rig hosts.</summary>
    public RigNodeMetersViewModel ModuleMeters => _moduleMeters ??= new(() => _dsp as IDspMeterSource, () => SelectedNode?.NodeId)
    {
        Spectrum = new(() => SpectrumRenderer, () => _dsp, () => SelectedNode?.NodeId)
    };

    /// <summary>The node spectrum's offline renderer; none in the standalone rig.</summary>
    public DotCharter.Tone.Audio.Dsp.Spectrum.IOfflineToneRenderer? SpectrumRenderer { get; init; }

    private RigBoardMetersViewModel? _boardMeters;

    /// <summary>Every card's output level and the chain latency.</summary>
    public RigBoardMetersViewModel BoardMeters => _boardMeters ??= new(() => _dsp as IDspMeterSource, () => Nodes,
        () => _audio.ActiveSampleRate, _loc);

    private RigNodeMetersViewModel? _outputMeters;

    /// <summary>The rig's output: what leaves the LAST unit in the chain. That is
    /// the only output the engine meters, so the footer's OUTPUT bar reads it
    /// rather than pretending to be a master meter.</summary>
    public RigNodeMetersViewModel OutputMeters => _outputMeters ??= new(() => _dsp as IDspMeterSource, () => Nodes.LastOrDefault()?.NodeId);

    // ---- Rack zoom (persisted in AppConfig, scales the chain container) --------

    /// <summary>Rack zoom factor (0.5..1.5). A <c>LayoutTransform</c> scales the
    /// pedal chain by this, so horizontal scrolling still walks the whole rack.
    /// Persisted as <c>AppConfig.PedalboardZoom</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentLabel))]
    private double _zoom = 1.0;

    /// <summary>Human-readable zoom readout for the toolbar (e.g. "100%").</summary>
    public string ZoomPercentLabel => $"{Math.Round(Zoom * 100)}%";

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            Zoom = clamped; // re-enters with the clamped value, then persists below
            return;
        }

        PersistZoom(clamped);
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Clamp(Zoom + ZoomStep, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Clamp(Zoom - ZoomStep, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1.0;

    private double LoadPersistedZoom()
    {
        try
        {
            var z = _config.LoadRigConfig().PedalboardZoom;
            return z is >= MinZoom and <= MaxZoom ? z : 1.0;
        }
        catch { return 1.0; }
    }

    private void PersistZoom(double value)
    {
        try
        {
            if (Math.Abs(_config.LoadRigConfig().PedalboardZoom - value) < 0.001) return;
            _config.UpdateRigConfig(c => c.PedalboardZoom = value);
        }
        catch { /* zoom is cosmetic — never let a config write break the rig */ }
    }

    // The drawer has no height of its own: the module editor and
    // the drawer share ONE host below the routing canvas and ONE dragged
    // divider — see RigWorkspaceViewModel.

    /// <summary>The native module catalogue (feeds the gear rail).</summary>
    public IReadOnlyList<DspModuleType> AvailableModules { get; }

    /// <summary>The gear rail: the browsable, searchable module catalogue with
    /// custom-drawn miniature cards (replaces the old add-dropdown). Click =
    /// add at chain end; drag onto the rack = insert at the drop slot.</summary>
    public GearRailViewModel GearRail { get; }

    /// <summary>The tone-preset browser panel (category rail + card grid +
    /// save/overwrite/delete over the player's user profiles). Applying a preset
    /// here rebuilds the pedal list via <see cref="OnPresetApplied"/>.</summary>
    public PresetBrowserViewModel Browser { get; }

    /// <summary>Whether the tone-browser drawer at the rack's foot is expanded.
    /// Collapsing it leaves just the drawer header (title + active tone +
    /// chevron) so the rack gets the vertical space.
    /// <para>Closed by DEFAULT since the v8 shell: the command bar now carries
    /// the preset well, the ▲▼ steppers and save/overwrite, so the drawer is the
    /// second-level browse surface rather than the primary one — and the rack
    /// gained ~170 px of chrome (command bar + slot strip + transport) that the
    /// stage would otherwise pay for.</para></summary>
    [ObservableProperty]
    private bool _isBrowserOpen;

    [RelayCommand]
    private void ToggleBrowser() => IsBrowserOpen = !IsBrowserOpen;

    /// <summary>Whether the gear-browser sidebar is showing. The command bar's
    /// GEAR button drives it; hiding it hands the whole width to the stage.</summary>
    [ObservableProperty]
    private bool _isGearRailOpen = true;

    [RelayCommand]
    private void ToggleGearRail() => IsGearRailOpen = !IsGearRailOpen;

    // ---- master bypass (the command bar's BYPASS lamp) -------------------------

    /// <summary>The nodes that were ALREADY bypassed when the master bypass was
    /// armed, keyed by native node id so a <see cref="Sync"/> in between cannot
    /// scramble the restore. Null while the master bypass is off.</summary>
    private HashSet<int>? _bypassRestore;

    /// <summary>True while the whole rig is bypassed from the command bar.</summary>
    [ObservableProperty]
    private bool _isMasterBypassed;

    /// <summary>Bypasses (or restores) every module at once — the rig's "off"
    /// switch. Restoring puts back exactly the per-pedal bypasses that were set
    /// before, so arming and disarming the master is not a way to silently
    /// un-bypass a pedal the player had deliberately switched out.</summary>
    [RelayCommand]
    private void ToggleMasterBypass()
    {
        if (IsMasterBypassed)
        {
            var restore = _bypassRestore;
            _bypassRestore = null;
            IsMasterBypassed = false;
            foreach (var node in Nodes)
                node.Bypassed = restore != null && restore.Contains(node.NodeId);
            return;
        }

        _bypassRestore = Nodes.Where(n => n.Bypassed).Select(n => n.NodeId).ToHashSet();
        IsMasterBypassed = true;
        foreach (var node in Nodes)
            node.Bypassed = true;
    }

    /// <summary>The chain, in signal order (front of chain = index 0).</summary>
    public ObservableCollection<PedalNodeViewModel> Nodes { get; } = [];

    /// <summary>The board laid out in columns (ABI v23): linear pedals, split
    /// junctions (splitter/merger) and the fanned parallel region body. Bound by
    /// the row-grouped board ItemsControl; rebuilt from <see cref="Nodes"/> via
    /// <see cref="SplitRegionLayout"/> after every structural change.</summary>
    public ObservableCollection<BoardColumnViewModel> BoardColumns { get; } = [];

    /// <summary>True when the chain currently has an active split region (a
    /// non-bypassed splitter). Drives a couple of layout hints.</summary>
    public bool HasSplitRegion { get; private set; }

    /// <summary>True when an active split region has NO closing merger — the
    /// engine unity-averages the branches at the chain end. Surfaced as a subtle
    /// "auto-merge at end" indicator chip on the board header.</summary>
    public bool AutoMergeActive { get; private set; }

    /// <summary>The live split region's branch rows (empty when the chain is
    /// serial) — the lane strip beside the signal-flow graph binds these, so the
    /// mixer can never show a lane the board is not actually running.</summary>
    public IReadOnlyList<RegionRowViewModel> SplitLanes { get; private set; } = [];

    public bool HasSplitLanes => SplitLanes.Count > 0;

    // ---- transport-panel furniture (DRAWN, NOT CONNECTED) ----------------------
    // The rig's transport panel is part of the design and none of it is wired:
    // the transport belongs to the chart editor and Play Mode, and there is no
    // metering / CPU / latency channel on this view model. These two lists give
    // the panel its SHAPE only — a fixed segment count and eight bank pads — so
    // the markup does not have to hand-repeat identical Borders. Nothing here
    // ever changes at runtime; a meter that moved without a source behind it
    // would be worse than one that had been left out.

    /// <summary>Segment placeholders for the (inert) input/output meters.</summary>
    public IReadOnlyList<string> MeterSegments { get; } =
        [.. Enumerable.Range(0, 16).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Pad captions for the (inert) bar selector.</summary>
    public IReadOnlyList<string> BarSelectorPads { get; } =
        [.. Enumerable.Range(1, 8).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    public bool HasNodes => Nodes.Count > 0;
    public bool HasModules => AvailableModules.Count > 0;

    /// <summary>
    ///     Reconciles the pedal list against the service's current chain. Call
    ///     when the overlay opens (so a chain built elsewhere is reflected) and
    ///     after any bulk chain change (a preset apply, a capture load).
    ///     <para>
    ///         INCREMENTAL: cards are diffed by native
    ///         <see cref="PedalNodeViewModel.NodeId" /> — a surviving node keeps
    ///         its view model (its params/bypass/branch are MIRRORED from the
    ///         service, without re-pushing to native) and therefore its realized
    ///         card in every host, so adding one pedal inflates ONE card and a
    ///         tone switch reuses whatever the two rigs share. The previous full
    ///         teardown re-inflated every <c>FireUnitCard</c> on every add,
    ///         remove and preset apply — measured at ~280 ms of UI-thread work
    ///         per gesture on a 7-module board (Debug, headless), which is the
    ///         freeze the owner reported. The selection survives when its node
    ///         does, which is what keeps the module editor dock stable across a
    ///         preset apply.
    ///     </para>
    ///     <para>
    ///         A SEAT REPOINT must not come through here — it swaps
    ///         <see cref="_dsp" /> underneath view models that hold the OLD
    ///         chain, and node ids are only unique per chain. That path calls
    ///         <see cref="ResyncAll" />.
    ///     </para>
    /// </summary>
    public void Sync()
    {
        _captureIndex = null; // a resync re-reads the library (a capture may have been downloaded)
        var nodes = _dsp.GetNodes();

        // Existing cards by native node id — the reuse key.
        var byId = new Dictionary<int, PedalNodeViewModel>(Nodes.Count);
        foreach (var vm in Nodes) byId[vm.NodeId] = vm;

        var target = new List<PedalNodeViewModel>(nodes.Count);
        foreach (var node in nodes)
        {
            target.Add(byId.TryGetValue(node.NodeId, out var existing) && existing.CanMirror(node)
                ? MirrorNodeVm(existing, node)
                : CreateNodeVm(node));
        }

        ApplyNodeDiff(target);
        OnPropertyChanged(nameof(HasNodes));
        ReassertMasterBypass();
        RebuildBoardColumns();
        RefreshNeuralAmpStatus();
        // a preset, an undo step or a restore brings its own scenes (or none)
        RefreshScenes();
    }

    /// <summary>
    ///     Keeps the master BYPASS lamp honest across a chain change on the SAME
    ///     board — a preset apply, an A/B compare push, a capture loaded into the
    ///     rig.
    ///     <para>
    ///         The master bypass means "the rig is off", and it must survive
    ///         those as more than a LAMP: <c>DcRigApplier</c> writes the incoming rig's
    ///         own (un-bypassed) flags onto every node it reuses or adds, so
    ///         picking a tone with the rig switched off would bring the sound straight
    ///         back while the lamp still said BYPASS — and the next press then
    ///         "restored" a set of node ids that half belonged to a board that was
    ///         gone, un-bypassing pedals the incoming tone had
    ///         deliberately switched out. The seat repoint and the profile switch
    ///         are the other half of this and are handled by <see cref="ResyncAll" />,
    ///         which drops the armed state outright — there the BOARD is somebody
    ///         else's, so the gesture is not theirs to keep.
    ///     </para>
    /// </summary>
    private void ReassertMasterBypass()
    {
        if (!IsMasterBypassed) return;

        // A node that is gone must not keep a claim on the restore set: node ids
        // are handed out per chain and a reused id would come back from the next
        // disarm claiming it had been switched out by hand.
        if (_bypassRestore is { } restore && restore.Count > 0)
        {
            var live = new HashSet<int>(Nodes.Select(n => n.NodeId));
            restore.RemoveWhere(id => !live.Contains(id));
        }

        foreach (var node in Nodes)
        {
            if (!node.Bypassed)
                node.Bypassed = true;
        }
    }

    /// <summary>
    ///     A NEW native engine is live, so the chain this board mirrors was
    ///     emptied and repopulated underneath it — every node id it holds is dead.
    ///     <para>
    ///         <b>Why this subscription has to exist.</b> The board is a DI
    ///         singleton built long before the engine, and it is reconciled only
    ///         when a host opens the rig (<c>PlayModeViewModel.Overlays</c>,
    ///         <c>MainWindowViewModel.ToneRig</c>). With the rig ALREADY open, a
    ///         device change, a buffer-size change or a watchdog recovery left
    ///         every card bound to a node id the new engine never handed out: the
    ///         cards drew pedals that did not exist, every knob write went to a
    ///         dead id and was silently refused, and Remove/Move addressed nothing.
    ///         <c>ToneSessionService</c> restores the rig on this very event and
    ///         nothing told the surface. (<c>NodesChanged</c> is the wrong signal —
    ///         it also fires for this board's own edits, which is why the doc
    ///         comment claiming the pedalboard rebuilds from it was never true.)
    ///     </para>
    ///     <para>
    ///         POSTED: the event is raised on the attaching thread inside the
    ///         playback service's gate, and the tone restore runs inline on it, so
    ///         a posted rebuild reads a chain that is already repopulated whatever
    ///         the subscription order. <see cref="ResyncAll" /> is right rather than
    ///         the incremental <see cref="Sync" /> — the id-keyed diff would
    ///         false-match a reused id onto a card built against the old engine —
    ///         and dropping the master bypass and the compare slots with it is
    ///         correct for the same reason: both are keyed to a board that is gone.
    ///     </para>
    /// </summary>
    private void OnChainEngineAttached()
    {
        if (_dispatcher is not null) _dispatcher.Post(OnEngineAttachedOnUi);
        else OnEngineAttachedOnUi();
    }

    private void OnEngineAttachedOnUi()
    {
        ResyncAll();
        RaiseSpilloverAvailability();
    }

    /// <summary>Full teardown + rebuild of every pedal card. The SEAT REPOINT
    /// path (<c>OnSelectedSeatChanged</c>): each <see cref="PedalNodeViewModel"/>
    /// holds the chain it was created against, so pointing the surface at
    /// another seat's chain must hand every card the NEW chain — which only a
    /// rebuild does — and node ids are per-chain, so the id-keyed diff above
    /// could false-match across seats.</summary>
    public void ResyncAll()
    {
        SelectedNode = null;
        _captureIndex = null;
        Nodes.Clear();
        foreach (var node in _dsp.GetNodes())
            Nodes.Add(CreateNodeVm(node));
        OnPropertyChanged(nameof(HasNodes));
        RebuildBoardColumns();
        RefreshNeuralAmpStatus();

        // Everything this surface remembers ABOUT A BOARD rather than about the
        // app is scoped to the board that just went away, and both of the things
        // in that class silently addressed the wrong guitar.
        //
        // The master bypass is an armed gesture keyed by NATIVE NODE ID, and node
        // ids are only unique per chain — both seats start at 1. Carried across a
        // repoint the lamp claimed the new board was bypassed when it was not,
        // and the next press "restored" the OTHER seat's bypass pattern onto it,
        // un-bypassing pedals its player had deliberately switched out.
        _bypassRestore = null;
        IsMasterBypassed = false;

        // …and so is the undo-remove snapshot: it holds a chain INDEX and a set of
        // param values read off a board that is not the board on screen, so
        // "put it back" would drop somebody else's unit into this seat's chain at
        // a slot that means nothing here.
        ClearRemovedUnitSnapshot();

        // The A/B slots are two snapshots of ONE board. Left alone, pressing
        // SWITCH after a repoint applied the previous seat's (or the previous
        // PROFILE's) whole rig onto the board on screen — the wrong-guitar shape
        // the browser apply and the tuner each shipped. Emptied, not re-seeded:
        // the first press then FORKS from the new board, which is silent, where a
        // seed would apply a rig identical to the live one.
        ResetCompareSlots();

        // The scene bar follows the chain it now mirrors (another seat's, another
        // profile's, or the rig a new engine was restored with) — and a scene asked
        // for on the board that went away is not this board's to switch.
        _sceneAfterApply = null;
        RefreshScenes();

        // …and the undo history: its steps are snapshots of the board that went away.
        ResetHistory();
    }

    /// <summary>Mutates <see cref="Nodes"/> into <paramref name="target"/> with
    /// the MINIMAL collection operations (remove gone, move survivors, insert
    /// new) so the board's ItemsControls keep the containers of every surviving
    /// card. Clears the selection only when the selected node is gone.</summary>
    private void ApplyNodeDiff(List<PedalNodeViewModel> target)
    {
        var keep = new HashSet<PedalNodeViewModel>(target);
        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Nodes[i]))
                Nodes.RemoveAt(i);
        }

        if (SelectedNode is { } selected && !keep.Contains(selected))
            SelectedNode = null;

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Nodes.Count && ReferenceEquals(Nodes[i], target[i])) continue;
            var j = Nodes.IndexOf(target[i]);
            if (j >= 0) Nodes.Move(j, i);
            else Nodes.Insert(i, target[i]);
        }
    }

    /// <summary>The reuse half of <see cref="Sync"/>: pushes the service's
    /// current state for <paramref name="node"/> onto the surviving card.</summary>
    private static PedalNodeViewModel MirrorNodeVm(PedalNodeViewModel vm, DspNode node)
    {
        vm.MirrorFrom(node);
        return vm;
    }

    /// <summary>
    ///     Raised after every board re-lay (<see cref="RebuildBoardColumns"/>) —
    ///     ONE signal per finished structural gesture, including the mutations
    ///     that never touch <see cref="Nodes"/> (a branch re-tag, a 2↔3
    ///     re-width, a junction bypass). The Fire screen's lane fold
    ///     (<c>AmpFxRigViewModel.RefreshBoardSegments</c>) hangs off this rather
    ///     than off the collection, because the routing properties are a LAYOUT
    ///     input (the 0.16 stale-routing owner bug).
    /// </summary>
    public event Action? BoardStructureChanged;

    /// <summary>Re-projects <see cref="Nodes"/> into <see cref="BoardColumns"/>
    /// through the pure <see cref="SplitRegionLayout"/>. Called after every
    /// structural mutation (add / remove / move / branch / junction bypass /
    /// enabledBranches) so the board mirrors the native routing.</summary>
    public void RebuildBoardColumns()
    {
        var infos = Nodes
            .Select(n => new BoardNodeInfo(n.ModuleTypeId, n.Bypassed, n.Branch, n.EnabledBranches))
            .ToList();
        var layout = SplitRegionLayout.Compute(infos);

        BoardColumns.Clear();
        foreach (var col in layout.Columns)
        {
            switch (col.Kind)
            {
                case BoardColumnKind.Pedal:
                    BoardColumns.Add(new PedalColumnViewModel(Nodes[col.NodeIndex], isJunction: false));
                    break;
                case BoardColumnKind.Junction:
                    BoardColumns.Add(new PedalColumnViewModel(Nodes[col.NodeIndex], isJunction: true));
                    break;
                case BoardColumnKind.Region:
                    var region = col.Region!;
                    var rows = new List<RegionRowViewModel>(region.RowCount);
                    for (var r = 0; r < region.RowCount; r++)
                        rows.Add(new RegionRowViewModel(r + 1, region.Rows[r].Select(i => Nodes[i])));
                    BoardColumns.Add(new RegionColumnViewModel(rows, region.AutoMergeAtEnd));
                    break;
            }
        }

        HasSplitRegion = layout.RegionRowCount > 1;
        AutoMergeActive = BoardColumns.OfType<RegionColumnViewModel>().Any(r => r.AutoMergeAtEnd);
        SplitLanes = BoardColumns.OfType<RegionColumnViewModel>().FirstOrDefault()?.Rows ?? [];
        OnPropertyChanged(nameof(HasSplitRegion));
        OnPropertyChanged(nameof(AutoMergeActive));
        OnPropertyChanged(nameof(SplitLanes));
        OnPropertyChanged(nameof(HasSplitLanes));
        NotifyRoutingMode();
        BoardStructureChanged?.Invoke();
        NoteEdit(); // a unit added, removed, moved or re-laned: an undo step
    }

    private PedalNodeViewModel CreateNodeVm(DspNode node)
    {
        var type = AvailableModules.FirstOrDefault(m => m.Id == node.ModuleTypeId);
        var vm = new PedalNodeViewModel(_dsp, node, type, _loc, _audio, _files, LookupCaptureManifest,
            ListLibraryChoices);
        // A junction bypass / enabledBranches change re-lays the board.
        vm.StructureChanged += RebuildBoardColumns;
        // A capture picked on a NEURAL AMP card loads asynchronously natively — arm
        // the bounded status poll so the badge stops claiming "no model loaded".
        // (No Sync here: the composition didn't change and a rebuild would tear the
        // card the user is interacting with out from under them.)
        if (vm.IsNeuralAmp)
            vm.StringCommitted += ScheduleNeuralAmpStatusRefresh;
        TrackEdits(vm);
        return vm;
    }

    // ---- selection (flow strip → board highlight) ------------------------------

    /// <summary>The pedal highlighted on the board (set by a flow-strip click).
    /// Cleared automatically when the chain rebuilds or the node is removed.</summary>
    [ObservableProperty]
    private PedalNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(PedalNodeViewModel? oldValue, PedalNodeViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;

        // AmpliTube parity: selecting a Pitch-family pedal surfaces the tuner.
        if (newValue?.Category == DspModuleCategory.Pitch)
            RigTuner.Open();
    }

    /// <summary>
    ///     Raised whenever a selection is REQUESTED — including a re-select of
    ///     the node that is already selected, which
    ///     <see cref="SelectedNode" />'s own change notification cannot report.
    ///     <para>
    ///         That gap is not academic: the rig auto-selects an amp when it
    ///         opens, so the FIRST block a player clicks is routinely the one
    ///         already selected, and "clicking a block opens its settings" has
    ///         to hold for that click too. Making the property re-notify instead
    ///         would tear down and rebuild <c>RigDockUnit</c> on every click,
    ///         which is the re-inflation the chain-mirror work avoids.
    ///     </para>
    /// </summary>
    public event EventHandler<PedalNodeViewModel?>? NodeSelectionRequested;

    /// <summary>Selects the chain node at <paramref name="index"/> (flow-strip
    /// click). Out-of-range clears the selection. Returns the selected node.</summary>
    public PedalNodeViewModel? SelectNodeAt(int index)
    {
        SelectedNode = index >= 0 && index < Nodes.Count ? Nodes[index] : null;
        NodeSelectionRequested?.Invoke(this, SelectedNode);
        return SelectedNode;
    }

    /// <summary>Selects a node by reference — the slot strip's row click (it
    /// binds the node itself, not an index), and the board drag controller's
    /// click-without-travel.</summary>
    [RelayCommand]
    private void SelectNode(PedalNodeViewModel? node)
    {
        SelectedNode = node;
        NodeSelectionRequested?.Invoke(this, node);
    }
}
