using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The tone-preset browser that sits under the pedalboard: a category rail
/// (All → each <see cref="DspToneCategory"/> → My Tones) filtering a grid of
/// preset cards, plus the save/overwrite/delete controls over the player's user
/// profiles. Clicking a card applies it onto the live chain via
/// <see cref="IDspTonePresetService.ApplyToPlayer"/> — on the rig's SELECTED
/// seat — and asks the owning pedalboard to
/// re-sync its pedals (<c>onApplied</c>). Saves snapshot the CURRENT live rig
/// (supplied by <c>captureRig</c>) so "apply → tweak knobs → Save as new" works.
/// The pure category/search filtering lives in <see cref="PresetBrowserFilter"/>;
/// this VM is the stateful shell and is unit-tested against a fake service.
/// </summary>
public sealed partial class PresetBrowserViewModel : ObservableObject
{
    private readonly IDspTonePresetService _presets;
    private readonly Func<DcRigPreset> _captureRig;
    private readonly Action _onApplied;

    /// <summary>
    ///     Which SEAT an apply lands on. Every seat has its own
    ///     chain (ABI v28) and the rig's seat picker repoints the board, but until
    ///     0.61 this browser called the seat-less <see cref="IDspTonePresetService.Apply"/>
    ///     — so a card click put player 1's tone on seat 0 while player 2's board
    ///     was on screen, the exact wrong-guitar shape the SCENE strip's compare
    ///     must avoid. The owning pedalboard hands in <c>() =&gt; SelectedPlayer</c>;
    ///     preset-only tests omit it and get seat 0, which is what
    ///     <see cref="IDspTonePresetService.Apply"/> always meant.
    /// </summary>
    private readonly Func<int> _selectedPlayer;

    /// <summary>Coalesces the search box's per-keystroke refresh into one pass
    /// (the <c>CoalescedFilterPass</c> rule: a burst of keystrokes against a
    /// non-virtualized 83-card grid may not rebuild it once per key). Discrete
    /// gestures — a rail click — go through <c>RunImmediately</c>. The ctor's
    /// <c>searchPost</c> is its scheduler: the product passes the UI
    /// dispatcher's background post; tests omit it and the pass runs
    /// synchronously.</summary>
    private readonly Shell.CoalescedFilterPass _searchPass;

    /// <summary>
    ///     Takes the preset apply OFF the UI thread. Every <c>RemoveNode</c> and
    ///     <c>MoveNode</c> blocks its caller in the native bypass crossfade
    ///     (<c>DspChain::fadeOutAndWait</c>, ≈47 ms at 512/48 kHz), and an
    ///     eight-module swap is on the order of sixteen of them — a card click
    ///     would freeze the UI for hundreds of milliseconds against a 150 ms
    ///     budget. The queue also single-flights per seat with last-writer-wins,
    ///     so skimming the grid costs one apply of the tone you stop on.
    ///     <para>The ctor's <c>applyWorker</c> is its scheduler, symmetric with
    ///     <c>searchPost</c>: the product passes <c>Task.Run</c> plus the UI
    ///     dispatcher's post; tests omit both and the apply runs inline.</para>
    /// </summary>
    private readonly ToneApplyQueue _applyQueue;

    /// <summary>The OS file pickers, for the tone-file exchange below. Null in a
    /// host with no window (preset-only tests, headless) — both commands then
    /// report themselves unavailable rather than half-running.</summary>
    private readonly DotCharter.Tone.Infrastructure.Storage.IFileStorageService? _files;

    /// <summary>Reads and writes the tone files the pickers above choose.</summary>
    private readonly IExportTargetWriter _exportFiles;

    /// <summary>Module type id → the catalogue's display name, for a card's chain
    /// line ("Compressor → Overdrive → Legacy Amp"). Identity when the host has no
    /// chain service: an id this build cannot name is still information.</summary>
    private readonly Func<string, string> _moduleName;
    private readonly ILocalizationService _loc;

    /// <summary>The words the browser prints; its context menu reads them too.</summary>
    public ILocalizationService Localization => _loc;

    public PresetBrowserViewModel(
        IDspTonePresetService presets, ILocalizationService localization, Func<DcRigPreset> captureRig, Action onApplied,
        NamCaptureBrowserViewModel? namBrowser = null,
        Action<Action>? searchPost = null,
        Func<int>? selectedPlayer = null,
        Action<Action>? applyWorker = null,
        Action<Action>? applyPostBack = null,
        IAppLogger? logger = null,
        DotCharter.Tone.Infrastructure.Storage.IFileStorageService? files = null,
        // The design panel at the top of the drawer, built by the HOST from the
        // seams below (ToneDesignHostSeams). Null: no panel (the standalone rig).
        Func<ToneDesignHostSeams, object?>? toneDesignPanel = null,
        Func<string, string>? moduleDisplayName = null,
        IExportTargetWriter? exportFiles = null,
        // The rig tuner's tuning, handed to the design panel for every request.
        Func<string?>? currentTuning = null,
        // The rig's setlists: a rail entry of their own when supplied.
        RigSetlistsViewModel? setlists = null)
    {
        Setlists = setlists;
        _loc = localization;
        _exportFiles = exportFiles ?? new ExportTargetWriter();
        _moduleName = moduleDisplayName ?? (static id => id);
        _presets = presets;
        _captureRig = captureRig;
        _onApplied = onApplied;
        _files = files;
        _selectedPlayer = selectedPlayer ?? (static () => 0);
        NamBrowser = namBrowser;
        _searchPass = new Shell.CoalescedFilterPass(RefreshCards, searchPost ?? (action => action()));
        _applyQueue = new ToneApplyQueue(presets, (_, _) => _onApplied(), applyWorker, applyPostBack, logger);
        ToneDesign = toneDesignPanel?.Invoke(new ToneDesignHostSeams(
            ApplyPreset,
            SnapshotLiveTone,
            SaveDesignedTone,
            moduleDisplayName,
            applyPostBack,
            logger,
            _loc));
        if (ToneDesign is ToneDesignCoreViewModel design) design.TuningProvider = currentTuning;

        SaveCategories = Enum.GetValues<DspToneCategory>()
            .Select(c => new PresetCategoryOption(c, CategoryLabel(_loc, c)))
            .ToList();
        _selectedSaveCategory = SaveCategories[0];

        BuildRail();
        SelectedRail = Rail.Count > 0 ? Rail[0] : null;
    }

    /// <summary>
    ///     The "Design a tone" panel that sits at the TOP of this drawer, or null
    ///     when the host supplies none. The host's factory builds it (the panel is
    ///     DotCharter.Tone.UI's <c>ToneDesignViewModel</c>, which this assembly cannot
    ///     name) exactly once, here, because it drives the drawer's own
    ///     seams — <see cref="ApplyPreset" />, <see cref="SnapshotLiveTone" /> and
    ///     <see cref="SaveDesignedTone" /> — so an AI-designed tone reaches the
    ///     chain and the library by exactly the paths a card click and the
    ///     save-as dialog already use. See
    ///     <c>docs/agents/ui/pedalboard/ai-tone-design.md</c>.
    /// </summary>
    public object? ToneDesign { get; }

    /// <summary>
    ///     <see cref="ToneDesign" /> as the shared core type both hosts build (or
    ///     derive from), so the Tones view can bind the panel's open/closed state
    ///     — the designer column folds away while it is closed and the library
    ///     takes its width. Null when the host supplies no panel.
    /// </summary>
    public ToneDesignCoreViewModel? Designer => ToneDesign as ToneDesignCoreViewModel;

    /// <summary>
    ///     The LIVE chain wrapped as a USER-origin preset — the restore point the
    ///     design panel's Discard puts back. User origin is load-bearing for the
    ///     same reason the A/B compare slots use it: the factory apply path
    ///     loudness-normalizes, and a restore that comes back re-levelled has not
    ///     restored what the player was hearing.
    /// </summary>
    internal DspTonePreset SnapshotLiveTone() => new(
        _activePreset?.Id ?? string.Empty,
        _activePreset?.Name is { Length: > 0 } n ? n : _loc["PresetBrowser_DefaultName"],
        _activePreset?.Category ?? DspToneCategory.Clean,
        _activePreset?.Description ?? string.Empty,
        _captureRig(),
        DspToneOrigin.User);

    /// <summary>
    ///     Saves a DESIGNED rig into the player's library. It goes through
    ///     <see cref="IDspTonePresetService.SaveUser" /> like every other user
    ///     tone — the rig comes from the design rather than from
    ///     <c>captureRig</c>, which is the only difference from
    ///     <see cref="ConfirmSave" /> — and then rebuilds the picker, jumps to My
    ///     Tones and selects the new card, so the tone the player just saved is
    ///     visibly IN the library rather than merely written to disk.
    /// </summary>
    internal DspTonePreset SaveDesignedTone(
        string name, DspToneCategory category, string description, DcRigPreset rig)
    {
        var cleanName = string.IsNullOrWhiteSpace(name)
            ? _loc["PresetBrowser_DefaultName"]
            : name.Trim();
        var saved = _presets.SaveUser(cleanName, category, description?.Trim() ?? string.Empty, rig);
        SelectUserRail();
        RefreshCards();
        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
        return saved;
    }

    /// <summary>The left category rail (All, the eight tone categories, My Tones,
    /// and — when TONE3000 is wired — NAM Captures).</summary>
    public ObservableCollection<PresetRailRow> Rail { get; } = [];

    /// <summary>The TONE3000 NAM-capture section (null when the online services
    /// are not wired, e.g. in preset-only tests — the rail entry is omitted then).</summary>
    public NamCaptureBrowserViewModel? NamBrowser { get; }

    /// <summary>True while the NAM Captures rail entry is selected: the view
    /// swaps the search+card grid for the capture panel.</summary>
    public bool IsNamCapturesSelected => SelectedRail?.Kind == PresetFilterKind.NamCaptures;

    /// <summary>
    ///     The card grid and its empty state, gated so the NAM panel can take the
    ///     column instead. Computed here rather than composed in markup because a
    ///     binding cannot AND two properties, and the alternative — wrapping both
    ///     in a `Panel` — re-indents forty lines of the drawer for nothing.
    ///     <para>
    ///         <b>Both must exist.</b> `PresetBrowserFilter` returns NO cards for
    ///         `PresetFilterKind.NamCaptures` by design, so without the second one
    ///         the preset EMPTY STATE would draw on top of the capture panel —
    ///         which is exactly the "no cards here" message that made the missing
    ///         panel look like an empty library rather than a missing feature.
    ///     </para>
    /// </summary>
    public bool ShowCards => HasCards && !IsNamCapturesSelected && !IsSetlistsSelected;

    /// <inheritdoc cref="ShowCards" />
    public bool ShowNoCards => !HasCards && !IsNamCapturesSelected && !IsSetlistsSelected;

    /// <summary>The head's tone count is shown while the grid (or its empty
    /// state) is — a search that matched nothing still says 0. With the setlist
    /// or NAM panel in the grid's place it counted cards nobody could see and
    /// read "Tones 0".</summary>
    public bool ShowsToneCount => !IsNamCapturesSelected && !IsSetlistsSelected;

    /// <summary>The rig's setlists (null in a host that has none — the rail entry
    /// is then absent, like the NAM section's).</summary>
    public RigSetlistsViewModel? Setlists { get; }

    /// <summary>True while the Setlists rail entry is selected: the setlist panel
    /// takes the card grid's place (the NAM section's rule, and for the same
    /// reason <see cref="ShowCards" /> and <see cref="ShowNoCards" /> both yield).</summary>
    public bool IsSetlistsSelected => SelectedRail?.Kind == PresetFilterKind.Setlists;

    /// <summary>True while a search is narrowing the grid — the empty state then
    /// offers to clear it, because "nothing here" is the search's doing, not the
    /// library's.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    ///     The empty grid's sentence, chosen for WHY it is empty: a search that
    ///     matched nothing names the search, an empty My Tones says how a tone gets
    ///     there, and anything else is the plain "nothing here" line. One generic
    ///     sentence for all three read as a broken library when it was a typo.
    /// </summary>
    public string EmptyText =>
        IsSearching
            ? _loc.Format("PresetBrowser_NoMatch", SearchText.Trim())
            : SelectedRail?.Kind == PresetFilterKind.User
                ? _loc["PresetBrowser_EmptyUser"]
                : _loc["PresetBrowser_Empty"];

    /// <summary>Empties the search box — the empty state's way out when a search
    /// matched nothing. Runs the refresh at once: it is a discrete gesture, not
    /// typing.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        _searchPass.RunImmediately();
    }

    /// <summary>The preset cards shown for the current rail + search.</summary>
    public ObservableCollection<PresetCardViewModel> Cards { get; } = [];

    /// <summary>True when the current rail + search yields at least one card
    /// (drives the browser's empty-state toggle).</summary>
    public bool HasCards => Cards.Count > 0;

    [ObservableProperty] private PresetRailRow? _selectedRail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePresetName))]
    [NotifyPropertyChangedFor(nameof(CanOverwrite))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private PresetCardViewModel? _selectedCard;

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>The last preset actually applied (via a card, prev/next or the
    /// designer's Hear it) — the authoritative "active tone", independent of which
    /// rail the card grid is currently showing.</summary>
    private DspTonePreset? _activePreset;

    /// <summary>
    ///     The last preset actually applied, rig and all — the baseline the Amp
    ///     &amp; FX rig screen's "EDITED" badge compares the live chain against
    ///     (<see cref="RigEditState" />). Null until something is applied.
    /// </summary>
    internal DspTonePreset? ActivePreset => _activePreset;

    /// <summary>The applied preset's name, shown in the preset toolbar (falls back
    /// to the selected card, then a dash when nothing is active).</summary>
    public string ActivePresetName =>
        _activePreset?.Name is { Length: > 0 } a ? a
        : SelectedCard?.Name is { Length: > 0 } n ? n
        : _loc["PresetBrowser_NoPreset"];

    /// <summary>Overwrite + Delete only apply to the player's own presets.</summary>
    public bool CanOverwrite => CanChangeCard(SelectedCard);
    public bool CanDelete => CanChangeCard(SelectedCard);

    /// <summary>
    ///     Whether <paramref name="card" /> is one the player may rename,
    ///     overwrite or delete: only their OWN tones. A built-in is read-only
    ///     (the service refuses it too) — duplicate it to make it yours.
    /// </summary>
    public static bool CanChangeCard(PresetCardViewModel? card) => card?.IsUser == true;

    /// <summary>The reason Rename / Overwrite / Delete are disabled on
    /// <paramref name="card" /> — a built-in cannot be changed, duplicate it
    /// first — or null for the player's own tone, whose items are live.</summary>
    public string? LockedReason(PresetCardViewModel? card) =>
        card is { IsUser: false } ? _loc["PresetBrowser_BuiltInLocked"] : null;

    // ---- save-as dialog state -------------------------------------------------

    public IReadOnlyList<PresetCategoryOption> SaveCategories { get; }

    [ObservableProperty] private bool _isSaveDialogOpen;
    [ObservableProperty] private string _saveName = string.Empty;
    [ObservableProperty] private string _saveDescription = string.Empty;
    [ObservableProperty] private PresetCategoryOption? _selectedSaveCategory;

    /// <summary>
    ///     The user tone the save dialog is EDITING (Rename / edit details…), or
    ///     null when it saves the live rig as a new tone. One dialog, two modes:
    ///     in edit mode confirming rewrites that preset's name, description and
    ///     category under its own id and with its OWN rig — never the live one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingDetails))]
    [NotifyPropertyChangedFor(nameof(SaveDialogTitle))]
    [NotifyPropertyChangedFor(nameof(SaveDialogConfirmText))]
    private DspTonePreset? _editTarget;

    /// <summary>True while the save dialog edits an existing tone's details.</summary>
    public bool IsEditingDetails => EditTarget != null;

    /// <summary>The save dialog's title for its mode.</summary>
    public string SaveDialogTitle => _loc[
        IsEditingDetails ? "PresetBrowser_EditTitle" : "PresetBrowser_SaveTitle"];

    /// <summary>The save dialog's confirm caption for its mode.</summary>
    public string SaveDialogConfirmText => _loc[
        IsEditingDetails ? "PresetBrowser_SaveChanges" : "PresetBrowser_Save"];

    // ---- delete confirmation ----------------------------------------------------

    /// <summary>
    ///     The user tone waiting for the player to confirm its deletion, or null.
    ///     Delete never removes a tone on the first click: it asks first, in an
    ///     inline confirmation drawn like the save dialog.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteConfirmOpen))]
    [NotifyPropertyChangedFor(nameof(DeleteConfirmTitle))]
    private PresetCardViewModel? _pendingDelete;

    /// <summary>True while the delete confirmation is on screen.</summary>
    public bool IsDeleteConfirmOpen => PendingDelete != null;

    /// <summary>"Delete “name”?" — the confirmation names the tone it will remove.</summary>
    public string DeleteConfirmTitle => PendingDelete is { } card
        ? _loc.Format("PresetBrowser_DeleteConfirmTitle", card.Name)
        : string.Empty;

    // ---- rail / card refresh --------------------------------------------------

    partial void OnSelectedRailChanged(PresetRailRow? value)
    {
        // A rail click is one discrete gesture: run now, and drop any pass a
        // half-typed search left pending.
        _searchPass.RunImmediately();
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsNamCapturesSelected));
        OnPropertyChanged(nameof(IsSetlistsSelected));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowNoCards));
        OnPropertyChanged(nameof(ShowsToneCount));
        if (IsNamCapturesSelected) NamBrowser?.Activate();
        if (IsSetlistsSelected) Setlists?.RefreshNames();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        _searchPass.Request();
    }


    partial void OnSelectedCardChanged(PresetCardViewModel? oldValue, PresetCardViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }

    /// <summary>Rebuilds the card grid from the service's current catalogue for the
    /// selected rail + search. Preserves the selected card by id when it survives.</summary>
    private void RefreshCards()
    {
        var keepId = SelectedCard?.Id;
        Cards.Clear();
        foreach (var row in Rail)
            row.IsSelected = ReferenceEquals(row, SelectedRail);

        var all = _presets.AllPresets;
        RefreshRailCounts(all);

        if (SelectedRail == null) { SelectedCard = null; return; }

        var filtered = PresetBrowserFilter.Filter(all, SelectedRail.Kind, SelectedRail.Category, SearchText);
        // Curated "quality-first" ordering: the marquee artist/genre tones lead the
        // grid, everything else keeps its catalogue order (stable). The pure
        // PresetCuration seam is the preset analogue of GearCuration on the rail.
        foreach (var p in PresetCuration.OrderFeaturedFirst(filtered))
            Cards.Add(new PresetCardViewModel(p, CategoryLabel(_loc, p.Category), _moduleName));

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowNoCards));
        OnPropertyChanged(nameof(EmptyText));
        SelectedCard = keepId == null ? null : Cards.FirstOrDefault(c => c.Id == keepId);
        SyncActiveCard();
    }

    /// <summary>
    ///     Each rail row's count, under the CURRENT search — so typing "fuzz"
    ///     shows at a glance which categories hold a match instead of making the
    ///     player click through twelve rows to find out. The capture and setlist
    ///     rows count nothing: neither is a preset slice.
    /// </summary>
    private void RefreshRailCounts(IReadOnlyList<DspTonePreset> all)
    {
        foreach (var row in Rail)
        {
            row.Count = row.Kind is PresetFilterKind.NamCaptures or PresetFilterKind.Setlists
                ? null
                : PresetBrowserFilter.Filter(all, row.Kind, row.Category, SearchText).Count;
        }
    }

    /// <summary>Marks the card of the tone that is actually PLAYING — the one the
    /// last apply put on the chain — so the grid can say which it is even when
    /// the selection has moved on.</summary>
    private void SyncActiveCard()
    {
        var activeId = _activePreset?.Id;
        foreach (var card in Cards)
            card.IsActive = activeId != null && card.Id == activeId;
    }

    // ---- commands -------------------------------------------------------------

    /// <summary>Applies a card's preset onto the live chain and re-syncs the board.
    /// Also selects it so overwrite/delete target it.</summary>
    [RelayCommand]
    public void ApplyCard(PresetCardViewModel? card)
    {
        if (card == null) return;
        SelectedCard = card;
        ApplyPreset(card.Preset);
    }

    /// <summary>Common apply path (card, prev/next, the designer's Hear it, or
    /// the onboarding wizard's tone page): pushes the preset onto the SELECTED
    /// SEAT's chain, re-syncs the board and marks it the active tone.</summary>
    public void ApplyPreset(DspTonePreset preset) => ApplyPresetOnScene(preset, null);

    /// <summary>A tone is on its way to the selected seat's chain (the apply queue
    /// is working): anything that would snapshot the chain must wait for it.</summary>
    internal bool IsApplying => _applyQueue.IsBusy(_selectedPlayer());

    /// <summary>
    ///     <see cref="ApplyPreset(DspTonePreset)" />, starting on scene
    ///     <paramref name="startScene" /> (a setlist entry's): the scene's values
    ///     go out in the SAME apply (<see cref="RigScenes.StartOn" />), so the song
    ///     starts on its scene without a second switch. The ACTIVE preset stays the
    ///     preset as saved, so switching scene does not read as an edit.
    /// </summary>
    public void ApplyPresetOnScene(DspTonePreset preset, int? startScene)
    {
        _activePreset = preset;
        if (startScene is { } scene)
            preset = preset with { Rig = RigScenes.StartOn(preset.Rig, scene) };
        // The name and the playing card are the click's own feedback and must
        // land in this frame; the CHAIN work goes to the queue, which is what
        // keeps the native crossfade off the UI thread. The board re-sync
        // (`_onApplied`) is the queue's completion, posted back.
        OnPropertyChanged(nameof(ActivePresetName));
        SyncActiveCard();
        _applyQueue.ApplyToPlayer(_selectedPlayer(), preset);
    }

    [RelayCommand]
    private void PrevPreset() => StepPreset(-1);

    [RelayCommand]
    private void NextPreset() => StepPreset(+1);

    private void StepPreset(int direction)
    {
        if (Cards.Count == 0) return;
        var index = SelectedCard == null ? -1 : Cards.IndexOf(SelectedCard);
        index = ((index + direction) % Cards.Count + Cards.Count) % Cards.Count;
        ApplyCard(Cards[index]);
    }

    /// <summary>Opens the inline "save as new" dialog, seeded from the active card's
    /// category so a tweak of an existing tone saves into the same bucket.</summary>
    [RelayCommand]
    private void BeginSaveAsNew()
    {
        EditTarget = null;
        SaveName = SelectedCard?.Name is { Length: > 0 } n ? n : string.Empty;
        SaveDescription = string.Empty;
        var cat = SelectedCard?.Category ?? DspToneCategory.Clean;
        SelectedSaveCategory = SaveCategories.FirstOrDefault(o => o.Value == cat) ?? SaveCategories[0];
        IsSaveDialogOpen = true;
    }

    /// <summary>
    ///     Opens the save dialog in EDIT mode over one of the player's own tones,
    ///     pre-filled with its name, description and category. Built-ins are
    ///     refused (<see cref="CanChangeCard" />).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    public void BeginEditCard(PresetCardViewModel? card)
    {
        if (!CanChangeCard(card)) return;
        EditTarget = card!.Preset;
        SaveName = card.Name;
        SaveDescription = card.Description;
        SelectedSaveCategory = SaveCategories.FirstOrDefault(o => o.Value == card.Category) ?? SaveCategories[0];
        IsSaveDialogOpen = true;
    }

    [RelayCommand]
    private void CancelSave() => IsSaveDialogOpen = false;

    /// <summary>However the dialog closes — Cancel, confirm, or a host's Esc
    /// writing the flag directly — it leaves edit mode, so the next "Save as
    /// new…" cannot open still pointed at the last tone that was renamed.</summary>
    partial void OnIsSaveDialogOpenChanged(bool value)
    {
        if (!value) EditTarget = null;
    }

    /// <summary>Commits the save dialog. Save-as-new snapshots the live rig into
    /// a NEW user preset, then jumps to My Tones and selects it; in edit mode it
    /// rewrites the edited tone's details instead (<see cref="ConfirmEdit" />).</summary>
    [RelayCommand]
    public void ConfirmSave()
    {
        var name = string.IsNullOrWhiteSpace(SaveName)
            ? _loc["PresetBrowser_DefaultName"]
            : SaveName.Trim();
        var category = (SelectedSaveCategory ?? SaveCategories[0]).Value;
        if (EditTarget is { } target)
        {
            ConfirmEdit(target, name, category);
            return;
        }

        var saved = _presets.SaveUser(name, category, SaveDescription?.Trim() ?? string.Empty, _captureRig());

        // The saved tone IS the live rig — make it the ACTIVE tone. Without this the
        // toolbar's active-tone name and the searchable dropdown keep pointing at the
        // preset that was applied BEFORE the tweak+save (they lied about the rig).
        _activePreset = saved;

        IsSaveDialogOpen = false;
        SelectUserRail();
        RefreshCards();
        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
        OnPropertyChanged(nameof(ActivePresetName));
    }

    /// <summary>
    ///     The edit half of <see cref="ConfirmSave" />: the SAME preset (its id)
    ///     with the SAME rig it was saved with — only its name, description and
    ///     category change. The live rig is not read: renaming a tone is not
    ///     overwriting it. If it is the tone on the rig, the active preset follows
    ///     the new name.
    /// </summary>
    private void ConfirmEdit(DspTonePreset target, string name, DspToneCategory category)
    {
        var saved = _presets.SaveUser(name, category, SaveDescription?.Trim() ?? string.Empty, target.Rig, target.Id);
        if (_activePreset?.Id == target.Id) _activePreset = saved;

        IsSaveDialogOpen = false;
        EditTarget = null;
        RefreshCards();
        // A new category can take the card out of the rail on screen; My Tones
        // always holds it, so the player sees what they just edited.
        if (Cards.All(c => c.Id != saved.Id))
        {
            SelectUserRail();
            RefreshCards();
        }

        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
        OnPropertyChanged(nameof(ActivePresetName));
    }

    /// <summary>
    ///     Saves a copy of <paramref name="card" />'s preset — built-in or the
    ///     player's own — into My Tones as "&lt;name&gt; (copy)" with the card's
    ///     rig, and selects it. It does NOT apply it: the rig keeps playing what
    ///     it was playing. Duplicating is how a built-in becomes editable.
    /// </summary>
    [RelayCommand]
    public void DuplicateCard(PresetCardViewModel? card)
    {
        if (card == null) return;
        var name = _loc.Format("PresetBrowser_CopyName", card.Name);
        var saved = _presets.SaveUser(name, card.Category, card.Description, card.Preset.Rig);
        SelectUserRail();
        RefreshCards();
        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
    }

    /// <summary>Replaces the selected USER preset's rig with the current live rig,
    /// keeping its id/name/category (the "update this profile" action).</summary>
    [RelayCommand(CanExecute = nameof(CanOverwrite))]
    public void Overwrite() => OverwriteCard(SelectedCard);

    /// <summary>
    ///     Replaces <paramref name="card" />'s rig with the current live rig,
    ///     keeping its id, name, category and description — the one implementation
    ///     behind both the head's Overwrite and the card menu's "Overwrite with
    ///     current rig". Built-ins are refused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    public void OverwriteCard(PresetCardViewModel? card)
    {
        if (!CanChangeCard(card)) return;
        var saved = _presets.SaveUser(card!.Name, card.Category, card.Description, _captureRig(), card.Id);
        // The overwritten profile now HOLDS the live rig — it is the active tone (and
        // the stale _activePreset instance would otherwise still carry the old rig).
        _activePreset = saved;
        RefreshCards();
        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
        OnPropertyChanged(nameof(ActivePresetName));
    }

    /// <summary>Asks to delete the selected USER preset — the head's Delete…
    /// goes through the same confirmation as the card menu's.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    public void Delete() => RequestDeleteCard(SelectedCard);

    /// <summary>
    ///     Opens the delete confirmation for <paramref name="card" />. Nothing is
    ///     deleted here: <see cref="ConfirmDelete" /> does that, and
    ///     <see cref="CancelDelete" /> leaves the tone alone. Built-ins are
    ///     refused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    public void RequestDeleteCard(PresetCardViewModel? card)
    {
        if (!CanChangeCard(card)) return;
        PendingDelete = card;
    }

    /// <summary>Deletes the tone the confirmation names, clears it as the active
    /// tone if it was, and rebuilds the grid.</summary>
    [RelayCommand]
    public void ConfirmDelete()
    {
        var card = PendingDelete;
        PendingDelete = null;
        if (!CanChangeCard(card)) return;
        _presets.DeleteUser(card!.Id);
        if (_activePreset?.Id == card.Id) _activePreset = null;
        if (SelectedCard?.Id == card.Id) SelectedCard = null;
        RefreshCards();
        OnPropertyChanged(nameof(ActivePresetName));
    }

    /// <summary>Closes the delete confirmation and keeps the tone.</summary>
    [RelayCommand]
    public void CancelDelete() => PendingDelete = null;

    // ---- tone FILES: export the live rig, import somebody else's -------------

    /// <summary>
    ///     The tone-file exchange's one status line — the outcome of the last
    ///     export or import, localized. Empty until something happens.
    ///     <para>
    ///         It exists because a bad file must SAY so: the import is throw-free
    ///         by contract (<see cref="TonePresetJsonFile.Parse" />), and a
    ///         throw-free failure with no status is a button that does nothing,
    ///         which is indistinguishable from a broken one.
    ///     </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    private string _importStatus = string.Empty;

    public bool HasImportStatus => ImportStatus.Length > 0;

    /// <summary>Whether the tone-file commands can do anything at all — false in a
    /// host with no file pickers, so the pair ships visibly disabled rather than
    /// silently inert.</summary>
    public bool CanExchangeToneFiles => _files != null;

    /// <summary>
    ///     Writes the LIVE rig to a JSON tone file through the save picker.
    ///     <para>
    ///         The live rig, not the selected card: what the player wants to send
    ///         is what they are hearing, tweaks included — the same snapshot
    ///         (<c>captureRig</c>) "Save as new" takes. Its identity comes from the
    ///         ACTIVE preset when there is one, so an exported tone keeps the name
    ///         it is known by.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExchangeToneFiles))]
    private async Task ExportActiveAsync()
    {
        if (_files is null) return;

        var name = _activePreset?.Name is { Length: > 0 } active
            ? active
            : _loc["PresetBrowser_DefaultName"];
        var preset = new DspTonePreset(
            _activePreset?.Id ?? string.Empty,
            name,
            _activePreset?.Category ?? SelectedSaveCategory?.Value ?? DspToneCategory.Clean,
            _activePreset?.Description ?? string.Empty,
            _captureRig(),
            DspToneOrigin.User);

        await ExportPresetAsync(preset);
    }

    /// <summary>Whether the card menu's Export can run: file pickers exist and a
    /// card is named.</summary>
    private bool CanExportCard(PresetCardViewModel? card) => CanExchangeToneFiles && card != null;

    /// <summary>
    ///     Writes <paramref name="card" />'s OWN preset — its saved rig, not the
    ///     live one — to a JSON tone file, through the same picker, writer and
    ///     status lines as <see cref="ExportActiveAsync" />. Any card, built-in
    ///     included: sharing a tone does not change it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportCard))]
    private async Task ExportCardAsync(PresetCardViewModel? card)
    {
        if (card == null) return;
        await ExportPresetAsync(card.Preset);
    }

    /// <summary>The export both commands share: the save picker (named after the
    /// tone), the asynchronous write and the localized outcome.</summary>
    private async Task ExportPresetAsync(DspTonePreset preset)
    {
        if (_files is null) return;
        var name = preset.Name is { Length: > 0 } n ? n : _loc["PresetBrowser_DefaultName"];
        var stem = DotCharter.Tone.Infrastructure.Storage.SafeFileName.Make(name);
        if (stem.Length == 0) stem = "tone";

        var path = await _files.SaveFilePickerAsync(
            _loc["Rig_ToneExportTitle"],
            stem + TonePresetJsonFile.FileExtension,
            ToneFileFilters());
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // Asynchronous write: the whole point of not doing file I/O between a
            // click and the next frame (UiThreadIoDeferralTests' rule).
            await _exportFiles.WriteAllTextAsync(path, TonePresetJsonFile.Serialize(preset));
            ImportStatus = _loc.Format("Rig_ToneExported", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ImportStatus = _loc["Rig_ToneExportFailed"];
        }
    }

    /// <summary>
    ///     Reads a JSON tone file, stores it as one of the player's OWN presets
    ///     and applies it.
    ///     <para>
    ///         It lands through the same <see cref="ConfirmSave" /> path a saved
    ///         tone takes (<c>SaveUser</c> → rebuild the picker → My Tones →
    ///         select), which mints a fresh id: an imported file may never
    ///         overwrite a preset the player made, whatever id it carries. It is
    ///         then applied, because "import" that does not let you hear it is a
    ///         file operation, not a tone one.
    ///     </para>
    ///     <para>
    ///         A bad file produces <see cref="ImportStatus" /> and nothing else —
    ///         no throw, no half-applied rig.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExchangeToneFiles))]
    private async Task ImportAsync()
    {
        if (_files is null) return;

        var path = await _files.OpenFilePickerAsync(
            _loc["Rig_ToneImportTitle"], ToneFileFilters());
        if (string.IsNullOrEmpty(path)) return;

        string json;
        try
        {
            json = await _exportFiles.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ImportStatus = _loc["Rig_ToneImportUnreadable"];
            return;
        }

        var result = TonePresetJsonFile.Parse(json);
        if (result.Preset is not { } imported)
        {
            ImportStatus = FaultText(result);
            return;
        }

        // A file with no name of its own is named after itself rather than after
        // the browser's placeholder — the file name IS what the player recognises.
        var name = imported.Name is { Length: > 0 } named
            ? named
            : Path.GetFileNameWithoutExtension(path);

        var saved = _presets.SaveUser(name, imported.Category, imported.Description, imported.Rig);
        _activePreset = saved;
        SelectUserRail();
        RefreshCards();
        SelectedCard = Cards.FirstOrDefault(c => c.Id == saved.Id);
        OnPropertyChanged(nameof(ActivePresetName));
        ApplyPreset(saved);
        ImportStatus = _loc.Format("Rig_ToneImported", saved.Name);
    }

    /// <summary>The localized reason an import was refused. The FAULT travels as an
    /// enum from the service layer and the words are chosen here, so nothing below
    /// the UI has to know a language.</summary>
    private string FaultText(TonePresetImport result) => result.Fault switch
    {
        TonePresetImportFault.NoModules => _loc["Rig_ToneImportEmpty"],
        TonePresetImportFault.UnknownModule => _loc.Format(
            "Rig_ToneImportUnknownModule", result.Detail ?? "?"),
        _ => _loc["Rig_ToneImportMalformed"]
    };

    private IReadOnlyList<DotCharter.Tone.Infrastructure.Storage.FilePickerFilter> ToneFileFilters() =>
        [new DotCharter.Tone.Infrastructure.Storage.FilePickerFilter(
            _loc["Rig_ToneFileFilter"], ["*" + TonePresetJsonFile.FileExtension])];

    private void SelectUserRail()
    {
        var userRow = Rail.FirstOrDefault(r => r.Kind == PresetFilterKind.User);
        if (userRow != null) SelectedRail = userRow;
    }

    // ---- rail construction + labels ------------------------------------------

    private void BuildRail()
    {
        Rail.Clear();
        Rail.Add(new PresetRailRow(
            PresetFilterKind.All, default, _loc["PresetBrowser_Cat_All"]));
        foreach (var c in Enum.GetValues<DspToneCategory>())
            Rail.Add(new PresetRailRow(PresetFilterKind.Category, c, CategoryLabel(_loc, c)));
        // The algorithmic originals get a rail entry of their own rather than
        // being hidden behind a filter: they are still every one of the tones
        // this app shipped with, and "moved into a Legacy tab" is not the same
        // thing as "hidden by default". Sits after the categories and before My
        // Tones — a shelf you can browse, not an option you have to discover.
        Rail.Add(new PresetRailRow(
            PresetFilterKind.Legacy, default, _loc["PresetBrowser_Cat_Legacy"]));
        Rail.Add(new PresetRailRow(
            PresetFilterKind.User, default, _loc["PresetBrowser_Cat_User"]));
        if (Setlists != null)
        {
            Rail.Add(new PresetRailRow(
                PresetFilterKind.Setlists, default, _loc["PresetBrowser_Cat_Setlists"]));
        }

        if (NamBrowser != null)
        {
            Rail.Add(new PresetRailRow(
                PresetFilterKind.NamCaptures, default, _loc["PresetBrowser_Cat_Nam"]));
        }
    }

    /// <summary>Player-friendly localized label for a tone category.</summary>
    public static string CategoryLabel(ILocalizationService localization, DspToneCategory category) =>
        localization[category switch
        {
            DspToneCategory.Clean => "PresetBrowser_Cat_Clean",
            DspToneCategory.Crunch => "PresetBrowser_Cat_Crunch",
            DspToneCategory.HighGain => "PresetBrowser_Cat_HighGain",
            DspToneCategory.Lead => "PresetBrowser_Cat_Lead",
            DspToneCategory.Acoustic => "PresetBrowser_Cat_Acoustic",
            DspToneCategory.Bass => "PresetBrowser_Cat_Bass",
            DspToneCategory.Ambient => "PresetBrowser_Cat_Ambient",
            DspToneCategory.Experimental => "PresetBrowser_Cat_Experimental",
            _ => "PresetBrowser_Cat_All"
        }];
}

/// <summary>One entry in the browser's category rail.</summary>
public sealed partial class PresetRailRow : ObservableObject
{
    public PresetRailRow(PresetFilterKind kind, DspToneCategory category, string label)
    {
        Kind = kind;
        Category = category;
        Label = label;
    }

    public PresetFilterKind Kind { get; }
    public DspToneCategory Category { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>How many tones this row holds under the current search; null for
    /// a row that is not a preset slice (NAM captures), which prints no count.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int? _count;

    /// <summary>The count as the rail prints it, or empty for no count.</summary>
    public string CountText => Count?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>True when the row counts and holds nothing — the rail dims it, so
    /// a search that matched only two categories reads as two lit rows.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The row as a screen reader and the ListBox's text search read it.</summary>
    public override string ToString() => Label;
}

/// <summary>One preset card: the tone's name, its category, its origin (the
/// player's own, a neural capture) and its chain, spelled out in the catalogue's
/// own words and tinted by each unit's family.</summary>
public sealed partial class PresetCardViewModel : ObservableObject
{
    public PresetCardViewModel(DspTonePreset preset, string categoryLabel, Func<string, string>? moduleName = null)
    {
        Preset = preset;
        CategoryLabel = categoryLabel;
        var name = moduleName ?? (static id => id);
        ModuleGlyphs = preset.Rig.Modules
            .Select(m => new PresetModuleGlyph(m.ModuleTypeId, name(m.ModuleTypeId)))
            .ToList();
        ChainSummary = string.Join(" → ", ModuleGlyphs.Select(g => g.Name));
    }

    public DspTonePreset Preset { get; }
    public string Id => Preset.Id;
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public bool HasDescription => Description.Length > 0;
    public DspToneCategory Category => Preset.Category;
    public bool IsUser => Preset.IsUser;
    public string CategoryLabel { get; }

    /// <summary>The preset's module chain, one entry per unit in signal order.</summary>
    public IReadOnlyList<PresetModuleGlyph> ModuleGlyphs { get; }

    /// <summary>"Compressor → Overdrive → Legacy Amp → Cabinet" — the chain the card
    /// will put on the rig, readable without applying it.</summary>
    public string ChainSummary { get; }

    /// <summary>The card the player picked (and that Overwrite / Delete act on).</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The tone the rig is PLAYING — the last one applied. Usually the
    /// selected card; not after a designed tone was auditioned over it.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>The card as a screen reader and the ListBox's text search read it.</summary>
    public override string ToString() => Name;
}

/// <summary>One unit in a preset card's chain: its type id, its display name, and
/// the Fire identity colour of its family (the same fold every rig surface uses,
/// so a drive reads as a drive on the card and on the board).</summary>
public sealed class PresetModuleGlyph
{
    public PresetModuleGlyph(string moduleTypeId, string? name = null)
    {
        ModuleTypeId = moduleTypeId;
        Name = name is { Length: > 0 } n ? n : moduleTypeId;
        TokenKey = Controls.Fire.FireUnitState.TokenKeyFor(Controls.Fire.FireRigCategory.ForModule(moduleTypeId));
    }

    public string ModuleTypeId { get; }
    public string Name { get; }

    /// <summary>The resource key of the unit's family colour
    /// (<c>FireTokenBrushConverter</c> resolves it).</summary>
    public string TokenKey { get; }
}

/// <summary>An entry in the save-dialog's category picker (enum value + label).</summary>
public sealed record PresetCategoryOption(DspToneCategory Value, string Label);
