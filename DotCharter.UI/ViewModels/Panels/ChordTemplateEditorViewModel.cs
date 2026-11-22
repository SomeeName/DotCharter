using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Panels;

public partial class ChordTemplateEditorViewModel : ViewModelBase, IChordFingerSurface
{
    // UX5: named values for the per-string Finger ComboBox. Matches the
    // tokens that FingerTextCodec accepts ("" = unspecified, T = thumb,
    // 1..4 = fingers). Exposed as a static collection so the XAML
    // binding doesn't allocate per-row.
    /// <summary>Shared InvertStrings order from the editor, so the chord box and
    /// per-string rows match timeline lanes independently of profile handedness.</summary>
    public bool FlipStrings => _editor.FlipStrings;

    public static IReadOnlyList<string> FingerOptions { get; } =
        new[] { string.Empty, "T", "1", "2", "3", "4" };

    private readonly ChartEditorViewModel _editor;
    private readonly IFingeringGenerationService _fingeringService;
    private readonly IHistoryService _historyService;
    private readonly ChordTemplateEditPlanner _edits;
    private readonly ChordApplyScopeController _scope;
    private readonly ChordFingerDraftEditor _fingerDraft;
    private readonly ChordBoxEditHandler _box;
    private readonly ChordFingeringBatch _batch;

    [ObservableProperty] private string _chordName = string.Empty;

    /// <summary>
    /// The rung of the "Apply to" ladder every edit takes. Default
    ///     "selected": what the 0.36 "this chord only" default meant, the
    ///     selection, so select-like + Save still re-fingers the whole run.
    ///     <para>
    ///         Sticky per SESSION but never persisted — a working mode like the
    ///         grid divisor and the string mask: a silently restored mode makes
    ///         the first edit of a session act differently for no visible reason.
    ///         The view model is a DI singleton, so the field gives exactly that
    ///         lifetime.
    ///     </para>
    /// </summary>
    [ObservableProperty] private ChordApplyRung _applyRung = ChordApplyRung.Selected;

    /// <summary>The edits the ladder is shown over: the Sound tab sets
    /// <see cref="ChordApplyFamily.Technique" /> over its Technique and Playback segments.</summary>
    [ObservableProperty] private ChordApplyFamily _applyFamily;

    [ObservableProperty] private bool _isArpeggio;

    /// <summary>Rocksmith's double-stop panel flag, <see cref="ChordTemplate.NopInRs" />:
    /// a named toggle rather than a "-nop" typed into the name.</summary>
    [ObservableProperty] private bool _isNop;

    /// <summary>The No name box, <see cref="ChordTemplate.Unnamed" />: checked clears the name and
    /// keeps every automatic namer off it; unchecked puts back the name it cleared, else suggests one.</summary>
    [ObservableProperty] private bool _isUnnamed;

    private readonly Dictionary<ChordTemplate, string> _namesBeforeUnnamed = new(ReferenceEqualityComparer.Instance);

    /// <summary>EOF's Fingerless flag, <see cref="ChordTemplate.Fingerless" />: the grip has no
    /// fingering on purpose. Turning it on clears the fingers; a finger set later turns it off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FingerStripTip))]
    private bool _isFingerless;

    /// <summary>The loaded grip is AUTHORED (<see cref="ChordTemplate.IsPinned" />): no automatic
    /// flow replaces or re-fingers it. Set in place, one undo node, like the hand-shape card's box.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>The pin can change: a template of the arrangement (not a display-only seed) with no
    /// ghost gems — a grip holding ghosts is pinned whatever the box says, so it is locked then.</summary>
    [ObservableProperty] private bool _canTogglePin;

    /// <summary>The finger strips' tooltip: what the strip does, or why it is disabled.</summary>
    public string FingerStripTip => Ladder.Localization[IsFingerless ? "ChordTemplate_FingerlessStripTip" : "ChordTemplate_FingerTip"];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveFingering))]
    [NotifyCanExecuteChangedFor(nameof(SaveAndNextFingeringCommand))]
    private bool _hasPendingFingerEdits;

    /// <summary>Save has work: a typed finger, or — on "+ the same grip
    /// elsewhere" — a grip fingered differently from this shape.</summary>
    public bool CanSaveFingering => Template != null && (HasPendingFingerEdits || Ladder.GripsNeedFingering);
    private bool _isUpdatingUi;
    [ObservableProperty] private Chord? _previewChord;
    [ObservableProperty] private TemplateListItem? _selectedDropdownTemplate;
    private bool _selectionUpdateQueued;
    private readonly IUiDispatcher _ui;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveFingering))]
    [NotifyCanExecuteChangedFor(nameof(SaveAndNextFingeringCommand))]
    private ChordTemplate? _template;
    [ObservableProperty] private ChordTemplate? _draftTemplate;
    private readonly IConfigurationService? _configService;
    private readonly IAppLogger? _logger;
    private readonly ProjectState? _projectState;
    private readonly HandShapeGripGrid _gripGrid;

    public ChordTemplateEditorViewModel(ChartEditorViewModel editor, IHistoryService historyService,
        IFingeringGenerationService fingeringService, ILocalizationService localization, IUiDispatcher? ui = null,
        IConfigurationService? configService = null, IAppLogger? logger = null, ProjectState? projectState = null)
    {
        _editor = editor;
        _gripGrid = new HandShapeGripGrid(editor, historyService, fingeringService);
        _historyService = historyService;
        _fingeringService = fingeringService;
        _configService = configService;
        _logger = logger;
        _projectState = projectState;
        _ui = ui ?? new Tone.UI.Platform.AvaloniaUiDispatcher();
        _edits = new ChordTemplateEditPlanner(editor, historyService, fingeringService, configService, logger);
        _scope = new ChordApplyScopeController(editor, _edits, () => Template, () => ApplyRung,
            () => configService?.LoadAppConfig().SyncHandShapeToChordTemplate != false);
        _fingerDraft = new ChordFingerDraftEditor(this);
        _box = new ChordBoxEditHandler(this, _fingerDraft, historyService, RestoreFretRunOrigin);
        Ladder = new ChordApplyLadder(localization);
        Preview = new ChordApplyPreview(editor, Ladder.Localization);
        Names = new ChordTemplateNameOffers(editor, configService);
        _batch = new ChordFingeringBatch(editor, fingeringService, historyService, this, _fingerDraft,
            arr => Names.ChordNaming(arr), () => LoadTemplate(Template!, StringInputs.Count), ShowBatchTemplate);
        ShapeEditor = new ChordShapeEditOpener(editor, () => Template, () => _scope.ThisSounds().Chords.FirstOrDefault());

        _historyService.HistoryChanged += OnHistoryChanged;

        // The flip toggle inverts the highway; the chord box and these rows have
        // to turn with it or the panel ends up a mirror image of the chart.
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChartEditorViewModel.FlipStrings)) return;
            OnPropertyChanged(nameof(FlipStrings));
            RefreshDisplayOrder();
        };

        if (_editor.Selection.SelectedChords is INotifyCollectionChanged sc)
            sc.CollectionChanged += (_, _) => QueueUpdateFromSelection();
        if (_editor.Selection.SelectedHandShapes is INotifyCollectionChanged sh)
            sh.CollectionChanged += (_, _) => QueueUpdateFromSelection();
        if (_editor.Selection.SelectedNotes is INotifyCollectionChanged sn)
            sn.CollectionChanged += (_, _) => QueueUpdateFromSelection();

        UpdateFromSelection();
    }

    /// <summary>
    ///     Re-read the panel after an undo or a redo.
    ///     <para>
    ///         It is not a bare <c>LoadTemplate(Template, …)</c>, which
    ///         refreshes the ROWS and nothing else — but a history move can ADD
    ///         or REMOVE arrangement templates (<c>CreateAndSwitchTemplateAction</c>
    ///         does both), so the dropdown would go stale, and undoing the mint that
    ///         an isolated reshape had just made would leave the panel holding a
    ///         template the arrangement does not contain while still OFFERING
    ///         it in the list.
    ///     </para>
    ///     <para>
    ///         That is not cosmetic. Picking that entry runs
    ///         <c>SwitchToTemplate</c>, which writes its <c>Id</c> onto the
    ///         selected chords and hand shapes — a dangling <c>TemplateId</c>,
    ///         which <c>SngFromDomainMapper</c> filters on
    ///         <c>TemplateIndexById.ContainsKey</c>: those chords are silently
    ///         dropped from the exported psarc. Reload from the arrangement
    ///         instead, and fall back to the selection when the loaded shape is
    ///         gone.
    ///     </para>
    /// </summary>
    private void OnHistoryChanged()
    {
        var loaded = Template;
        if (_editor.Selection.SelectedChords.Count == 0 &&
            _editor.Selection.SelectedHandShapes.FirstOrDefault() is { } shape && loaded?.Id != shape.TemplateId)
        {
            UpdateFromSelection();
            return;
        }

        if (loaded == null) return;

        var arr = _editor.ActiveArrangement;
        if (arr == null || !arr.ChordTemplates.Any(t => ReferenceEquals(t, loaded)))
        {
            UpdateFromSelection();
            return;
        }

        _isUpdatingUi = true;
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == loaded.Id);
        LoadTemplate(loaded, StringInputs.Count);
        _isUpdatingUi = false;
    }

    /// <summary>
    /// The inspector's re-sync after a selection or history move: a
    ///     selected chord that names a REAL template loads that template — so an
    ///     arpeggio tick or a rebind made elsewhere shows at once — and anything
    ///     else resolves from the selection exactly as selecting it did.
    ///     <para>
    ///         The inspector must not load a fabricated, fretless
    ///         <c>ChordTemplate { Name = "Custom" }</c> for an untemplated chord
    ///         (TemplateId -1 until the first save) or a blank one with no chord
    ///         selected. Neither carries the selection's shape, so after any
    ///         technique edit the card would read "Custom" and the ladder narrow the
    ///         selection to "selected (0)".
    ///     </para>
    /// </summary>
    public void ReloadForSelectedChord()
    {
        var arr = _editor.ActiveArrangement;
        if (arr == null)
        {
            UpdateFromSelection();
            return;
        }

        var chord = _editor.Selection.SelectedChords.FirstOrDefault();
        var named = chord == null ? null : arr.FindChordTemplate(chord.TemplateId);
        if (named == null)
        {
            UpdateFromSelection();
            return;
        }

        LoadTemplate(named, StringDigitTargeting.StringCountOf(arr));
    }

    public ObservableCollection<StringFingeringInput> StringInputs { get; } = [];

    /// <summary>
    ///     <see cref="StringInputs" /> in the order the HIGHWAY draws the lanes —
    ///     which is what the rows are bound to.
    ///     <para>
    ///         The model list stays index-ordered (0 = lowest string) because
    ///         every edit path keys off <c>StringIndex</c>; only the DISPLAY
    ///         mirrors. Unflipped the two are identical — the highway puts string
    ///         0 at the top — so this exists for the flip toggle, which has to
    ///         invert the rows with the chord box above them and the highway
    ///         above that.
    ///     </para>
    /// </summary>
    public ObservableCollection<StringFingeringInput> DisplayStringInputs { get; } = [];

    /// <summary>The name list, suggested names and containing shapes the panel offers.</summary>
    public ChordTemplateNameOffers Names { get; }
    public ObservableCollection<TemplateListItem> AvailableTemplates { get; } = [];

    public int Capo => _editor.ActiveArrangement?.Capo ?? 0;
    public IReadOnlyList<string>? TuningNames => TuningDisplayNames.For(_editor.ActiveArrangement);

    /// <summary>The "Apply to" ladder, read off the scope the edit itself is
    /// resolved on (<see cref="ChordTemplateEditPlanner.ResolveReach" />).</summary>
    public ChordApplyLadder Ladder { get; }

    /// <summary>The bulk-edit preview on the timeline and the song map.</summary>
    public ChordApplyPreview Preview { get; }

    /// <summary>Opens the Edit shape dialog on the shown shape.</summary>
    public ChordShapeEditOpener ShapeEditor { get; }

    private void RefreshScope()
    {
        var scope = _scope.CurrentScope();
        Ladder.ShowsReshapeHint = false;
        var rung = _gripGrid.IsolateSharedBar(Template, ApplyRung);
        Ladder.Update(scope, rung, DraftTemplate, ApplyFamily);
        Preview.Update(scope, rung, _scope.SelectedSounds(), ApplyFamily);
        OnPropertyChanged(nameof(FingeringTargetCount));
        OnPropertyChanged(nameof(SaveFingeringLabel));
        OnPropertyChanged(nameof(CanSaveFingering));
        SaveAndNextFingeringCommand.NotifyCanExecuteChanged();
    }

    partial void OnApplyRungChanged(ChordApplyRung value) => RefreshScope();

    partial void OnApplyFamilyChanged(ChordApplyFamily value) => RefreshScope();

    /// <summary>The chords a technique or playback edit reaches on the lit rung, resolved
    /// on the same scope as the ladder and its preview; null without a shown shape.</summary>
    internal ChordTechniqueTargets? TechniqueTargets() => _scope.CurrentScope()?.TechniqueTargets(ApplyRung);

    partial void OnDraftTemplateChanged(ChordTemplate? value)
    {
        Ladder.UpdateFingering(value);
        OnPropertyChanged(nameof(FingeringTargetCount));
        OnPropertyChanged(nameof(SaveFingeringLabel));
    }

    /// <summary>
    ///     How many sounds one press of Save re-fingers on the ladder's rung —
    ///     every chord and hand shape, the "same grip" templates' chords
    ///     included. Select-like a chord and this is the whole run; it is the
    ///     sign on the button itself that the fingering lands on more than the
    ///     chord under the caret ("there is no way to adjust the fingering for
    ///     all of them").
    /// </summary>
    public int FingeringTargetCount => Ladder.FingeringTargetCount;

    /// <summary>Caption of the Save button, carrying the live
    /// <see cref="FingeringTargetCount" /> whenever the press reaches more than
    /// one sound.</summary>
    public string SaveFingeringLabel => Ladder.SaveFingeringLabel(FingeringTargetCount);

    [RelayCommand]
    private void ChooseApplyRung(ChordApplyRung rung)
    {
        if (ApplyRung == rung) RefreshScope();
        else ApplyRung = rung;
    }

    private void QueueUpdateFromSelection()
    {
        if (_selectionUpdateQueued) return;

        _selectionUpdateQueued = true;
        _ui.Post(() =>
        {
            _selectionUpdateQueued = false;
            UpdateFromSelection();
        });
    }

    private void UpdateFromSelection()
    {
        _isUpdatingUi = true;
        OnPropertyChanged(nameof(Capo));

        UpdateAvailableTemplates();

        var arr = _editor.ActiveArrangement;
        var selection = _scope.SelectedSounds();
        var selectedChords = selection.Chords;
        var selectedHandShapes = selection.HandShapes;

        // A selected CHORD is the source of truth for the shape shown: display its
        // ACTUAL notes, never whatever template it happens to point at. Freshly
        // imported chords carry TemplateId -1 until the first save (only
        // RscpTemplateManager.RegenerateTemplates, on save, assigns them), and an
        // edit can leave a chord's notes ahead of its template. In both cases the
        // old code failed the id lookup and fell through to ChordTemplates.First(),
        // showing a completely unrelated shape (owner: a 14/15 chord read as
        // "12 12 12 12"). Resolve the chord's own template only when it genuinely
        // matches; otherwise build the display template from the chord's notes.
        if (selectedChords.Count > 0 && arr != null)
        {
            var display = ChordDisplayTemplates.Resolve(selectedChords[0], arr, _fingeringService, Names.ChordNaming);
            SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == display.Id);
            LoadTemplate(display, arr.Tuning.Length);
            _isUpdatingUi = false;
            return;
        }

        if (selectedHandShapes.Count > 0 && arr != null)
        {
            var hs = selectedHandShapes[0];
            var temp = arr.FindChordTemplate(hs.TemplateId);
            if (temp != null)
            {
                SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == temp.Id);
                LoadTemplate(temp, arr.Tuning.Length);
                ChordDisplayTemplates.OverlayNotes(hs, _editor, StringInputs);
                RefreshPreviewChord();
            }
            else
            {
                // Handshape with no/missing template — synthesise display-only frets
                // from the notes the handshape encloses so the editor reflects what
                // the user actually sees on the fretboard.
                EnsureStringInputs(arr.Tuning.Length);
                foreach (var input in StringInputs)
                {
                    input.FretText = "";
                    input.FingerText = "";
                }
                ChordName = string.Empty;
                IsArpeggio = false;
                IsNop = false;
                IsPinned = false;
                CanTogglePin = false;
                Template = null;
                Names.Clear();
                ChordDisplayTemplates.OverlayNotes(hs, _editor, StringInputs);
                RefreshPreviewChord();
            }
            _isUpdatingUi = false;
            return;
        }

        // Nothing selected — browse mode: show the first template so the user can
        // pick/edit any template through the dropdown without a selection.
        if (arr != null && arr.ChordTemplates.Any())
        {
            var temp = arr.ChordTemplates.First();
            SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == temp.Id);
            LoadTemplate(temp, arr.Tuning.Length);
        }
        else
        {
            ClearTarget(arr?.StringCount ?? 6);
        }

        RefreshScope();
        _isUpdatingUi = false;
    }

    private void ClearTarget(int stringCount)
    {
        Template = null;
        DraftTemplate = null;
        SelectedDropdownTemplate = null;
        HasPendingFingerEdits = false;
        CanTogglePin = false;
        IsPinned = false;
        IsUnnamed = false;
        IsFingerless = false;
        IsArpeggio = false;
        IsNop = false;
        ChordName = string.Empty;
        Names.Clear();
        EnsureStringInputs(stringCount);
        foreach (var input in StringInputs)
        {
            input.FretText = string.Empty;
            input.FingerText = string.Empty;
        }

        RefreshPreviewChord();
        OnPropertyChanged(nameof(CanSaveFingering));
        SaveAndNextFingeringCommand.NotifyCanExecuteChanged();
    }

    /// <summary>One row per string. The DISPLAY mirror is rebuilt whenever the
    /// count changes — growing the model list without it would leave
    /// the rows empty until the flip toggle happened to move.</summary>
    private void EnsureStringInputs(int stringCount)
    {
        while (StringInputs.Count < stringCount)
        {
            var input = new StringFingeringInput(StringInputs.Count);
            input.PropertyChanged += Input_PropertyChanged;
            StringInputs.Add(input);
        }
        while (StringInputs.Count > stringCount)
            StringInputs.RemoveAt(StringInputs.Count - 1);
        RefreshStringNames();
        if (DisplayStringInputs.Count != StringInputs.Count) RefreshDisplayOrder();
    }

    internal StringFingeringInput? AdjacentStringInput(int stringIndex, int direction)
    {
        var index = DisplayStringInputs.ToList().FindIndex(input => input.StringIndex == stringIndex);
        var next = index + Math.Sign(direction);
        return index >= 0 && next >= 0 && next < DisplayStringInputs.Count ? DisplayStringInputs[next] : null;
    }

    private void RefreshDisplayOrder()
    {
        DisplayStringInputs.Clear();
        var ordered = FlipStrings ? StringInputs.Reverse() : StringInputs.AsEnumerable();
        foreach (var input in ordered) DisplayStringInputs.Add(input);
    }

    /// <summary>
    ///     The tuning LETTER beside each row (the open string) and the
    ///     arrangement its SOUNDING pitch is measured against — same source,
    ///     same moment, so the two can never describe different arrangements.
    ///     <see cref="StringFingeringInput.PitchName" /> derives itself from the
    ///     row's own fret text, so there is nothing here to keep in step with an
    ///     edit.
    /// </summary>
    private void RefreshStringNames()
    {
        var arr = _editor.ActiveArrangement;
        var tuning = TuningDisplayNames.For(arr);
        OnPropertyChanged(nameof(TuningNames));
        for (var i = 0; i < StringInputs.Count; i++)
        {
            StringInputs[i].StringName = tuning != null && i < tuning.Length ? tuning[i] : "";
            StringInputs[i].Arrangement = arr;
            StringInputs[i].RefreshPitch();
        }
    }

    /// <summary>
    ///     The Library's pick: shows <paramref name="template" /> on the panel for browsing and for the
    ///     Edit shape dialog, even with nothing selected, and never re-templates a chord (the dropdown's switch does).
    /// </summary>
    internal void Browse(ChordTemplate template)
    {
        if (Template is { } shown && ReferenceEquals(shown, template)) return;
        _isUpdatingUi = true;
        try
        {
            UpdateAvailableTemplates();
            SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == template.Id);
            LoadTemplate(template, _editor.ActiveArrangement?.StringCount ?? StringInputs.Count);
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void UpdateAvailableTemplates()
    {
        AvailableTemplates.Clear();
        if (_editor.ActiveArrangement == null) return;
        var strings = _editor.ActiveArrangement.StringCount;
        foreach (var t in _editor.ActiveArrangement.ChordTemplates) AvailableTemplates.Add(new TemplateListItem(t, strings));
    }

    public void LoadTemplate(ChordTemplate template, int stringCount)
    {
        // Switching templates discards the in-progress finger buffer; the user
        // explicitly chose a different chord without saving.
        HasPendingFingerEdits = false;
        // SAVE AND RESTORE, never a hard `false` at the end. Every caller here
        // wraps its own `_isUpdatingUi = true` region around this, and clearing
        // the flag from inside re-armed `Input_PropertyChanged` for the rest of
        // that region. `UpdateFromSelection`'s hand-shape branch is the one that
        // bit: it loads the shape and then OVERLAYS the frets of the sounds the
        // bar encloses, so merely SELECTING a hand shape whose template declares
        // fewer strings than its sounds play pushed a real
        // EditTemplateGlobalAction — one per overlaid string — reshaping the
        // shared template and dirtying the project from a pure selection.
        var wasUpdatingUi = _isUpdatingUi;
        _isUpdatingUi = true;
        Template = null;

        EnsureStringInputs(stringCount);

        // The name list FIRST, with this template's name in it, then the name.
        // The box is a ComboBox whose SelectedItem is bound to
        // ChordName: rebuilding its ItemsSource after the name was set cleared
        // the selection, and the binding wrote that null BACK into ChordName,
        // so selecting a chord showed an empty name box.
        Names.UpdateKnownChordNames(template.Name);
        ChordName = template.Name;
        // IsArpeggio, not .Arpeggio: TWO flags spell arpeggio and each import
        // path sets only one (editing-invariants.md). Reading the SNG flag alone
        // made the checkbox report "not an arpeggio" for every RS-XML / EOF
        // arpeggio, which spells it ForceArpeggioInRs.
        IsArpeggio = template.IsArpeggio;
        IsNop = template.NopInRs;
        IsUnnamed = template.Unnamed;
        IsFingerless = template.Fingerless;
        var hasGhosts = _gripGrid.HasGhosts(template);
        IsPinned = template.IsPinned || hasGhosts;
        CanTogglePin = !hasGhosts && template.GhostMask == 0 &&
                       _editor.ActiveArrangement?.ChordTemplates.Any(t => ReferenceEquals(t, template)) == true;

        foreach (var input in StringInputs)
        {
            input.FretText = template.Frets.TryGetValue(input.StringIndex, out var f) ? f.ToString() : "";
            input.FingerText = template.Fingers.TryGetValue(input.StringIndex, out var finger)
                ? FingerTextCodec.Format(finger)
                : "";
        }

        Template = template;
        DraftTemplate = template.Clone();
        Names.Update(template);
        RefreshPreviewChord();

        OnPropertyChanged(nameof(Template));
        _isUpdatingUi = wasUpdatingUi;
    }

    private void RefreshPreviewChord()
    {
        if (Template == null)
        {
            PreviewChord = null;
            RefreshScope();
            OnPropertyChanged(nameof(MutedStrings));
            return;
        }

        var chord = new Chord { TemplateId = Template.Id };
        foreach (var kvp in Template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = kvp.Key, Fret = kvp.Value });
        PreviewChord = chord;
        OnPropertyChanged(nameof(MutedStrings));
        RefreshScope();
    }

    partial void OnSelectedDropdownTemplateChanged(TemplateListItem? value)
    {
        if (_isUpdatingUi || value == null || Template == null || value.Template.Id == Template.Id) return;
        SwitchToTemplate(value.Template);
    }

    [RelayCommand]
    private void CreateNewTemplate()
    {
        if (_editor.ActiveArrangement == null || Template == null) return;

        var newId = _editor.ActiveArrangement.ChordTemplates.Any()
            ? _editor.ActiveArrangement.ChordTemplates.Max(t => t.Id) + 1
            : 0;

        var newTemplate = Template.Clone();
        newTemplate.Id = newId;
        newTemplate.Name = string.IsNullOrEmpty(Template.Name) ? "New Chord" : Template.Name + " (Copy)";

        var targets = _scope.SwitchTargets();
        var selectedChords = targets.Chords.ToList();
        var selectedHandShapes = targets.HandShapes.ToList();

        var newChords = selectedChords.Select(c =>
        {
            var nc = c.Clone();
            nc.TemplateId = newTemplate.Id;
            var newNotes = new List<ChordNote>();
            foreach (var kvp in newTemplate.Frets)
            {
                var stringIdx = kvp.Key;
                var existingNote = nc.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIdx);
                if (existingNote != null)
                {
                    existingNote.Fret = kvp.Value;
                    newNotes.Add(existingNote);
                }
                else
                {
                    newNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = kvp.Value });
                }
            }

            nc.ChordNotes = new ObservableCollection<ChordNote>(newNotes);
            return nc;
        }).ToList();

        var newHandShapes = selectedHandShapes.Select(hs =>
        {
            var nhs = hs.Clone();
            nhs.TemplateId = newTemplate.Id;
            return nhs;
        }).ToList();

        _historyService.Do(StrandedShapes.Wrap(_editor.ActiveArrangement,
            new CreateAndSwitchTemplateAction(
                _editor.ActiveArrangement.ChordTemplates,
                newTemplate, selectedChords, newChords, selectedHandShapes,
                newHandShapes, _editor), _editor.RequestRedraw));

        _isUpdatingUi = true;
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == newTemplate.Id);
        LoadTemplate(newTemplate, StringInputs.Count);
        _isUpdatingUi = false;
    }

    /// <summary>
    ///     Picking a template in the dropdown. With a selection it moves the
    ///     ladder's sounds onto the picked template (one undo node); with nothing
    ///     selected it BROWSES to it — doing nothing
    ///     at all while the dropdown shows the pick would be wrong.
    /// </summary>
    private void SwitchToTemplate(ChordTemplate newTemplate)
    {
        var targets = _scope.SwitchTargets();
        if (targets.Count > 0)
            _edits.SwitchSelection(newTemplate, [.. targets.Chords], [.. targets.HandShapes]);

        _isUpdatingUi = true;
        LoadTemplate(newTemplate, StringInputs.Count);
        _isUpdatingUi = false;
    }

    /// <summary>
    ///     Commits <paramref name="newState" /> on the ladder's rung through
    ///     <see cref="ChordTemplateEditPlanner.ApplyEdit" />, shows the template it
    ///     answers and says on the status rail what changed; a refused edit leaves
    ///     the panel as it is.
    /// </summary>
    private bool ApplyTemplateEdit(ChordTemplate newState, ChordTemplateEditKind? kind = null)
    {
        if (Template == null || _isUpdatingUi)
        {
            return false;
        }

        var editKind = kind ?? ChordTemplateEditPlanner.KindOf(Template, newState);
        if (_gripGrid.Apply(Template, newState, editKind, ApplyRung) is { } grip)
        {
            LoadTemplate(grip, StringInputs.Count);
            UpdateAvailableTemplates();
            SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == grip.Id);
            return true;
        }

        var scope = _scope.CurrentScope();
        var rung = editKind is ChordTemplateEditKind.Shape or ChordTemplateEditKind.Fingering
            ? _gripGrid.IsolateSharedBar(Template, ApplyRung) : ApplyRung;
        var result = scope == null ? null : _edits.ApplyEdit(Template, newState, scope, rung, editKind);
        if (result == null)
        {
            if (scope is { HasSelection: false } && !FingersDictHelper.Equal(Template.Frets, newState.Frets))
                RefuseReshape(Template);
            return false;
        }

        _projectState?.SetMessage(Ladder.Describe(result.Targets));
        var shown = result.Shown;
        _isUpdatingUi = true;
        LoadTemplate(shown, StringInputs.Count);
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == shown.Id);
        _isUpdatingUi = false;
        return true;
    }

    /// <summary>
    ///     A reshape with nothing selected is refused (it would rewrite every use
    ///     of the shape). It is not refused SILENTLY, which would leave the typed fret in
    /// the box over a model that never changed: say why, and put the
    ///     rows back to what the shape really holds.
    /// </summary>
    private void RefuseReshape(ChordTemplate shape)
    {
        var wasUpdatingUi = _isUpdatingUi;
        _isUpdatingUi = true;
        foreach (var input in StringInputs)
            input.FretText = shape.Frets.TryGetValue(input.StringIndex, out var fret) ? fret.ToString() : "";
        _isUpdatingUi = wasUpdatingUi;
        Ladder.ShowsReshapeHint = true;
    }

    private void Input_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Template == null || sender is not StringFingeringInput input || _isUpdatingUi) return;

        if (e.PropertyName == nameof(StringFingeringInput.FretText))
        {
            // Frets remain auto-committed: editing a fret rewrites the chord
            // shape across all instances and is the user's primary edit verb.
            var newState = Template.Clone();
            // Snapped, never taken verbatim: the box accepts any int the user
            // can type. The TEXT is deliberately left alone — this commits per
            // keystroke, so rewriting it mid-run would turn typing "12" under a
            // capo 5 into "52". The template holds the legal fret; the box
            // re-reads it on the next load.
            if (int.TryParse(input.FretText, out var fret))
                newState.Frets[input.StringIndex] = _fingerDraft.SnapFretToNeck(fret);
            else newState.Frets.Remove(input.StringIndex);
            ApplyTemplateEdit(newState);
            return;
        }

        if (e.PropertyName == nameof(StringFingeringInput.FingerText))
        {
            // Fingerings are deferred: typing a digit updates the buffer
            // (StringInputs) only and flips a "pending" flag. The user must
            // explicitly press "Save" or "Save & Next Missing" to commit and
            // propagate. Avoids per-keystroke history spam and accidental
            // propagation when the user mistypes mid-chord.
            HasPendingFingerEdits = true;
            _fingerDraft.UpdateDraftFingersFromInputs();
        }
    }

    [RelayCommand]
    private void SaveFingerEdits() => _fingerDraft.CommitPendingFingerEdits();

    [RelayCommand(CanExecute = nameof(CanSaveFingering))]
    private void SaveAndNextFingering() => _batch.SaveAndNextFingering();

    internal void ToggleNote((int stringIdx, int fret) pos) => _box.ToggleNote(pos);

    internal void SetHoveredFret(ChordBoxFretEdit edit) => _box.SetHoveredFret(edit);

    internal void SetHoveredFinger(ChordBoxFingerEdit edit) => _box.SetHoveredFinger(edit);

    internal void ClearString(int stringIdx) => _box.ClearString(stringIdx);

    [RelayCommand]
    private void GenerateAllFingerings() => _batch.GenerateAllFingerings();

    [RelayCommand]
    private void SelectNextMissingFingering() => _batch.SelectNextMissingFingering();

    /// <summary>Bulk-sets the fingering (-1 unspecified, 0 thumb, 1..4 fingers) on the named
    /// strings and commits it, as the SetFinger keybinds do; no strings named means every string.</summary>
    public bool SetFingerForStrings(int fingerId, IReadOnlyCollection<int>? stringIndices) =>
        _fingerDraft.SetFingerForStrings(fingerId, stringIndices);

    /// <summary>Restores the shape a fret-typing run started from, when the arrangement still holds it.</summary>
    private void RestoreFretRunOrigin(ChordTemplate origin)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, origin)) return;

        var wasUpdatingUi = _isUpdatingUi;
        _isUpdatingUi = true;
        LoadTemplate(origin, StringInputs.Count);
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == origin.Id);
        _isUpdatingUi = wasUpdatingUi;
    }

    /// <summary>Shows a template the batch walk landed on, without echoing the load back as an edit.</summary>
    private void ShowBatchTemplate(ChordTemplate next)
    {
        _isUpdatingUi = true;
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == next.Id);
        LoadTemplate(next, _editor.ActiveArrangement?.Tuning.Length ?? StringInputs.Count);
        _isUpdatingUi = false;
    }

    IReadOnlyList<StringFingeringInput> IChordEditSurface.StringInputs => StringInputs;

    bool IChordEditSurface.IsUpdatingUi
    {
        get => _isUpdatingUi;
        set => _isUpdatingUi = value;
    }

    bool IChordEditSurface.ApplyTemplateEdit(ChordTemplate newState, ChordTemplateEditKind? kind) =>
        ApplyTemplateEdit(newState, kind);

    /// <summary>
    ///     Strings of the SELECTED CHORD that are fully muted — what the chord
    ///     box draws as ✕. A missing/blank template fret is NOT a mute, it is an
    ///     unused string; the two took the identical code path in the old string
    ///     table and its tooltip said otherwise.
    ///     <para>
    ///         It asked <see cref="PreviewChord" />, and that answer was
    ///         structurally always EMPTY: the preview is synthesised in
    ///         <see cref="RefreshPreviewChord" /> from <c>Template.Frets</c>
    ///         alone, and a <c>ChordTemplate</c> carries no mute — every
    ///         synthesised <c>ChordNote</c> is left at <c>MuteType.None</c>. So
    ///         the ✕ never rendered for any chord, and the property read like a
    ///         working feature. Mute is authored on the SOUND, so the sound is
    ///         what has to be asked.
    ///     </para>
    /// </summary>
    public IReadOnlyList<int> MutedStrings => FullyMutedStrings(_editor.Selection.SelectedChords.FirstOrDefault());

    /// <inheritdoc cref="MutedStrings" />
    internal static IReadOnlyList<int> FullyMutedStrings(Chord? chord) =>
        chord?.ChordNotes.Where(n => n.Mute == MuteType.Full).Select(n => n.StringIndex).ToArray()
        ?? [];

    partial void OnChordNameChanged(string value)
    {
        // A null is the name box losing its selection (its list was rebuilt),
        // never a rename the user asked for.
        if (value is null) return;
        if (Template != null && !_isUpdatingUi && Template.Name != value)
        {
            var newState = Template.Clone();
            newState.Name = value;
            // A typed "-nop" is the toggle's flag, not name text.
            RsChordDisplaySuffix.FoldNop(newState);
            ApplyTemplateEdit(newState);
        }
    }

    partial void OnIsArpeggioChanged(bool value)
    {
        if (Template == null || _isUpdatingUi || Template.IsArpeggio == value) return;

        var newState = Template.Clone();
        newState.Arpeggio = value;
        // UNCHECKING has to clear BOTH spellings or the template stays an
        // arpeggio while the box says it is not — the RS-XML reader sets only
        // ForceArpeggioInRs, so clearing the SNG flag alone changed nothing an
        // export could see. CHECKING writes the canonical flag and leaves the
        // RS-specific one exactly as it found it, so a template that already
        // carries the `-arp` display name keeps exporting under it.
        if (!value) newState.ForceArpeggioInRs = false;
        ApplyTemplateEdit(newState);
    }

    /// <summary>The toggle reaches what the "Apply to" ladder shows, like the
    /// arpeggio box: a Nop edit is its own kind and never crosses to another grip.</summary>
    partial void OnIsNopChanged(bool value)
    {
        if (Template == null || _isUpdatingUi || Template.NopInRs == value) return;

        var newState = Template.Clone();
        newState.NopInRs = value;
        ApplyTemplateEdit(newState);
    }

    partial void OnIsUnnamedChanged(bool value)
    {
        if (Template == null || _isUpdatingUi || Template.Unnamed == value) return;
        var arr = _editor.ActiveArrangement;
        var newState = Template.Clone();
        newState.Unnamed = value;
        if (value) _namesBeforeUnnamed[Template] = Template.Name;
        newState.Name = value ? string.Empty
            : _namesBeforeUnnamed.TryGetValue(Template, out var remembered) && remembered.Length > 0 ? remembered
            : arr == null ? string.Empty : NewChordNames.For(arr, Template.Frets, Names.ChordNaming(arr));
        var previousName = Template.Name;
        ApplyTemplateEdit(newState, ChordTemplateEditKind.Name);
        if (value && Template is { Unnamed: true }) _namesBeforeUnnamed[Template] = previousName;
        if (Template == null || Template.Unnamed == value) return;
        _isUpdatingUi = true;
        IsUnnamed = Template.Unnamed;
        _isUpdatingUi = false;
    }

    /// <summary>A template edit on the ladder's rung, as one undo node; a refused one puts the toggle back.</summary>
    partial void OnIsFingerlessChanged(bool value)
    {
        if (Template == null || _isUpdatingUi || Template.Fingerless == value) return;

        var newState = Template.Clone();
        newState.Fingerless = value;
        if (value) newState.Fingers.Clear();
        ApplyTemplateEdit(newState, ChordTemplateEditKind.Fingerless);
        if (Template == null || Template.Fingerless == value) return;
        _isUpdatingUi = true;
        IsFingerless = Template.Fingerless;
        _isUpdatingUi = false;
    }

    /// <summary>Pins or unpins the loaded grip in place (<see cref="SetTemplatePinnedAction" />); a box
    /// that cannot change (see <see cref="CanTogglePin" />) is put back.</summary>
    partial void OnIsPinnedChanged(bool value)
    {
        if (Template == null || _isUpdatingUi || Template.IsPinned == value) return;
        if (CanTogglePin) _historyService.Do(new SetTemplatePinnedAction(Template, value));
        if (Template == null || Template.IsPinned == value) return;
        _isUpdatingUi = true;
        IsPinned = Template.IsPinned;
        _isUpdatingUi = false;
    }

    [RelayCommand]
    private void ApplySuggestedChordName(string? value)
    {
        if (Template == null || string.IsNullOrWhiteSpace(value)) return;
        ChordName = value;
    }

    /// <summary>
    ///     Rebinds the selection onto a containing shape, so the bar arrives with
    ///     the whole grip and its fingering already authored. One undo step.
    /// </summary>
    [RelayCommand]
    private void ApplyContainingShape(ShapeSuggestion? suggestion)
    {
        var target = suggestion?.Template;
        var arr = _editor.ActiveArrangement;
        if (target == null || arr == null) return;

        var targets = _scope.SwitchTargets();
        if (targets.Count == 0) return;
        List<Chord> chords = [.. targets.Chords];
        List<HandShape> handShapes = [.. targets.HandShapes];

        _edits.SwitchSelection(target, chords, handShapes);
        _logger?.Info(AppLogCategory.Editing,
            $"Reused containing shape '{ChordTemplateEditPlanner.Show(target.Name)}' (template {target.Id}) for " +
            $"{chords.Count} chord(s) + {handShapes.Count} hand shape(s).");

        _isUpdatingUi = true;
        LoadTemplate(target, StringInputs.Count);
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == target.Id);
        _isUpdatingUi = false;
    }

    // --- CHART ACTIONS FOR UNDO/REDO ---
    // Extracted to DotCharter.UI/ViewModels/Panels/Actions/ChordTemplateActions.cs

}
