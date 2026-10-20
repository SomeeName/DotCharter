// AmpFxRigViewModel root: the Amp & FX rig screen over the one PedalboardViewModel - constructor, meters, insert point, unit selection, branch and category filters, gear groups, row types.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The Fire-layer <b>Amp &amp; FX rig</b> screen (§6.8,
///     <c>Amp and FX Rig.dc.html</c>): header · signal strip · board · module
///     editor dock · footer, with the gear panel down the right.
///
///     <para>
///         <b>It owns no rig state.</b> Every unit, knob, preset, gear card and
///         tuner reading on the screen comes from the ONE
///         <see cref="PedalboardViewModel" /> singleton that already drives the
///         classic rack, which is what makes the two skins the same rig rather
///         than two rigs: change a knob here and the rack has it, because there
///         is only one chain and one managed mirror of it. What this type adds
///         is the screen's OWN state — which unit the dock is showing, what the
///         gear panel is filtered to, and the input meter's poll — plus the
///         projections the mockup's layout needs (signal chips, category
///         groups).
///     </para>
///
///     <para>
///         <b>Nothing here invents a parameter.</b> A unit's knobs are
///         <see cref="PedalNodeViewModel.KnobParams" />, materialized from the
///         native module catalogue; the screen has no per-module table of its
///         own and must never grow one. The mockup draws several knobs the DSP
///         does not have, and
///         <c>AmpFxRigParameterSetTests</c> is what keeps them out.
///     </para>
/// </summary>
public sealed partial class AmpFxRigViewModel : ObservableObject
{
    private readonly PedalboardViewModel _rig;
    private readonly DotCharter.Tone.Online.Tone3000.INamCaptureLibrary? _namLibrary;
    private readonly ILocalizationService _loc;

    public AmpFxRigViewModel(PedalboardViewModel rig,
        DotCharter.Tone.Online.Tone3000.INamCaptureLibrary? namLibrary = null,
        LiveInputChannelViewModel? inputChannel = null)
    {
        _rig = rig;
        _loc = rig.Localization;
        _namLibrary = namLibrary;
        // A service-less picker (a test, a host with no container) reports
        // itself unavailable rather than being null: the footer control is
        // always there, disabled with its reason printed, never a blank spot.
        _inputChannel = inputChannel ?? new LiveInputChannelViewModel(null, null, _loc);
        _inputChannel.Seat = rig.SelectedPlayer;
        // A screen built over a rig whose Tones mode is ALREADY up (the second
        // host, or a re-open) finds it there with nothing to seed: since W4 the
        // mode IS `rig.IsBrowserOpen`, so there is no second flag to keep in
        // step with it — which is what a `_isDrawerExpanded` seeded here used
        // to be for.

        CategoryFilters = new ObservableCollection<RigCategoryFilter>(
            new[] { new RigCategoryFilter(null, _loc["Rig_Cat_All"]) { IsActive = true } }
                .Concat(FireRigCategory.DisplayOrder.Select(c =>
                    new RigCategoryFilter(c, _loc[FireRigCategory.LabelKeyFor(c)]))));

        GearLibraryTabs =
        [
            new RigGearLibraryTab(RigGearLibraryTabKind.Browse,
                _loc["Rig_GearTab_Browse"], ShowBrowseCommand),
            new RigGearLibraryTab(RigGearLibraryTabKind.Favorites,
                _loc["Rig_GearTab_Favorites"], ShowFavoritesCommand)
        ];
        RefreshGearLibraryTabs();

        _rig.Nodes.CollectionChanged += (_, _) => RefreshChain();
        _rig.GearRail.Cards.CollectionChanged += (_, _) => RebuildGearGroups();
        _rig.GearRail.FavoritesChanged += RefreshGearFavorites;
        _rig.PropertyChanged += OnRigPropertyChanged;
        // …and the REQUEST, not just the change: re-clicking the block that is
        // already selected must still bring its editor up (W3).
        _rig.NodeSelectionRequested += (_, _) => RevealSelectedEditor();
        _rig.Browser.PropertyChanged += OnBrowserPropertyChanged;
        _rig.Controls.Editor.AssignmentCommitted += RaiseEditedChanged;
        // The lane fold re-runs on the rig's own re-lay signal, NOT on the node
        // collection: a branch re-tag, a 2↔3 re-width and a junction bypass all
        // change the lanes without touching Nodes (the routing properties are a
        // LAYOUT input — the 0.16 stale-routing owner bug).
        _rig.BoardStructureChanged += RefreshBoardSegments;

        RefreshChain();
        RefreshBoardSegments();
        RebuildGearGroups();
    }

    /// <summary>The one live rig. The screen binds straight through it for
    /// everything the classic rack already exposes.</summary>
    public PedalboardViewModel Rig => _rig;

    /// <summary>
    ///     The footer's guitar-input JACK picker — the owner's "when picking an
    ///     amp you cannot select the channel". It follows the rig's
    ///     seat picker and writes the same config Settings ▸ Audio does; see
    ///     <see cref="LiveInputChannelViewModel" />. Settable so the headless
    ///     smoke test can hand the screen a picker over a fake engine.
    /// </summary>
    [ObservableProperty] private LiveInputChannelViewModel _inputChannel;

    // ── header: preset identity ──────────────────────────────────────────────

    /// <summary>
    ///     True when the live chain does not match the preset that was
    ///     applied to it — the mockup's "EDITED" badge. Derived
    ///     (<see cref="RigEditState" />), never a flag, so no mutation path can
    ///     forget to raise it.
    /// </summary>
    public bool IsEdited =>
        RigEditState.IsEdited(_rig.BuildCurrentRig(), _rig.Browser.ActivePreset?.Rig);

    /// <summary>
    ///     The name on the header's tone pill.
    ///     <para>
    ///         A rig that has never had a preset applied has no name, and the
    ///         browser's own fallback for that is an em dash — fine in a list
    ///         column, wrong on a NAMED control in the header, where it rendered
    ///         as a caret and an EDITED badge with no subject between them.
    ///         "Custom tone" is what that state IS: the chain is real, it just is
    ///         not a saved tone yet. It reads the browser's ACTIVE PRESET rather
    ///         than its display string, so the two fallbacks stay independent.
    ///     </para>
    /// </summary>
    public string PresetName => _rig.Browser.ActivePreset?.Name is { Length: > 0 } name
        ? name
        : _loc["Rig_CustomTone"];

    /// <summary>
    ///     The header's tone-FILE pair: write the live rig out as a JSON tone, or
    ///     read one in. Surfaced on the screen because the tone pill they sit
    ///     beside is in the HEADER, whose DataContext is this screen — the browser
    ///     drawer further down is its own scope
    ///     (<c>x:DataType="vm:PresetBrowserViewModel"</c>), and the same command
    ///     bound from two scopes is one command, not two doors.
    /// </summary>
    public System.Windows.Input.ICommand ExportToneCommand => _rig.Browser.ExportActiveCommand;

    /// <inheritdoc cref="ExportToneCommand" />
    public System.Windows.Input.ICommand ImportToneCommand => _rig.Browser.ImportCommand;

    /// <summary>The tone-file exchange's status line, straight off the browser —
    /// the header prints it where the import happened.</summary>
    public string ToneFileStatus => _rig.Browser.ImportStatus;

    /// <inheritdoc cref="ToneFileStatus" />
    public bool HasToneFileStatus => _rig.Browser.HasImportStatus;

    // ── header: the input meter ──────────────────────────────────────────────

    /// <summary>
    ///     Live capture peak in dBFS, off <c>dca_engine_get_input_peak</c> via
    ///     <see cref="PedalboardViewModel" />'s audio service — the SAME reading
    ///     the menu backdrop and the settings modal use. Negative infinity until
    ///     something is heard; the meter's own floor decides how that draws.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputClipping))]
    private double _inputLevelDb = double.NegativeInfinity;

    /// <summary>
    ///     How tall every unit's faceplate stands on the board
    ///     (<see cref="RigBoardFaceSize" />). Written by the view from the board's
    ///     height and the lane count; the card template binds it.
    /// </summary>
    [ObservableProperty] private double _boardFaceHeight = 220;

    /// <summary>
    ///     The CLIP lamp. Digital full scale is 0 dBFS, so anything at or above
    ///     it is a sample the converter could not represent — this is the real
    ///     peak crossing a real threshold, not a decorated meter.
    /// </summary>
    public bool IsInputClipping => InputLevelDb >= 0.0;

    /// <summary>Polls the engine's input peak. Driven by the view's meter clock
    /// (one timer for the screen, stopped when the screen is not showing) —
    /// never by a timer inside a view model that outlives the surface.</summary>
    public void PollInputMeter(float peak)
        => InputLevelDb = peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);

    /// <summary>
    ///     Where the header meter's number comes from — the app's ONE capture
    ///     tap (<c>LivePlayViewModel.ReadInputPeak</c>), handed in by whichever
    ///     host is showing the screen rather than reached for from here.
    ///     <para>
    ///         Null is a real state, not a missing wire: a host that has no
    ///         capture to read leaves the meter at its floor instead of inventing
    ///         a level for it.
    ///     </para>
    /// </summary>
    public Func<float>? InputPeakProvider { get; set; }

    /// <summary>The footer's drop-tune pedal, handed in by the host (it owns the
    /// value). Null: the host has none, and the footer shows no pedal.</summary>
    public RigDropTuneViewModel? DropTune { get; init; }

    /// <summary>The footer's CPU readout over the engine's callback load (ABI v46),
    /// from a reader the host hands in. Null: the host has no engine, and the footer
    /// shows no readout.</summary>
    public RigCpuLoadViewModel? CpuLoad { get; private init; }

    /// <inheritdoc cref="CpuLoad" />
    public Func<DotCharter.Tone.Audio.Abstractions.AudioCallbackLoad?>? CallbackLoadProvider
    {
        get;
        init
        {
            field = value;
            CpuLoad = value is null ? null : new RigCpuLoadViewModel(value, _loc);
        }
    }

    private RigReampViewModel? _reamp;

    /// <summary>
    ///     The DI recorder + re-amp transport (ABI v37), shared by every rig
    ///     surface of the host. Null in a host that built none (a test): the
    ///     header's DI button is then not drawn.
    /// </summary>
    public RigReampViewModel? Reamp
    {
        get => _reamp;
        set
        {
            if (!SetProperty(ref _reamp, value)) return;
            value?.Player = _rig.SelectedPlayer;
            OnPropertyChanged(nameof(HasReamp));
        }
    }

    /// <summary>True when the host supplied a <see cref="Reamp" />.</summary>
    public bool HasReamp => _reamp != null;

    /// <summary>
    ///     One tick of the screen's live readouts: the input peak (and therefore
    ///     the CLIP lamp) and the EDITED badge. Called by
    ///     <c>AmpFxRigView</c>'s own clock, which runs only while the screen is
    ///     attached and effectively visible — so the two hosts share one poll
    ///     rather than each keeping a timer for the same number.
    /// </summary>
    public void PollMeters()
    {
        PollInputMeter(InputPeakProvider?.Invoke() ?? 0f);
        CpuLoad?.Tick();
        _rig.ModuleMeters.Poll();
        _rig.OutputMeters.Poll();
        _rig.BoardMeters.Poll();
        _rig.CompareLevel.Sample();
        RaiseEditedChanged();
    }

    /// <summary>
    ///     Whether the header draws its own ✕, which is a property of the HOST
    ///     rather than of the rig.
    ///     <para>
    ///         The chart editor shows the rig over a shell that has an ACTIVITY
    ///         RAIL, and the rail's rig row is a two-way toggle: you switch to
    ///         the rig there and you switch away from it there. A ✕ beside it
    ///         would be a second, quieter door onto one boolean.
    ///     </para>
    ///     <para>
    ///         Play Mode has no rail — a run is a game and hides every piece of
    ///         chrome — so there the ✕ is the only pointer door and stays. The
    ///         shell's overlay follows its surface: over Home or a full-bleed
    ///         Library there is no rail either, so it turns the ✕ on.
    ///     </para>
    /// </summary>
    public bool ShowCloseButton
    {
        get => _showCloseButton;
        set => SetProperty(ref _showCloseButton, value);
    }

    private bool _showCloseButton = true;

    /// <summary>
    ///     Raised by the header's ✕. The screen does not know which surface is
    ///     showing it — Play Mode's overlay flag and the chart editor's are
    ///     different properties on different view models — so it asks, and the
    ///     host closes itself.
    /// </summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ── signal strip ─────────────────────────────────────────────────────────

    /// <summary>One chip per chain node, in signal order. Rebuilt on any
    /// composition change; the chips carry no state of their own.</summary>
    public ObservableCollection<PedalNodeViewModel> Chain { get; } = [];

    public bool HasChain => Chain.Count > 0;

    /// <summary>
    ///     The same chain again, each node paired with whether it is the FIRST —
    ///     which is the only thing the signal strip's <c>→</c> separators need.
    ///     <para>
    ///         A separator cannot be a sibling of the item template (an
    ///         <c>ItemsControl</c> has no "between items" slot) and the node view
    ///         models are the RACK's, so they may not grow a screen-shaped index
    ///         property. One tiny projection is the honest way to say it.
    ///     </para>
    /// </summary>
    public ObservableCollection<RigSignalChip> SignalChips { get; } = [];

    // ── the board's insertion point ──────────────────────────────────────────

    /// <summary>
    ///     Which GAP the next added unit lands in — the mockup's <c>+</c>
    ///     buttons on the cables between cards, and the "INSERT AT n" chip.
    ///     Null means the end of the chain, which is what the gear panel did
    ///     before and still does when no gap is armed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsertPoint))]
    [NotifyPropertyChangedFor(nameof(InsertPointLabel))]
    private int? _insertAt;

    public bool HasInsertPoint => InsertAt.HasValue;

    /// <summary>"INSERT AT 3" — 1-based, because it names a POSITION in the
    /// signal path to a player, not an array index.</summary>
    public string InsertPointLabel => InsertAt is { } at
        ? _loc.Format("Rig_InsertAt", at + 1)
        : string.Empty;

    /// <summary>Arms the gap BEFORE this unit. The index is resolved from the
    /// live chain rather than carried on the card, so a reorder cannot leave a
    /// stale one behind. A gap inside a parallel lane carries that lane's
    /// branch with it (<see cref="InsertBranch" />), so the added unit lands in
    /// the lane whose wire was clicked.</summary>
    [RelayCommand]
    private void InsertBefore(PedalNodeViewModel? node)
    {
        if (node is null) return;
        var index = Chain.IndexOf(node);
        if (index < 0) return;
        if (InsertAt == index)
        {
            InsertAt = null;
            return;
        }

        InsertAt = index;
        InsertBranch = LaneOf(node);
    }

    /// <summary>Arms the gap at the END of the chain (the trailing connector).</summary>
    [RelayCommand]
    private void InsertAtEnd() => InsertAt = InsertAt == Chain.Count ? null : Chain.Count;

    [RelayCommand]
    private void ClearInsertPoint() => InsertAt = null;

    /// <summary>
    ///     Adds a gear card, then seats it at the armed gap.
    ///
    ///     <para>
    ///         The rail's own add command APPENDS — that is the one native path
    ///         and it stays the one native path; this only moves the node that
    ///         appeared. The count check is what makes it safe: adding a splitter
    ///         auto-pairs a merger, so the chain grows by TWO and there is no
    ///         single "the new unit" to seat. In that case the insert point is
    ///         simply spent, which is honest — a parallel region is not something
    ///         you drop into a gap.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void AddGear(RigGearItem? item)
    {
        if (item is null) return;

        // With no gap armed the panel's own target lane decides where the unit
        // goes, and its slot is resolved BEFORE the add — afterwards the new node
        // is already folded into a lane (the appended node lands inside the region
        // whenever it auto-merges at the chain end), which would move the answer.
        var laneTarget = InsertAt is null ? AddTargetLane : null;
        var laneIndex = laneTarget is { } wanted ? LaneAddIndex(wanted) : -1;

        var before = _rig.Nodes.Count;
        item.RailAddCommand.Execute(item.Card);

        if (InsertAt is { } at && _rig.Nodes.Count == before + 1)
        {
            var node = _rig.Nodes[^1];
            var target = Math.Clamp(at, 0, _rig.Nodes.Count - 1);
            // A LANE gap seats atomically through DropToRow (move + branch tag),
            // so the unit never lands at the right slot in the wrong lane.
            if (InsertBranch is { } branch) _rig.DropToRow(node, target, branch);
            else _rig.MoveTo(node, target);
        }
        else if (laneTarget is { } lane && laneIndex > 0 && _rig.Nodes.Count == before + 1)
        {
            // Same atomic move+tag as a lane gap: the branch tag is what decides
            // which lane it renders in, and the slot is what keeps it inside the
            // region at all.
            _rig.DropToRow(_rig.Nodes[^1], laneIndex, lane);
        }

        InsertAt = null;
    }

    // ── the module editor dock ───────────────────────────────────────────────

    /// <summary>
    ///     Whether the dock at the board's foot is expanded (the mockup's
    ///     HIDE ⌄ control). Collapsed it leaves only its header.
    ///     <para>
    ///         It is MOMENTARY: the next selection re-opens it, because
    ///         "a block is selected and its editor is not showing" is the state
    ///         that item exists to remove. HIDE still collapses the unit you are
    ///         looking at; it is not a mode. (It does not hand the canvas any
    ///         height either — the canvas does not resize — so what it
    ///         buys is a clear workspace, not a bigger board.)
    ///     </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockBodyVisible))]
    [NotifyPropertyChangedFor(nameof(IsEditorHidden))]
    [NotifyPropertyChangedFor(nameof(HasNoDockSelection))]
    [NotifyPropertyChangedFor(nameof(DockToggleLabel))]
    [NotifyPropertyChangedFor(nameof(HiddenEditorHint))]
    private bool _isDockOpen = true;

    [RelayCommand]
    private void ToggleDock() => IsDockOpen = !IsDockOpen;

    /// <summary>
    ///     THE EMPTY WORKSPACE HAS TWO STATES AND THEY ARE NOT THE SAME SENTENCE
    ///     (W4 round 2). <see cref="IsDockBodyVisible" /> is false in both, and
    ///     the body said "Nothing selected" in both — which is FALSE in the
    ///     second: the dock bar beside it is printing the selected unit's name
    ///     and its ENABLED switch, and the canvas is drawing that block lit.
    ///     <list type="bullet">
    ///         <item><see cref="HasNoDockSelection" /> — genuinely nothing
    ///             picked. On a populated chain that only happens after an
    ///             explicit clear, since <c>EnsureEditorSelection</c> picks one
    ///             on open, so in practice it is the EMPTY-CHAIN state.</item>
    ///         <item><see cref="IsEditorHidden" /> — a unit IS selected and HIDE
    ///             collapsed it. HIDE is momentary (W3) but it is a real state
    ///             until the next pick, and the way out is the same button that
    ///             got you in — which is why its caption moves.</item>
    ///     </list>
    /// </summary>
    public bool IsEditorHidden => HasDockUnit && !IsDockOpen;

    /// <inheritdoc cref="IsEditorHidden" />
    public bool HasNoDockSelection => !HasDockUnit;

    /// <summary>HIDE while the editor is up, SHOW while it is not. A toggle
    /// whose caption does not move says "hide" on the control that unhides.</summary>
    public string DockToggleLabel =>
        _loc[IsDockOpen ? "Rig_Dock_Toggle" : "Rig_Dock_Show"];

    /// <summary>"Legacy Amp is selected but its editor is hidden…" — the empty
    /// body's second sentence, naming the unit the bar beside it already names.
    /// Empty when nothing is selected, so the binding cannot print a stale
    /// name into the other state.</summary>
    public string HiddenEditorHint =>
        DockUnit is { } unit
            ? _loc.Format("Rig_Editor_HiddenBody", unit.DisplayName)
            : string.Empty;

    /// <summary>Re-raises everything that derives from the (HasDockUnit,
    /// IsDockOpen) pair. Called wherever <see cref="IsDockBodyVisible" /> is —
    /// they have the same two inputs, and a site that raised one and not the
    /// others would leave the empty body saying the wrong sentence.</summary>
    private void RaiseDockEmptyState()
    {
        OnPropertyChanged(nameof(IsEditorHidden));
        OnPropertyChanged(nameof(HasNoDockSelection));
        OnPropertyChanged(nameof(DockToggleLabel));
        OnPropertyChanged(nameof(HiddenEditorHint));
        OnPropertyChanged(nameof(IsDetailsShown));
    }

    /// <summary>
    ///     Whether the 244 px editor body is up at all: it needs BOTH the dock
    ///     expanded and a unit selected.
    ///     <para>
    ///         Collapsed, the dock is its 34 px bar and nothing else — the
    ///         mockup's shape. The version this replaced kept the whole body up
    ///         with "nothing selected" centred in it, which spent a third of the
    ///         screen saying nothing and is what the empty board underneath
    ///         already says.
    ///     </para>
    ///     <para>
    ///         It carried a third term — <c>!IsDrawerBodyVisible</c> — while the
    ///         tone designer and the Tones list were a DRAWER sharing the
    ///         workspace with this editor (W1). They are a MODE now
    ///         (<see cref="IsTonesMode" />) and the whole canvas body is hidden
    ///         while it is up, so the editor's own state is simply left alone —
    ///         which is what "leaving the mode returns to the editor exactly as
    ///         left" means.
    ///     </para>
    /// </summary>
    public bool IsDockBodyVisible => IsDockOpen && HasDockUnit;

    /// <summary>
    ///     THE TONES MODE. The tone designer, the matcher and
    ///     the Tones list take the whole body below the rig header; the routing
    ///     canvas is replaced by the read-only chain strip and the gear rail
    ///     stays.
    ///     <para>
    ///         It is <see cref="PedalboardViewModel.IsBrowserOpen" /> under a
    ///         name that says what it now does, deliberately rather than as a
    ///         second flag: the header's preset pill, the Esc walk-up's middle
    ///         rung and both hosts' <c>TryCloseOverlay</c> already drive that
    ///         one property, so the mode inherits every door the drawer had and
    ///         none of them had to learn a new word.
    ///     </para>
    ///     <para>
    ///         Why a mode and not a panel: measured at 1600×900 the drawer left
    ///         the designer a <b>140 px</b> viewport for <b>487 px</b> of
    ///         content, so the prompt field alone did not fit — let alone the
    ///         prompt, Design, "From the recording" and "Match by section"
    ///         together, which are one feature. The pin
    ///         is <c>Perf/RigTonesModeUiPerfSmokeTests</c>.
    ///     </para>
    /// </summary>
    public bool IsTonesMode => _rig.IsBrowserOpen;

    private RigDockUnit? _dockUnit;

    /// <summary>
    ///     The unit the dock is editing — the board/strip selection, wrapped in
    ///     the face the editor should draw for it
    ///     (<see cref="RigEditorFaces" />). Rebuilt on selection change, because
    ///     the face, the model rail and the capture list are all fixed the moment
    ///     you know which unit it is.
    /// </summary>
    public RigDockUnit? DockUnit =>
        _dockUnit ??= _rig.SelectedNode is { } node
            // The rig goes in so the editor header can carry this unit's own
            // composition actions (duplicate / replace / remove) as bindable
            // properties on the unit — the RigGearItem rule; a junction also gets
            // the routing console over the board's live lanes.
            ? new RigDockUnit(node, _namLibrary, _rig.Controls, _rig.MidiControls, _rig.ModuleMeters, _rig, RoutingConsoleFor(node))
            {
                // the gear bar's DETAILS toggle decides whether the editor draws its details column
                ShowsDetails = IsDetailsOpen
            }
            : null;

    /// <summary>A splitter or merger opens the routing console over the board's
    /// live lanes (the same <see cref="RigBoardLane" /> instances the board draws).</summary>
    private RigRoutingConsoleViewModel? RoutingConsoleFor(PedalNodeViewModel node)
        => node.IsSplitter || node.IsMerger
            ? new RigRoutingConsoleViewModel(_rig, BoardSegments.OfType<RigSplitSegment>().FirstOrDefault()?.Lanes)
            : null;

    public bool HasDockUnit => _rig.SelectedNode != null;

    /// <summary>
    ///     SELECTING A BLOCK WINS. There is no state in which a
    ///     block is selected and its editor is not showing, so the two things
    ///     that could hide it are both undone here:
    ///     <list type="bullet">
    ///         <item>the tone drawer holding the workspace — W1's last-gesture
    ///             rule, and picking a block IS the later gesture;</item>
    ///         <item>the dock collapsed by HIDE, which collapses the unit you
    ///             are looking at and is never a mode the next selection
    ///             inherits.</item>
    ///     </list>
    ///     <para>
    ///         It is called from TWO places and needs both.
    ///         <see cref="PedalboardViewModel.SelectedNode" />'s change
    ///         notification catches every path that moves the selection —
    ///         including the ones this screen does not own, like a chain edit's
    ///         re-select and an undo restore — and
    ///         <see cref="PedalboardViewModel.NodeSelectionRequested" /> catches
    ///         the click on a block that was ALREADY selected, which raises no
    ///         change at all. A fix in the tile's command handler alone would
    ///         have missed both halves, which is how the reported bug survived.
    ///     </para>
    /// </summary>
    private void RevealSelectedEditor()
    {
        if (_rig.SelectedNode is null) return;
        // Since W4 the first of the two is LEAVING THE TONES MODE. Picking a
        // block is a request to EDIT it, and the editor is not on screen at all
        // while the mode is up — the same last-gesture rule the drawer had,
        // one structure up.
        _rig.IsBrowserOpen = false;
        IsDockOpen = true;
    }

    /// <summary>Selects a unit by its card — the split region's Split / Merge
    /// nodes, which are drawn as the region's frame rather than as tiles.</summary>
    [RelayCommand]
    private void SelectUnit(PedalNodeViewModel? node)
    {
        if (node is null) return;
        var index = _rig.Nodes.IndexOf(node);
        if (index >= 0) _rig.SelectNodeAt(index);
    }

    /// <summary>The dock bar's ← / →: step the selection through the chain in
    /// signal order, wrapping at either end (the design's "Previous module").</summary>
    [RelayCommand]
    private void SelectPreviousUnit() => StepSelection(-1);

    [RelayCommand]
    private void SelectNextUnit() => StepSelection(+1);

    private void StepSelection(int direction)
    {
        if (Chain.Count == 0) return;
        var current = _rig.SelectedNode is { } node ? Chain.IndexOf(node) : -1;
        var next = current < 0
            ? (direction > 0 ? 0 : Chain.Count - 1)
            : (current + direction + Chain.Count) % Chain.Count;
        _rig.SelectNodeAt(_rig.Nodes.IndexOf(Chain[next]));
    }

    /// <summary>
    ///     Where the docked unit sits: "Lane A" inside the split region, "Series"
    ///     on the serial run, "Routing" for the junctions — the design's
    ///     "Lane B / Overdrive" breadcrumb.
    /// </summary>
    public string DockLaneLabel
    {
        get
        {
            if (_rig.SelectedNode is not { } node) return string.Empty;
            if (node.IsJunctionCard) return _loc["Rig_Editor_RoutingLane"];
            return LaneOf(node) is { } lane
                ? _loc.Format("Rig_Editor_LaneLabel", new RigBoardLane(lane, _loc).Label)
                : _loc["Rig_Editor_SeriesLane"];
        }
    }

    /// <summary>The breadcrumb's colour: the lane's fixed token, or the accent.</summary>
    public string DockLaneTokenKey =>
        _rig.SelectedNode is { IsJunctionCard: false } node && LaneOf(node) is { } lane
            ? new RigBoardLane(lane, _loc).TokenKey
            : "FireAccent";

    /// <summary>Opening a populated rig reveals its amp editor immediately,
    /// preserving an existing selection. It does not preserve a COLLAPSED dock
    /// (W3): selecting anything re-opens the editor, and this selects when
    /// nothing is selected, so the rig never opens onto a hidden editor.</summary>
    public void EnsureEditorSelection()
    {
        if (_rig.SelectedNode is not null || Chain.Count == 0) return;
        var preferred = Chain.FirstOrDefault(n => n.IsAmpHead || n.IsNeuralAmpHead)
                        ?? Chain.FirstOrDefault(n => !n.IsJunctionCard);
        if (preferred is not null) _rig.SelectNodeAt(_rig.Nodes.IndexOf(preferred));
    }

    // ── the board's undo-remove chip ─────────────────────────────────────────

    /// <summary>
    ///     The rig's one-level undo for the last unit removed from THIS board,
    ///     surfaced on the screen so the chip that offers it binds a property pair
    ///     on the DataContext it already has: the board area is a stack of
    ///     <c>DataTemplate</c>s over segments, where reaching the rig means the
    ///     host-cast chain the card's ✕ has to use.
    ///     <para>
    ///         It is the SAME command object, not a second door onto it — the rig
    ///         owns the snapshot, because the snapshot has to be dropped when the
    ///         board changes seat, profile or engine, none of which this screen
    ///         hears about.
    ///     </para>
    /// </summary>
    public System.Windows.Input.ICommand UndoRemoveCommand => _rig.UndoRemoveCommand;

    /// <inheritdoc cref="UndoRemoveCommand" />
    public bool CanUndoRemove => _rig.CanUndoRemove;

    /// <inheritdoc cref="UndoRemoveCommand" />
    public string UndoRemoveLabel => _rig.UndoRemoveLabel;

    // ── parallel branches ────────────────────────────────────────────────────

    /// <summary>
    ///     Which branch of a live parallel region the docked unit runs on.
    ///
    ///     <para>
    ///         The footer's SPLIT 2 / SPLIT 3 opens a parallel region, and a
    ///         region with no way to say WHICH branch a unit is on is a switch
    ///         with nothing behind it — every unit stays on branch 1 and the two
    ///         extra lanes run empty. The classic rack said it by dragging a
    ///         pedal onto a row of the split board; that board is gone with it,
    ///         so the same answer is given here, in the editor, where a unit's
    ///         other properties already live.
    ///     </para>
    ///     <para>
    ///         Empty unless the chain is actually split AND the docked unit is a
    ///         unit — a splitter or merger is the region's own boundary and has
    ///         no branch to be on.
    ///     </para>
    /// </summary>
    public ObservableCollection<RigBranchOption> BranchOptions { get; } = [];

    public bool HasBranchChoice => BranchOptions.Count > 0;

    /// <summary>Re-tags the docked unit's branch WITHOUT moving it in signal
    /// order — <see cref="PedalboardViewModel.DropToRow" /> treats "same index"
    /// as a no-op move and still applies the branch, which is exactly the half
    /// of the drag this needs.</summary>
    [RelayCommand]
    private void SetBranch(RigBranchOption? option)
    {
        if (option is null || _rig.SelectedNode is not { } node) return;
        var index = _rig.Nodes.IndexOf(node);
        if (index < 0) return;
        _rig.DropToRow(node, index, option.Branch);
        RebuildBranchOptions();
    }

    private void RebuildBranchOptions()
    {
        BranchOptions.Clear();

        var branches = _rig.RoutingBranches;
        if (branches > 1 && _rig.SelectedNode is { IsJunctionCard: false } node)
        {
            for (var b = 1; b <= branches; b++)
                BranchOptions.Add(new RigBranchOption(b, node.Branch == b));
        }

        OnPropertyChanged(nameof(HasBranchChoice));
    }

    // ── gear panel ───────────────────────────────────────────────────────────

    /// <summary>ALL + the eight fixed identities, in the mockup's order.</summary>
    public ObservableCollection<RigCategoryFilter> CategoryFilters { get; }

    [ObservableProperty] private RigCategoryFilter? _activeCategory;

    /// <summary>The catalogue, grouped by Fire identity and filtered by the
    /// search box + the active chip. Every card is a REAL module type.</summary>
    public ObservableCollection<RigGearGroup> GearGroups { get; } = [];

    public bool HasGearGroups => GearGroups.Count > 0;

    /// <summary>"17 UNITS" in the mockup — here it is however many modules the
    /// native catalogue actually has.</summary>
    public int UnitCount => _rig.AvailableModules.Count;

    [RelayCommand]
    private void FilterCategory(RigCategoryFilter? filter)
    {
        if (filter is null) return;
        foreach (var f in CategoryFilters) f.IsActive = ReferenceEquals(f, filter);
        ActiveCategory = filter;
        RebuildGearGroups();
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private void OnRigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The jack picker addresses the seat the board shows: with several seats
            // "Input" means THIS player's jack, the "right" box for
            // seat 1, exactly as the tuner moves with the seat.
            case nameof(PedalboardViewModel.SelectedSeat):
                InputChannel.Seat = _rig.SelectedPlayer;
                Reamp?.Player = _rig.SelectedPlayer;
                break;

            // The engine came or went (it is what the spillover switch's
            // availability follows): so does the DI recorder's.
            case nameof(PedalboardViewModel.CanToggleSpillover):
                Reamp?.RefreshAvailability();
                break;

            case nameof(PedalboardViewModel.SelectedNode):
                // SELECTING A BLOCK WINS (W3). There is no state in
                // which a block is selected and its editor is not showing, so
                // the two things that could hide it are both undone here:
                //   • the tone drawer holding the workspace (W1's last-gesture
                //     rule — picking a block IS the later gesture); and
                //   • the dock collapsed by HIDE, which is a momentary collapse
                //     of the CURRENT unit, never a mode the next selection
                //     inherits.
                // This is the ONE place it happens, on purpose: SelectedNode is
                // an ObservableProperty, so every entry point that selects —
                // the tile, SelectUnit, the dock's ← / →, EnsureEditorSelection,
                // an undo restore, the chart editor's host over the same
                // singleton — arrives here whether it knows about the drawer or
                // not. A per-path fix is a fix that the next path will miss,
                // which is exactly how the reported bug survived.
                RevealSelectedEditor();

                _dockUnit?.Release();
                _dockUnit = null;
                OnPropertyChanged(nameof(DockUnit));
                OnPropertyChanged(nameof(HasDockUnit));
                OnPropertyChanged(nameof(IsDockBodyVisible));
                RaiseDockEmptyState();
                OnPropertyChanged(nameof(DockLaneLabel));
                OnPropertyChanged(nameof(DockLaneTokenKey));
                RebuildBranchOptions();
                RevealDetailsForFacelessParams();
                break;

            case nameof(PedalboardViewModel.IsBrowserOpen):
                // The Tones MODE is this flag (W4). Nothing inside the canvas
                // body is touched — the mode is a sibling of it, not a re-layout
                // of it — so leaving it restores the canvas and the module
                // editor exactly as they were.
                OnPropertyChanged(nameof(IsTonesMode));
                break;

            // Opening, widening or closing the parallel region changes how many
            // branches there are to choose from — and closing it removes the
            // choice entirely.
            case nameof(PedalboardViewModel.RoutingBranches):
                RebuildBranchOptions();
                break;

            // The undo chip appears and disappears with the rig's snapshot, which
            // is also dropped by a seat/profile switch and an engine rebuild — none
            // of which this screen hears about directly.
            case nameof(PedalboardViewModel.CanUndoRemove):
                OnPropertyChanged(nameof(CanUndoRemove));
                OnPropertyChanged(nameof(UndoRemoveLabel));
                break;
        }
    }

    private void OnBrowserPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresetBrowserViewModel.ActivePresetName) or null)
        {
            OnPropertyChanged(nameof(PresetName));
        }

        if (e.PropertyName is nameof(PresetBrowserViewModel.ImportStatus) or null)
        {
            OnPropertyChanged(nameof(ToneFileStatus));
            OnPropertyChanged(nameof(HasToneFileStatus));
        }

        RaiseEditedChanged();
    }

    /// <summary>
    ///     Re-reads the EDITED badge. Called by the view's meter clock alongside
    ///     the input poll: the badge is a comparison over the whole chain, so it
    ///     is refreshed on the screen's own cadence rather than hooked to every
    ///     knob's <c>PropertyChanged</c> (which would run the comparison once per
    ///     pointer-move of a drag).
    /// </summary>
    public void RaiseEditedChanged() => OnPropertyChanged(nameof(IsEdited));

    /// <summary>
    ///     Reconciles the screen's two chain mirrors against the rig's live
    ///     node list with MINIMAL collection operations — remove gone, move
    ///     survivors, insert new — instead of a clear+refill.
    ///     The board's card row and the signal strip are non-virtualized
    ///     ItemsControls over these, so a clear+refill would re-inflate every unit
    ///     card per collection event (the rig's incremental <c>Sync</c> raises
    ///     one event per changed node); it would also reset the board ScrollViewer's
    ///     extent, which snaps a scrolled board back to the chain start on
    ///     every add — half of "the scrollbar seems a bit buggy".
    /// </summary>
    private void RefreshChain()
    {
        var nodes = _rig.Nodes;

        for (var i = Chain.Count - 1; i >= 0; i--)
        {
            if (!nodes.Contains(Chain[i]))
                Chain.RemoveAt(i);
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (i < Chain.Count && ReferenceEquals(Chain[i], nodes[i])) continue;
            var j = Chain.IndexOf(nodes[i]);
            if (j >= 0) Chain.Move(j, i);
            else Chain.Insert(i, nodes[i]);
        }

        // The chips pair each node with "am I first" (the → separator rule), so
        // repair by index and only REPLACE a chip whose node or flag changed.
        for (var i = SignalChips.Count - 1; i >= Chain.Count; i--)
            SignalChips.RemoveAt(i);
        for (var i = 0; i < Chain.Count; i++)
        {
            var isFirst = i == 0;
            if (i < SignalChips.Count)
            {
                var chip = SignalChips[i];
                if (ReferenceEquals(chip.Node, Chain[i]) && chip.IsFirst == isFirst) continue;
                SignalChips[i] = new RigSignalChip(Chain[i], isFirst);
            }
            else
            {
                SignalChips.Add(new RigSignalChip(Chain[i], isFirst));
            }
        }

        // The dock keeps its unit while that unit is still on the board (the
        // rig clears the selection when the node is gone, and the selection
        // handler rebuilds the dock) — tearing it down here re-inflated the
        // whole editor face on every chain event. The insert gap may not
        // exist either way.
        if (_dockUnit is { } dock && !nodes.Contains(dock.Node))
        {
            _dockUnit?.Release();
            _dockUnit = null;
            OnPropertyChanged(nameof(DockUnit));
        }

        if (InsertAt is { } at && at > Chain.Count) InsertAt = null;

        OnPropertyChanged(nameof(HasChain));
        OnPropertyChanged(nameof(HasDockUnit));
        OnPropertyChanged(nameof(IsDockBodyVisible));
        RaiseDockEmptyState();
        RefreshGearBadges();
        RebuildBranchOptions();
        RaiseEditedChanged();
    }

    /// <summary>Updates the gear rows' "IN RIG" badges in place. The gear
    /// list's STRUCTURE depends only on the catalogue and the active filter —
    /// rebuilding it (and re-inflating its ~57 row templates) on every chain
    /// event was pure waste; only the badges read the chain.</summary>
    private void RefreshGearBadges()
    {
        var inRig = _rig.Nodes.Select(n => n.ModuleTypeId).ToHashSet(StringComparer.Ordinal);
        foreach (var group in GearGroups)
        {
            foreach (var item in group.Items)
                item.IsInRig = inRig.Contains(item.Type.Id);
        }
    }

    /// <summary>Re-reads the rows' STARS in place, on the rail's own
    /// <c>FavoritesChanged</c> signal. Same reasoning as the badges: the list's
    /// structure depends on the catalogue and the filters, and starring a unit
    /// changes neither (on the Favourites tab the rail rebuilds its cards, which
    /// comes back through the collection subscription).</summary>
    private void RefreshGearFavorites()
    {
        foreach (var group in GearGroups)
        {
            foreach (var item in group.Items)
                item.IsFavorite = _rig.GearRail.IsFavorite(item.ModuleTypeId);
        }

        OnPropertyChanged(nameof(HasFavoriteGear));
    }

    /// <summary>
    ///     The palette's rows, flattened in <b>ONE</b> pass over the rail's
    ///     cards.
    ///     <para>
    ///         The list the view draws carries no group HEADER — the groups only
    ///         ever decided the ORDER — so it is bound to <see cref="GearRows" />,
    ///         one flat, virtualized <c>ListBox</c> rather than an
    ///         <c>ItemsControl</c> per identity nested in a scroller (nested
    ///         items panels cannot virtualize: the inner one is measured with an
    ///         unbounded height and realizes every row it has). <see cref="GearGroups" />
    ///         stays as the grouped projection the screen's own badge/star
    ///         refreshes and the library tests walk.
    ///     </para>
    ///     <para>
    ///         <b>One pass, not one per identity.</b> Looping
    ///         <c>FireRigCategory.DisplayOrder</c> and re-scanning every card inside
    ///         each arm — O(identities x cards): eight full scans and eight
    ///         <c>FireRigCategory.ForModule</c> lookups PER CARD, i.e. 800 of
    ///         them over a 100-module catalogue, on every tab click, every
    ///         favourite toggle and every coalesced keystroke. Bucketing by
    ///         identity once and emitting the buckets in <c>DisplayOrder</c>
    ///         afterwards leaves the order byte-identical and the work linear.
    ///         <c>UI/AmpFxRigParameterSetTests</c> lints that the cards are
    ///         enumerated exactly once.
    ///     </para>
    /// </summary>
    private void RebuildGearGroups()
    {
        var inRig = _rig.Nodes.Select(n => n.ModuleTypeId).ToHashSet(StringComparer.Ordinal);
        var wanted = ActiveCategory?.Category;

        // ONE pass over the catalogue: bucket, then emit in DisplayOrder.
        var buckets = new Dictionary<FireUnitCategory, List<RigGearItem>>();
        foreach (var card in _rig.GearRail.Cards)
        {
            var identity = FireRigCategory.ForModule(card.ModuleTypeId);
            if (wanted is { } only && only != identity) continue;

            if (!buckets.TryGetValue(identity, out var bucket))
                buckets[identity] = bucket = [];

            bucket.Add(new RigGearItem(
                card, identity, _loc, inRig.Contains(card.ModuleTypeId), _rig.GearRail.AddGearCommand,
                AddGearCommand,
                _rig.GearRail.IsFavorite(card.ModuleTypeId),
                _rig.GearRail.ToggleFavoriteCommand));
        }

        GearGroups.Clear();
        var rows = new List<RigGearItem>();
        foreach (var identity in FireRigCategory.DisplayOrder)
        {
            if (!buckets.TryGetValue(identity, out var cards)) continue;

            GearGroups.Add(new RigGearGroup(
                identity,
                _loc[FireRigCategory.LabelKeyFor(identity)],
                cards));
            rows.AddRange(cards);
        }

        // Assigned whole, never Clear()+Add per row: one binding reset costs the
        // virtualizing panel one re-measure, a hundred adds cost it a hundred.
        GearRows = rows;
        GearTilePairs = PairTiles(rows);

        OnPropertyChanged(nameof(HasGearGroups));
        OnPropertyChanged(nameof(UnitCount));
        OnPropertyChanged(nameof(ModuleCountLabel));

        // the rows are new objects; the selection follows the unit, not the row
        var keep = SelectedGear?.ModuleTypeId;
        SelectedGear = keep is null ? null
            : GearGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.ModuleTypeId == keep);
        if (SelectedGear is { } again) again.IsSelected = true;
    }

    /// <summary>
    ///     The flat, DisplayOrder-ordered palette the gear list renders — the
    ///     same <see cref="RigGearItem" /> instances <see cref="GearGroups" />
    ///     holds, concatenated. Replaced wholesale by
    ///     <c>RebuildGearGroups</c>; never mutated in place.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RigGearItem> _gearRows = [];

    /// <summary>"100 modules" under the panel title (57 before the JuceSharp
    /// 0.9 gear wave) — whatever the native catalogue really reports.</summary>
    public string ModuleCountLabel => _loc.Format("Rig_Gear_ModuleCount", UnitCount);

    // ── the highlighted gear row and "Add to rig" ──────────────────────────

    /// <summary>The row the player clicked last; "Add to rig" adds it. Null
    /// until a row is picked, and again once the unit leaves the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGear))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedGearCommand))]
    private RigGearItem? _selectedGear;

    public bool HasSelectedGear => SelectedGear is not null;

    [RelayCommand]
    private void SelectGear(RigGearItem? item)
    {
        if (SelectedGear is { } previous) previous.IsSelected = false;
        SelectedGear = item;
        item?.IsSelected = true;
    }

    /// <summary>The design's "Add to rig": the highlighted row goes where the
    /// panel's add-to target (or the armed gap) says.</summary>
    [RelayCommand(CanExecute = nameof(CanAddSelectedGear))]
    private void AddSelectedGear() => AddGear(SelectedGear);

    private bool CanAddSelectedGear() => SelectedGear is { CanAdd: true };
}

/// <summary>
///     One chip on the signal strip: the unit, plus whether it opens the strip.
///     <see cref="ShowsArrow" /> is the inverse — the mockup draws a <c>→</c>
///     BETWEEN chips, so every chip but the first carries one ahead of itself.
/// </summary>
public sealed class RigSignalChip
{
    public RigSignalChip(PedalNodeViewModel node, bool isFirst)
    {
        Node = node;
        IsFirst = isFirst;
    }

    public PedalNodeViewModel Node { get; }
    public bool IsFirst { get; }
    public bool ShowsArrow => !IsFirst;
}

/// <summary>
///     One branch of the live parallel region, as the dock's segmented control
///     draws it. 1-based, because it names a LANE to a player — the same numbering
///     <c>RegionRowViewModel</c> uses and the same range
///     <see cref="PedalboardViewModel.DropToRow" /> clamps to.
/// </summary>
public sealed class RigBranchOption
{
    public RigBranchOption(int branch, bool isActive)
    {
        Branch = branch;
        IsActive = isActive;
    }

    public int Branch { get; }
    public bool IsActive { get; }
    public string Label => Branch.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One chip in the gear panel's filter row: ALL, or one of the eight
/// fixed identities.</summary>
public sealed partial class RigCategoryFilter : ObservableObject
{
    public RigCategoryFilter(FireUnitCategory? category, string label)
    {
        Category = category;
        Label = label;
    }

    /// <summary>Null = ALL.</summary>
    public FireUnitCategory? Category { get; }

    public string Label { get; }

    /// <summary>The gear browser's tab glyph: <c>IconRigCat&lt;identity&gt;</c>,
    /// <c>IconRigCatAll</c> for ALL (Styles/Icons.axaml).</summary>
    public string IconKey => Category is { } category ? "IconRigCat" + category : "IconRigCatAll";

    [ObservableProperty] private bool _isActive;
}

/// <summary>One identity's block in the gear list: its label, its count and its
/// cards.</summary>
public sealed class RigGearGroup
{
    public RigGearGroup(FireUnitCategory category, string label, IReadOnlyList<RigGearItem> items)
    {
        Category = category;
        Label = label;
        Items = items;
    }

    public FireUnitCategory Category { get; }
    public string Label { get; }
    public IReadOnlyList<RigGearItem> Items { get; }
    public int Count => Items.Count;
}

/// <summary>
///     One gear-list row. It decorates the rack's own
///     <see cref="GearCardViewModel" /> rather than replacing it, so the add
///     command, the model list and the tooltip are the SAME objects the classic
///     rack drives — a second gear model would be a second place for the
///     catalogue to drift.
/// </summary>
public sealed partial class RigGearItem : ObservableObject
{
    private readonly ILocalizationService _loc;

    public RigGearItem(GearCardViewModel card, FireUnitCategory category, ILocalizationService localization, bool isInRig,
        System.Windows.Input.ICommand railAddCommand,
        System.Windows.Input.ICommand addCommand,
        bool isFavorite = false,
        System.Windows.Input.ICommand? toggleFavoriteCommand = null)
    {
        Card = card;
        Category = category;
        _loc = localization;
        IsInRig = isInRig;
        RailAddCommand = railAddCommand;
        AddCommand = addCommand;
        IsFavorite = isFavorite;
        ToggleFavoriteCommand = toggleFavoriteCommand;
    }

    /// <summary>Highlighted by the row's click (the screen's SelectGearCommand),
    /// so "Add to rig" knows which unit to add.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>"AMPS" — the category the design prints before the control count.</summary>
    public string CategoryLabel => _loc[FireRigCategory.LabelKeyFor(Category)];

    public GearCardViewModel Card { get; }

    /// <summary>The row's module type id — the star command's parameter, and the
    /// key the favourites set is written with.</summary>
    public string ModuleTypeId => Card.ModuleTypeId;

    /// <summary>
    ///     The gear rail's OWN <c>ToggleFavoriteCommand</c>, carried per row for
    ///     the same reason <see cref="RailAddCommand" /> is: the gear list is an
    ///     ItemsControl inside an ItemsControl, so <c>$parent[ItemsControl]</c>
    ///     from a row resolves to the INNER one. Its parameter is
    ///     <see cref="ModuleTypeId" />. Null only when the screen was built over a
    ///     rig with no gear rail behind it.
    /// </summary>
    public System.Windows.Input.ICommand? ToggleFavoriteCommand { get; }

    /// <summary>The star. MUTABLE and updated in place, exactly like
    /// <see cref="IsInRig" /> — starring a unit must not re-inflate the list it
    /// is drawn in.</summary>
    [ObservableProperty] private bool _isFavorite;
    public DotCharter.Tone.UI.Controls.Dsp.PedalSkin Skin => DotCharter.Tone.UI.Controls.Dsp.DspModuleVisuals.SkinFor(Card.ModuleTypeId);

    /// <summary>
    ///     The rail's OWN append command — the one native add path. Carried per
    ///     row rather than reached by an ancestor binding, because the gear list
    ///     is an ItemsControl inside an ItemsControl and
    ///     <c>$parent[ItemsControl]</c> from a row resolves to the INNER one.
    ///     (The same reason <c>GearModelOption</c> carries its own command.)
    /// </summary>
    public System.Windows.Input.ICommand RailAddCommand { get; }

    /// <summary>
    ///     What the ROW is bound to: append through
    ///     <see cref="RailAddCommand" /> and then seat the new unit at the
    ///     board's armed insertion gap. Same object for every row (the screen's
    ///     own <c>AddGearCommand</c>), carried here for the nested-ItemsControl
    ///     reason above.
    /// </summary>
    public System.Windows.Input.ICommand AddCommand { get; }
    public FireUnitCategory Category { get; }

    /// <summary>The resource key of the row's identity colour.</summary>
    public string TokenKey => FireUnitState.TokenKeyFor(Category);

    /// <summary>The mockup's "IN RIG" badge — true when the chain already
    /// carries a node of this module type. MUTABLE: the screen updates it in
    /// place on chain changes rather than rebuilding the gear list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _isInRig;

    public bool CanAdd => RailAddCommand.CanExecute(Card);

    public string DisplayName => Card.DisplayName;

    /// <summary>
    ///     The sub-label under the name: how many controls the unit has.
    ///     <para>
    ///         The mockup writes a descriptive blurb here ("Optical", "Soft
    ///         clip", "Pitch widener") — copy for seventeen units, which would
    ///         be a second, hand-written catalogue for fifty-seven and the exact
    ///         shape §7 corrects. The unit's CATEGORY was the obvious
    ///         alternative and is worse than nothing: the rows are already
    ///         grouped by category, so it prints the group heading again on
    ///         every row. The control count is real, comes off the module's own
    ///         parameter list, and is the thing you actually want to know before
    ///         you drop a unit on the board.
    ///     </para>
    /// </summary>
    /// <summary>"AMPS · 9 controls".</summary>
    public string SubLabel =>
        CategoryLabel + " · " + _loc.Plural("Rig_Gear_ControlsOne", "Rig_Gear_Controls", ParamCount);

    /// <summary>Knob count — what the row's glyph draws, straight off the
    /// catalogue's real param list.</summary>
    public int ParamCount => Card.ParamCount;

    public DspModuleType Type => Card.Type;
}
