using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.ViewModels.Editor;

public class MultiTrackOverlayViewModel : ViewModelBase
{
    private readonly IGhostArrangementsService _ghostService;
    private readonly IStackedArrangementsService _stackedService;
    private readonly IGlobalEditingCoordinator _editingCoordinator;
    private readonly ProjectState _projectState;
    private readonly ITrackContextRegistry _trackContexts;
    private readonly IConfigurationService _configService;
    private readonly AppConfig _appConfig;
    private readonly IAppLogger _logger;

    /// <summary>The active timeline's ribbon while we persist its toggles.</summary>
    private RibbonVisibility? _activeRibbon;

    /// <summary>The project whose Arrangements collection we're watching —
    /// adding/removing an arrangement must rebuild the Tracks flyout lists
    /// (they would silently go stale).</summary>
    private ChartProject? _subscribedArrangementsProject;

    public MultiTrackOverlayViewModel(
        IGhostArrangementsService ghostService,
        IStackedArrangementsService stackedService,
        IGlobalEditingCoordinator editingCoordinator,
        ProjectState projectState,
        ITrackContextRegistry trackContexts,
        IConfigurationService configService,
        IAppLogger logger)
    {
        _ghostService = ghostService;
        _stackedService = stackedService;
        _editingCoordinator = editingCoordinator;
        _projectState = projectState;
        _trackContexts = trackContexts;
        _configService = configService;
        _appConfig = configService.LoadAppConfig();
        _logger = logger;

        _ghostService.Changed += () =>
        {
            OnPropertyChanged(nameof(VisibleGhostCount));
            RebuildEntries();
        };
        _stackedService.Changed += () =>
        {
            foreach (var entry in AllEntries)
                entry.NotifyStackVisibilityChanged();
            RefreshStackedEntries();
        };
        _projectState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.ActiveProject))
            {
                OnActiveProjectReplaced();
            }
            else if (e.PropertyName == nameof(ProjectState.ActiveArrangement))
            {
                RefreshActiveFlags();
                // ...and the ROW TEXT, which is not a function of which
                // arrangement is active. Arrangement is a plain domain object, so
                // renaming or retuning one in the Arrangement Settings dialog
                // changes nothing any binding can see; ProjectState re-raises
                // this property for exactly that case (see
                // NotifyActiveArrangementChanged) and it is the only signal that
                // reaches here.
                RefreshArrangementMetadata();
            }
        };

        RebuildEntries();
    }

    /// <summary>Non-active arrangements — used by the toolbar ghost + stack flyouts.</summary>
    public ObservableCollection<MultiTrackEntry> Entries { get; } = [];

    /// <summary>All arrangements including active — used internally for state tracking.</summary>
    public ObservableCollection<MultiTrackEntry> AllEntries { get; } = [];

    /// <summary>
    /// Non-active arrangements the user has explicitly pinned to the stacked
    /// tracks panel via the toolbar selector. Driven by
    /// <see cref="IStackedArrangementsService"/>.
    /// </summary>
    public ObservableCollection<MultiTrackEntry> StackedEntries { get; } = [];

    public int VisibleGhostCount => _ghostService.VisibleGhosts.Count;

    public bool HasOtherArrangements => Entries.Count > 0;

    public bool HasAnyArrangements => AllEntries.Count > 0;

    public bool HasStackedEntries => StackedEntries.Count > 0;

    // ── Tracks popover: SOLO the arrangement you are editing ────────────────

    /// <summary>What was on screen before Solo cleared it, so unsoloing puts
    /// the same overlays back rather than "restoring" to nothing.</summary>
    private readonly List<Arrangement> _soloedAwayGhosts = [];
    private readonly List<Arrangement> _soloedAwayStacks = [];
    private bool _isSoloed;

    /// <summary>
    ///     `Play Menus.dc.html` draws Solo / Mute / Settings… under the
    ///     arrangement picker. <b>Solo here is a TIMELINE solo</b>: on, nothing
    ///     but the arrangement you are editing is drawn — every other
    ///     arrangement's ghost and stacked row is cleared and remembered, and
    ///     turning it off puts exactly those back.
    ///     <para>
    ///         It is deliberately not an audio solo: an <c>Arrangement</c> has no
    ///         volume, mute, solo or bus of its own anywhere in this app, so a
    ///         chip that claimed to silence one would be a
    ///         capability the app cannot honour, looking live. The Mute beside it
    ///         is the MIDI bus that SOUNDS these notes and says so.
    ///     </para>
    /// </summary>
    public bool IsSoloed
    {
        get => _isSoloed;
        set
        {
            if (_isSoloed == value) return;

            if (value)
            {
                var (ghosts, stacks) = TimelineSolo.Engage(_ghostService, _stackedService);
                _soloedAwayGhosts.Clear();
                _soloedAwayGhosts.AddRange(ghosts);
                _soloedAwayStacks.Clear();
                _soloedAwayStacks.AddRange(stacks);
            }
            else
            {
                TimelineSolo.Release(_ghostService, _stackedService, _soloedAwayGhosts, _soloedAwayStacks);
                _soloedAwayGhosts.Clear();
                _soloedAwayStacks.Clear();
            }

            _isSoloed = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Solo means nothing while this project has one arrangement — there is
    ///     nothing to hide — so the chip disables instead of toggling a state
    ///     the timeline cannot show.
    /// </summary>
    public bool CanSolo => HasOtherArrangements;

    /// <summary>
    ///     A project switch must FORGET the project it closed — and until this
    ///     existed, the two multi-track services never did.
    ///     <para>
    ///         <see cref="IGhostArrangementsService" /> and
    ///         <see cref="IStackedArrangementsService" /> are app-wide singletons
    ///         holding <c>HashSet&lt;Arrangement&gt;</c>. <c>Arrangement</c>
    ///         declares no <c>Equals</c>/<c>GetHashCode</c>, so those are
    ///         REFERENCE keys — the exact shape of the
    ///         <c>TrackContextRegistry</c> leak. Their
    ///         <c>Clear()</c> had exactly one product caller,
    ///         <c>TimelineSolo</c>, and none on any project-replace path, so
    ///         every arrangement the session ever ghosted or stacked stayed
    ///         pinned with its whole chart graph for the life of the process.
    ///     </para>
    ///     <para>
    ///         Ghosts were also a visible CORRECTNESS bug, not only memory:
    ///         <c>ChartRenderer</c> walks <c>VisibleGhosts</c> directly and takes
    ///         <c>Levels[0].Notes</c> off each one, so the closed project's notes
    ///         were drawn over the open project's timeline. Stacked rows filter
    ///         through <c>AllEntries</c> (rebuilt from the open project), so that
    ///         half was memory-only.
    ///     </para>
    /// </summary>
    private void OnActiveProjectReplaced()
    {
        _ghostService.Clear();
        _stackedService.Clear();

        // Solo remembers what it hid so that releasing puts exactly that back.
        // Across a switch those are the CLOSED project's arrangements, so
        // releasing would push them straight back into the services just
        // cleared — and hold them until then. Drop the memory and the mode
        // together; the new project decides for itself whether Solo is even
        // available (CanSolo).
        _soloedAwayGhosts.Clear();
        _soloedAwayStacks.Clear();
        if (_isSoloed)
        {
            _isSoloed = false;
            OnPropertyChanged(nameof(IsSoloed));
        }

        RebuildEntries();
    }

    private void RebuildEntries()
    {
        DetachEntries();
        DetachActiveRibbon();
        Entries.Clear();
        AllEntries.Clear();
        StackedEntries.Clear();

        var project = _projectState.ActiveProject;
        SubscribeArrangementsCollection(project);
        if (project == null)
        {
            OnPropertyChanged(nameof(HasOtherArrangements));
            OnPropertyChanged(nameof(CanSolo));
            OnPropertyChanged(nameof(HasAnyArrangements));
            OnPropertyChanged(nameof(HasStackedEntries));
            return;
        }

        TrackEditorContext? activeCtx = null;
        foreach (var arr in project.Arrangements)
        {
            var isActive = arr == _projectState.ActiveArrangement;
            var ctx = _trackContexts.GetOrCreate(arr);
            ApplyActivationDefaults(ctx, isActive);
            if (isActive) activeCtx = ctx;
            var entry = new MultiTrackEntry(arr, ctx, isActive, _ghostService, _stackedService,
                _editingCoordinator, _projectState, _logger);
            entry.RequestRefresh += OnEntryRequestRefresh;
            AllEntries.Add(entry);
            if (!isActive) Entries.Add(entry);
        }

        AttachActiveRibbon(activeCtx);
        RefreshStackedEntries();
        OnPropertyChanged(nameof(HasOtherArrangements));
        OnPropertyChanged(nameof(CanSolo));
        OnPropertyChanged(nameof(HasAnyArrangements));
    }

    /// <summary>
    /// Rebuilds <see cref="StackedEntries"/> from the user's stack-selection
    /// service, filtered to non-active arrangements only. Active arrangement
    /// is excluded because it already lives at the top of the timeline.
    /// </summary>
    private void RefreshStackedEntries()
    {
        StackedEntries.Clear();
        foreach (var entry in AllEntries)
        {
            if (!entry.IsActive && _stackedService.IsStacked(entry.Arrangement))
                StackedEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasStackedEntries));
    }

    private void SubscribeArrangementsCollection(ChartProject? project)
    {
        if (ReferenceEquals(_subscribedArrangementsProject, project)) return;
        _subscribedArrangementsProject?.Arrangements.CollectionChanged -= OnArrangementsCollectionChanged;
        _subscribedArrangementsProject = project;
        _subscribedArrangementsProject?.Arrangements.CollectionChanged += OnArrangementsCollectionChanged;
    }

    private void OnArrangementsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildEntries();

    private void DetachEntries()
    {
        foreach (var e in AllEntries) e.RequestRefresh -= OnEntryRequestRefresh;
    }

    /// <summary>
    /// Updates IsActive on existing entries and refreshes the non-active
    /// <see cref="Entries"/> filter without recreating MultiTrackEntry
    /// instances. Preserving entries means the stacked-view ChartRenderer
    /// controls (and any in-progress pointer interactions on them) are not
    /// torn down when the user switches the active arrangement.
    /// </summary>
    private void RefreshActiveFlags()
    {
        // Detach BEFORE the defaults pass: the ribbon that was active gets
        // its lyrics/metadata rows forced off below, and that must not be
        // persisted as if the user had toggled them.
        DetachActiveRibbon();

        var active = _projectState.ActiveArrangement;
        TrackEditorContext? activeCtx = null;
        foreach (var entry in AllEntries)
        {
            entry.IsActive = entry.Arrangement == active;
            ApplyActivationDefaults(entry.Context, entry.IsActive);
            if (entry.IsActive) activeCtx = entry.Context;
        }

        AttachActiveRibbon(activeCtx);

        Entries.Clear();
        foreach (var entry in AllEntries)
        {
            if (!entry.IsActive)
                Entries.Add(entry);
        }

        RefreshStackedEntries();
        OnPropertyChanged(nameof(HasOtherArrangements));
        OnPropertyChanged(nameof(CanSolo));
    }

    /// <summary>
    /// The active (main) timeline gets the user's persisted ribbon-row
    /// preferences; stacked rows hide lyrics + per-measure metadata (both
    /// redundant there — the main timeline shows them at the same scroll
    /// position) while their T/S/P stay under per-row control.
    /// </summary>
    private void ApplyActivationDefaults(TrackEditorContext ctx, bool isActive)
    {
        if (isActive)
        {
            ctx.Ribbon.ShowTones = _appConfig.RibbonShowTones;
            ctx.Ribbon.ShowSections = _appConfig.RibbonShowSections;
            ctx.Ribbon.ShowPhrases = _appConfig.RibbonShowPhrases;
            ctx.Ribbon.ShowLyrics = _appConfig.RibbonShowLyrics;
            ctx.Ribbon.ShowMeasureMetadata = _appConfig.RibbonShowMeasureInfo;
        }
        else
        {
            ctx.Ribbon.ShowLyrics = false;
            ctx.Ribbon.ShowMeasureMetadata = false;
        }
    }

    /// <summary>
    /// Persists the main-timeline row toggles: while a ribbon is the active
    /// one, every user toggle is written straight to the app config, so the
    /// preferred rows survive arrangement switches and restarts.
    /// </summary>
    private void AttachActiveRibbon(TrackEditorContext? activeCtx)
    {
        if (activeCtx == null) return;
        _activeRibbon = activeCtx.Ribbon;
        _activeRibbon.PropertyChanged += OnActiveRibbonChanged;
    }

    private void DetachActiveRibbon()
    {
        if (_activeRibbon == null) return;
        _activeRibbon.PropertyChanged -= OnActiveRibbonChanged;
        _activeRibbon = null;
    }

    private void OnActiveRibbonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activeRibbon is not { } ribbon) return;
        _appConfig.RibbonShowTones = ribbon.ShowTones;
        _appConfig.RibbonShowSections = ribbon.ShowSections;
        _appConfig.RibbonShowPhrases = ribbon.ShowPhrases;
        _appConfig.RibbonShowLyrics = ribbon.ShowLyrics;
        _appConfig.RibbonShowMeasureInfo = ribbon.ShowMeasureMetadata;
        _configService.SaveAppConfig(_appConfig);
    }

    private void OnEntryRequestRefresh() => OnPropertyChanged(nameof(VisibleGhostCount));

    /// <summary>
    ///     Re-reads every row's NAME and TUNING from its arrangement.
    ///     <para>
    ///         <see cref="RefreshActiveFlags" /> is not enough on its own: it
    ///         rebuilds the non-active <see cref="Entries" /> filter, which
    ///         happens to re-bind those containers, but <see cref="AllEntries" />
    ///         — the collection the Tracks popover's PICKER binds, the one that
    ///         has to include the row you are already on — is left in place, so
    ///         its rows would keep a name the arrangement does not have.
    ///     </para>
    /// </summary>
    private void RefreshArrangementMetadata()
    {
        foreach (var entry in AllEntries) entry.NotifyArrangementMetadataChanged();
    }
}

public partial class MultiTrackEntry : ViewModelBase
{
    private readonly IGhostArrangementsService _ghostService;
    private readonly IStackedArrangementsService _stackedService;
    private readonly IGlobalEditingCoordinator _editingCoordinator;
    private readonly ProjectState _projectState;
    private readonly IAppLogger _logger;
    private bool _isActive;

    public MultiTrackEntry(
        Arrangement arrangement,
        TrackEditorContext context,
        bool isActive,
        IGhostArrangementsService ghostService,
        IStackedArrangementsService stackedService,
        IGlobalEditingCoordinator editingCoordinator,
        ProjectState projectState,
        IAppLogger logger)
    {
        // a row over missing services would construct and then throw from
        // its getters (the UI sweep built one reflectively and logged a binding
        // NullReferenceException on IsGhostVisible). Refused here, where the
        // fault is, instead.
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ghostService);
        ArgumentNullException.ThrowIfNull(stackedService);
        ArgumentNullException.ThrowIfNull(editingCoordinator);
        ArgumentNullException.ThrowIfNull(projectState);
        ArgumentNullException.ThrowIfNull(logger);
        Arrangement = arrangement;
        Context = context;
        _isActive = isActive;
        _ghostService = ghostService;
        _stackedService = stackedService;
        _editingCoordinator = editingCoordinator;
        _projectState = projectState;
        _logger = logger;
    }

    public event Action? RequestRefresh;

    public Arrangement Arrangement { get; }

    public TrackEditorContext Context { get; }

    public RibbonVisibility Ribbon => Context.Ribbon;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotActive));
        }
    }

    public bool IsNotActive => !IsActive;

    public string Name => Arrangement.Name;

    public bool IsGhostVisible
    {
        get => _ghostService.IsVisible(Arrangement);
        set
        {
            if (value == IsGhostVisible) return;
            _ghostService.SetVisible(Arrangement, value);
            OnPropertyChanged();
            RequestRefresh?.Invoke();
        }
    }

    public bool IsStackVisible
    {
        get => _stackedService.IsStacked(Arrangement);
        set
        {
            if (value == IsStackVisible) return;
            _stackedService.SetStacked(Arrangement, value);
            // Service.Changed fires NotifyStackVisibilityChanged on every entry,
            // so we don't raise PropertyChanged here directly.
        }
    }

    public void NotifyStackVisibilityChanged() => OnPropertyChanged(nameof(IsStackVisible));

    /// <summary>
    ///     The arrangement behind this row was renamed or retuned in place.
    ///     <see cref="Name" /> reads straight off it and the tuning cell binds
    ///     <see cref="Arrangement" /> through <c>ArrangementTuningConverter</c>,
    ///     and an <c>Arrangement</c> raises nothing of its own — so without this
    ///     the row keeps whatever it said when it was built.
    /// </summary>
    public void NotifyArrangementMetadataChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Arrangement));
    }

    [RelayCommand]
    private void SendSelectionHere()
    {
        _logger.Info(AppLogCategory.Editing, $"Send selection to arrangement '{Arrangement.Name}' invoked.");
        _editingCoordinator.SendSelectionToArrangement(Arrangement);
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (IsActive) return;
        _logger.Info(AppLogCategory.Project, $"Switching active arrangement to '{Arrangement.Name}' from stacked view.");
        _projectState.ActiveArrangement = Arrangement;
    }

    // `ToggleGhostCommand` is DELETED (0.58): both surfaces that show a ghost
    // toggle bind the PROPERTY two-way — `StackedTracksView`'s `ToggleButton
    // IsChecked="{Binding IsGhostVisible}"` (Avalonia's `IsChecked` is TwoWay by
    // default) and `TracksPopoverView`'s `CheckBox ... Mode=TwoWay`.
    //
    // NOTE for whoever reads this next: the command carried an `if (IsActive)
    // return;` guard that `IsGhostVisible`'s setter does not, and NEITHER surface
    // disables the toggle on the active row — so the guard has never actually run
    // in the product, and ghosting the ACTIVE arrangement is reachable today.
    // Deleting the command does not change that either way. If the guard is meant
    // to hold it belongs on a `CanToggleGhost` bound to `IsEnabled`, not in a
    // silent setter refusal: a two-way binding whose setter ignores the write
    // leaves the checkbox drawn checked over a model that is not.
}
