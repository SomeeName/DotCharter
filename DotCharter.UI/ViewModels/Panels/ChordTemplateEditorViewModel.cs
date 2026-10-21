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

namespace DotCharter.UI.ViewModels.Panels;

public partial class ChordTemplateEditorViewModel : ViewModelBase
{
    // UX5: named values for the per-string Finger ComboBox. Matches the
    // tokens that FingerTextCodec accepts ("" = unspecified, T = thumb,
    // 1..4 = fingers). Exposed as a static collection so the XAML
    // binding doesn't allocate per-row.
    /// <summary>Forwarded from the editor so this panel's chord box and its
    /// per-string rows follow the same flip toggle the highway does.</summary>
    public bool FlipStrings => _editor.FlipStrings;

    public static IReadOnlyList<string> FingerOptions { get; } =
        new[] { string.Empty, "T", "1", "2", "3", "4" };

    private readonly ChartEditorViewModel _editor;
    private readonly IFingeringGenerationService _fingeringService;
    private readonly IHistoryService _historyService;
    private readonly ChordTemplateEditPlanner _edits;

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
    private bool _hasPendingFingerEdits;

    /// <summary>Save has work: a typed finger, or — on "+ the same grip
    /// elsewhere" — a grip fingered differently from this shape.</summary>
    public bool CanSaveFingering => HasPendingFingerEdits || Ladder.GripsNeedFingering;
    private bool _isUpdatingUi;
    [ObservableProperty] private Chord? _previewChord;
    [ObservableProperty] private TemplateListItem? _selectedDropdownTemplate;
    private bool _selectionUpdateQueued;
    private readonly IUiDispatcher _ui;
    [ObservableProperty] private ChordTemplate? _template;
    [ObservableProperty] private ChordTemplate? _draftTemplate;
    private readonly IConfigurationService? _configService;
    private readonly IAppLogger? _logger;
    private readonly ProjectState? _projectState;

    /// <summary>
    ///     The selection this panel edits, with each sound's companion hand shape
    ///     folded in — see <see cref="AppConfig.SyncHandShapeToChordTemplate" />.
    ///     The ladder's "this chord + its hand shape" and its counts state that
    ///     fold, so it is never invisible.
    ///     <para>
    ///         Every entry point below promotes a covering hand shape, not ONLY
    ///         when nothing at all is selected (a bare NOTE). Select
    ///         a chord — which is what selecting a chord in the highway does, and
    ///         the overwhelmingly common gesture — and the bar above it would be
    ///         left out of the target list, so a fingering edit that minted a private
    ///         template would leave that bar on the old one.
    ///     </para>
    /// </summary>
    private ChordApplySounds SelectedSounds()
    {
        var chords = _editor.Selection.SelectedChords.ToList();
        var handShapes = _editor.Selection.SelectedHandShapes.ToList();

        // A bare note still finds its bar: with no chord and no shape selected,
        // the note IS the only handle the user has on the shape.
        if (chords.Count == 0 && handShapes.Count == 0)
        {
            handShapes.AddRange(SoundCompanions.CoveringHandShapes(
                _editor.Selection.SelectedNotes.Select(n => n.Position),
                _editor.HandShapes, handShapes));
        }

        if (chords.Count > 0 && SyncsHandShapes)
        {
            handShapes.AddRange(SoundCompanions.CoveringHandShapes(
                chords.Select(c => c.Position), _editor.HandShapes, handShapes));
        }

        return new ChordApplySounds(chords, handShapes);
    }

    /// <summary>The ladder's "this chord": the chord the panel shows (the first
    /// selected) with its own bar when the sync is on; else the first hand shape.</summary>
    private ChordApplySounds ThisSounds()
    {
        var selected = SelectedSounds();
        if (selected.Chords.Count == 0)
            return new ChordApplySounds([], selected.HandShapes.Take(1).ToList());

        var chord = selected.Chords[0];
        var handShapes = SyncsHandShapes
            ? SoundCompanions.CoveringHandShapes([chord.Position], _editor.HandShapes, []).ToList()
            : [];
        return new ChordApplySounds([chord], handShapes);
    }

    private bool SyncsHandShapes => _configService?.LoadAppConfig().SyncHandShapeToChordTemplate != false;

    public ChordTemplateEditorViewModel(ChartEditorViewModel editor, IHistoryService historyService,
        IFingeringGenerationService fingeringService, ILocalizationService localization, IUiDispatcher? ui = null,
        IConfigurationService? configService = null, IAppLogger? logger = null, ProjectState? projectState = null)
    {
        _editor = editor;
        _historyService = historyService;
        _fingeringService = fingeringService;
        _configService = configService;
        _logger = logger;
        _projectState = projectState;
        _ui = ui ?? new Tone.UI.Platform.AvaloniaUiDispatcher();
        _edits = new ChordTemplateEditPlanner(editor, historyService, fingeringService, configService, logger);
        Ladder = new ChordApplyLadder(localization);
        Preview = new ChordApplyPreview(editor, Ladder.Localization);
        Names = new ChordTemplateNameOffers(editor, configService);

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
            LoadTemplate(new ChordTemplate(), 6);
            return;
        }

        var chord = _editor.Selection.SelectedChords.FirstOrDefault();
        var named = chord == null ? null : arr.ChordTemplates.FirstOrDefault(t => t.Id == chord.TemplateId);
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
    public IReadOnlyList<string>? TuningNames => _editor.ActiveArrangement?.Tuning;

    /// <summary>The "Apply to" ladder, read off the scope the edit itself is
    /// resolved on (<see cref="ChordTemplateEditPlanner.ResolveReach" />).</summary>
    public ChordApplyLadder Ladder { get; }

    /// <summary>The bulk-edit preview on the timeline and the song map.</summary>
    public ChordApplyPreview Preview { get; }

    private ChordApplyScope? CurrentScope() =>
        Template == null ? null : _edits.ResolveReach(Template, ThisSounds(), SelectedSounds());

    private void RefreshScope()
    {
        var scope = CurrentScope();
        Ladder.ShowsReshapeHint = false;
        Ladder.Update(scope, ApplyRung, DraftTemplate, ApplyFamily);
        Preview.Update(scope, ApplyRung, SelectedSounds(), ApplyFamily);
        OnPropertyChanged(nameof(FingeringTargetCount));
        OnPropertyChanged(nameof(SaveFingeringLabel));
        OnPropertyChanged(nameof(CanSaveFingering));
    }

    partial void OnApplyRungChanged(ChordApplyRung value) => RefreshScope();

    partial void OnApplyFamilyChanged(ChordApplyFamily value) => RefreshScope();

    /// <summary>The chords a technique or playback edit reaches on the lit rung, resolved
    /// on the same scope as the ladder and its preview; null without a shown shape.</summary>
    internal ChordTechniqueTargets? TechniqueTargets() => CurrentScope()?.TechniqueTargets(ApplyRung);

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
        var selection = SelectedSounds();
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
            var display = ResolveChordDisplayTemplate(selectedChords[0], arr);
            SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == display.Id);
            LoadTemplate(display, arr.Tuning.Length);
            _isUpdatingUi = false;
            return;
        }

        if (selectedHandShapes.Count > 0 && arr != null)
        {
            var hs = selectedHandShapes[0];
            var temp = arr.ChordTemplates.FirstOrDefault(t => t.Id == hs.TemplateId);
            if (temp != null)
            {
                SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == temp.Id);
                LoadTemplate(temp, arr.Tuning.Length);
                OverlayNotesInHandshape(hs);
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
                OverlayNotesInHandshape(hs);
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

        RefreshScope();
        _isUpdatingUi = false;
    }

    /// <summary>
    /// The template to DISPLAY for a selected chord. Returns the chord's own
    /// template when it exists and its frets match the chord's notes; otherwise
    /// reflects the chord's ACTUAL notes — reusing an equivalent real template
    /// (its name + fingering win) or synthesising a display-only template
    /// (Id -1) with a suggested fingering and name so the finger column and the
    /// 3D preview agree. The synthesised template is never added to the
    /// arrangement here — reconciliation happens on the first edit
    /// (<c>ChordTemplateEditPlanner</c>), so merely selecting a
    /// still-untemplated chord neither dirties the project nor bloats the list.
    /// </summary>
    private ChordTemplate ResolveChordDisplayTemplate(Chord chord, Arrangement arr)
    {
        var assigned = arr.ChordTemplates.FirstOrDefault(t => t.Id == chord.TemplateId);
        if (assigned != null && !assigned.IsArpeggio && ChordTemplateEditPlanner.TemplateMatchesChord(assigned, chord))
            return assigned;

        var seed = new ChordTemplate { Id = -1, Name = string.Empty };
        foreach (var cn in chord.ChordNotes)
            seed.Frets[cn.StringIndex] = cn.Fret;

        var equiv = _fingeringService.FindEquivalentTemplate(arr, seed);
        if (equiv != null) return equiv;

        ArrangementFingeringStyle.SetSuggestedFingers(arr, seed, arr.StringCount);
        seed.Name = ChordNameSuggester.SuggestFirstChordName(arr, seed.Frets);
        return seed;
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
        var tuning = arr?.Tuning;
        for (var i = 0; i < StringInputs.Count; i++)
        {
            StringInputs[i].StringName = tuning != null && i < tuning.Length ? tuning[i] : "";
            StringInputs[i].Arrangement = arr;
        }
    }

    private void OverlayNotesInHandshape(HandShape hs)
    {
        // Walk every note + chord-note inside the handshape's range and make
        // sure each (string, fret) pair shows up on the fretboard preview. The
        // template might be empty (handshape created via right-click) or hold
        // only a subset; we never overwrite explicit template frets.
        bool InRange(FractionalPosition p) => p >= hs.Position && p < hs.EndPosition;

        foreach (var n in _editor.Notes.Where(n => InRange(n.Position)))
            ApplyOverlayFret(n.StringIndex, n.Fret);

        foreach (var ch in _editor.Chords.Where(c => InRange(c.Position)))
        {
            foreach (var cn in ch.ChordNotes)
                ApplyOverlayFret(cn.StringIndex, cn.Fret);
        }

        RefreshPreviewChord();
    }

    private void ApplyOverlayFret(int stringIndex, int fret)
    {
        if (stringIndex < 0 || stringIndex >= StringInputs.Count) return;
        var input = StringInputs[stringIndex];
        if (!string.IsNullOrEmpty(input.FretText)) return;
        input.FretText = fret.ToString();
    }

    private void UpdateAvailableTemplates()
    {
        AvailableTemplates.Clear();
        if (_editor.ActiveArrangement == null) return;
        foreach (var t in _editor.ActiveArrangement.ChordTemplates) AvailableTemplates.Add(new TemplateListItem(t));
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
        IsFingerless = template.Fingerless;
        IsPinned = template.IsPinned;
        CanTogglePin = template.GhostMask == 0 &&
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

        var targets = SwitchTargets();
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

        _historyService.Do(new CreateAndSwitchTemplateAction(
            _editor.ActiveArrangement.ChordTemplates,
            newTemplate, selectedChords, newChords, selectedHandShapes,
            newHandShapes, _editor));

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
        var targets = SwitchTargets();
        if (targets.Count > 0)
            _edits.SwitchSelection(newTemplate, [.. targets.Chords], [.. targets.HandShapes]);

        _isUpdatingUi = true;
        LoadTemplate(newTemplate, StringInputs.Count);
        _isUpdatingUi = false;
    }

    /// <summary>The sounds a template switch moves: the ladder's rung, resolved
    /// for a switch (the "same grip" rung moves every use, never other grips).
    /// Empty with nothing selected.</summary>
    private ChordApplySounds SwitchTargets()
    {
        var scope = CurrentScope();
        if (scope is not { HasSelection: true }) return ChordApplySounds.None;
        var targets = scope.Resolve(ApplyRung, ChordTemplateEditKind.Switch);
        return targets.Path == ChordApplyPath.Rebind ? targets.Sounds : ChordApplySounds.None;
    }

    /// <summary>
    ///     Commits <paramref name="newState" /> on the ladder's rung through
    ///     <see cref="ChordTemplateEditPlanner.ApplyEdit" />, shows the template it
    ///     answers and says on the status rail what changed; a refused edit leaves
    ///     the panel as it is.
    /// </summary>
    private void ApplyTemplateEdit(ChordTemplate newState, ChordTemplateEditKind? kind = null)
    {
        if (Template == null || _isUpdatingUi)
        {
            return;
        }

        var scope = CurrentScope();
        var result = scope == null ? null : _edits.ApplyEdit(Template, newState, scope, ApplyRung, kind);
        if (result == null)
        {
            if (scope is { HasSelection: false } && !FingersDictHelper.Equal(Template.Frets, newState.Frets))
                RefuseReshape(Template);
            return;
        }

        _projectState?.SetMessage(Ladder.Describe(result.Targets));
        var shown = result.Shown;
        _isUpdatingUi = true;
        LoadTemplate(shown, StringInputs.Count);
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == shown.Id);
        _isUpdatingUi = false;
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

    [RelayCommand]
    private void GenerateAllFingerings()
    {
        var arrangement = _editor.ActiveArrangement;
        if (arrangement == null) return;

        var actions = ChordTemplateFingeringBackfill.Plan(arrangement, _fingeringService, _editor, Names.ChordNaming(arrangement));
        if (actions.Count == 0) return;
        _historyService.Do(new CompositeAction(actions) { LabelKey = "History_GenerateFingerings", LinkPolicyOverride = LinkPolicy.ShareFirstChanged });

        if (Template != null) LoadTemplate(Template, StringInputs.Count);
    }

    [RelayCommand]
    private void SelectNextMissingFingering()
    {
        // Auto-save any in-progress finger edits before jumping away — matches
        // the user's mental model of "save & next" tied to this single button.
        // Only TYPED ones: on "+ same grip" Save has work with nothing typed,
        // and walking on to the next shape must not copy this one's fingering.
        if (HasPendingFingerEdits) CommitPendingFingerEdits();

        var arrangement = _editor.ActiveArrangement;
        if (arrangement == null) return;

        var pending = _fingeringService.FindTemplatesNeedingFingering(arrangement);
        if (pending.Count == 0) return;

        // Skip past the current template so repeated clicks walk the whole list.
        var startId = Template?.Id ?? -1;
        var next = pending.FirstOrDefault(t => t.Id > startId) ?? pending[0];

        _isUpdatingUi = true;
        UpdateAvailableTemplates();
        SelectedDropdownTemplate = AvailableTemplates.FirstOrDefault(t => t.Template.Id == next.Id);
        LoadTemplate(next, _editor.ActiveArrangement?.Tuning.Length ?? StringInputs.Count);
        _isUpdatingUi = false;

        // Jump the timeline to the first chord/handshape that uses this template
        // so the user sees the unset chord on the highway, EOF-style.
        SeekToFirstUsage(arrangement, next);
    }

    private void SeekToFirstUsage(Arrangement arrangement, ChordTemplate template)
    {
        FractionalPosition? earliest = null;
        foreach (var level in arrangement.Levels)
        {
            foreach (var c in level.Chords)
            {
                if (c.TemplateId == template.Id && (earliest == null || c.Position < earliest))
                    earliest = c.Position;
            }

            foreach (var h in level.HandShapes)
            {
                if (h.TemplateId == template.Id && (earliest == null || h.Position < earliest))
                    earliest = h.Position;
            }
        }
        if (earliest == null) return;
        var ms = earliest.Value.GetPositionMs(_editor.Beats);
        _editor.ExecuteSeek(ms);
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
                newState.Frets[input.StringIndex] = SnapFretToNeck(fret);
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
            UpdateDraftFingersFromInputs();
        }
    }

    /// <summary>
    ///     The strings the loaded shape frets ABOVE the capo — the only strings a
    ///     fingering can be about. Empty (not null) when nothing is loaded, so an
    ///     "every string" edit onto no shape touches nothing rather than
    ///     everything.
    /// </summary>
    private HashSet<int> FrettedStringsOfLoadedShape()
    {
        var result = new HashSet<int>();
        if (Template == null) return result;
        var capo = Capo;
        foreach (var (stringIndex, fret) in Template.Frets)
        {
            if (fret > capo)
                result.Add(stringIndex);
        }

        return result;
    }

    private void UpdateDraftFingersFromInputs()
    {
        if (Template == null) return;
        // Re-clone to force binding refresh on MiniFretboardControl (which
        // reads Template.Fingers eagerly and won't observe dict mutations).
        var draft = Template.Clone();
        foreach (var input in StringInputs)
        {
            var fingerId = FingerTextCodec.Parse(input.FingerText);
            if (fingerId >= 0) draft.Fingers[input.StringIndex] = fingerId;
            else draft.Fingers.Remove(input.StringIndex);
        }
        DraftTemplate = draft;
    }

    [RelayCommand]
    private void SaveFingerEdits() => CommitPendingFingerEdits();

    /// <summary>
    /// Bulk-set the fingering value (-1 = unspecified / 0 = thumb / 1..4 = fingers)
    /// on the named strings of the currently-loaded chord template, then commit
    /// just like the user clicked "Save fingerings". Used by the
    /// global SetFinger keybinds (Inspector → ChordTemplateEditor → here).
    /// If <paramref name="stringIndices"/> is empty the change is applied to
    /// every string in the template.
    /// </summary>
    public bool SetFingerForStrings(int fingerId, IReadOnlyCollection<int>? stringIndices)
    {
        if (!SetFingerDraftForStrings(fingerId, stringIndices)) return false;
        return CommitPendingFingerEdits();
    }

    /// <summary>
    ///     Fills the finger DRAFT without committing — the buffered half of
    ///     <see cref="SetFingerForStrings" />, shared so the chord box's
    ///     hover-typing and the keybinds cannot drift apart. Returns whether
    ///     anything actually changed.
    /// </summary>
    private bool SetFingerDraftForStrings(int fingerId, IReadOnlyCollection<int>? stringIndices)
    {
        if (Template == null) return false;

        // null means EVERY string; an EMPTY collection means NONE. They are not
        // the same value, and the caller produces an empty list for two
        // opposite answers - the string-edit mask being empty ("all strings",
        // the documented convention) and the mask being SET but intersecting
        // none of this chord's strings ("no strings"). Treating them alike
        // would invert the second and write the finger to every string of the chord.
        if (stringIndices is { Count: 0 }) return false;
        // "EVERY STRING" means every string the loaded SHAPE actually frets, not
        // every row on the panel. A 6-row panel over a three-string grip wrote
        // the finger onto the three strings the chord does not play, and onto
        // any OPEN one — the first is data no export can spell (RS-XML would
        // emit `fingerN` beside `fret="-1"`) and, because the L06/L07 rule walks
        // `Frets`, is invisible to the inspector that would otherwise report it;
        // the second IS L06. A caller that NAMES a string still gets exactly the
        // string it asked for.
        //
        // …but only when SETTING one. CLEARING ("no finger", fingerId < 0) has to
        // reach every row, because the strings that need repairing are exactly
        // the ones this narrowing excludes: a finger an older build left standing
        // on an open string is an L06 the panel reports and the keybind must be
        // able to take off. Setting is guarded twice over — here, and per row
        // below — so nothing writes onto a string the shape does not fret.
        var targetSet = stringIndices != null
            ? [.. stringIndices]
            : fingerId >= 0
                ? FrettedStringsOfLoadedShape()
                : null;
        if (targetSet is { Count: 0 }) return false;

        var changed = false;
        foreach (var input in StringInputs)
        {
            if (targetSet != null && !targetSet.Contains(input.StringIndex)) continue;
            // A finger only goes on a string the grip actually FRETS. A finger on
            // an open string is inspector rule L06 (FingerSetForOpenString) and
            // "open" is fret <= capo, not fret 0; a finger on a string the shape
            // does not play at all has nothing to hold down. ToggleNote,
            // SetHoveredFret and ClearString — the three shape-authoring gestures
            // — each strip it for exactly this reason, and so does
            // ChordTemplateFingerSetter.SetSuggestedFingers; the FINGER-authoring
            // gestures did not, so SetFinger1..4 (the keybinds, with no mask =
            // "every string of the chord") and the chord box's hover-typing minted
            // the very finding the panel's own Issues tab reports. CLEARING
            // (fingerId < 0) is always allowed — that is how an existing one is
            // repaired.
            if (fingerId > 0 && !IsFretted(input.StringIndex)) continue;
            var newText = fingerId < 0 ? string.Empty : FingerTextCodec.Format(fingerId);
            if (input.FingerText == newText) continue;
            input.FingerText = newText;
            changed = true;
        }

        if (!changed) return false;

        HasPendingFingerEdits = true;
        UpdateDraftFingersFromInputs();
        return true;
    }

    /// <summary>
    ///     Does the loaded shape hold this string down? Absent from
    ///     <c>Frets</c> means the shape does not play it; a fret at or below the
    ///     capo means it sounds OPEN. Either way there is no finger on it — the
    ///     same predicate <c>TemplateAndOverlapRule</c> uses to report L06.
    /// </summary>
    private bool IsFretted(int stringIndex) =>
        Template != null && Template.Frets.TryGetValue(stringIndex, out var fret) && fret > Capo;

    /// <summary>
    ///     Snaps an authored fret onto the frets the neck actually HAS —
    ///     <c>{0} ∪ [capo, 24]</c>, the same legal set every other fret-writing
    ///     verb lands in (<see cref="SoundingPitch.SnapToNeck" />).
    ///     <para>
    ///         The chord box's POINTER path could never produce an illegal fret
    ///         (<c>ChordBoxGeometry</c> floors its window at <c>capo + 1</c> and
    ///         its open rail returns the capo), so this reads like dead defence
    ///         until you notice that the two KEYBOARD paths bypass that geometry
    ///         entirely: hover-typing a digit builds a <c>ChordBoxFretEdit</c>
    ///         straight from the accumulator, and the per-row fret box parses
    ///         whatever was typed. Typing <c>3</c> on a capo-5 chart therefore
    ///         wrote fret 3 into a SHARED template — inspector rule <c>L01</c>
    ///         (<c>NoteFretBelowCapo</c>) on every chord bound to it, from a
    ///         gesture the charter cannot see is illegal. The fret box also took
    ///         any int at all: <c>99</c> and <c>-4</c> both landed verbatim on a
    ///         24-fret neck.
    ///     </para>
    ///     <para>
    ///         Direction is always <c>0</c> (absolute): every caller here is a
    ///         typed or clicked ABSOLUTE fret, so a sub-capo value lands ON the
    ///         capo — the answer <c>ArrangementAutoFixService.FixCapoFrets</c>
    ///         gives. Only a fret-DOWN run may fall through to open, and the
    ///         panel has no such verb.
    ///     </para>
    /// </summary>
    private int SnapFretToNeck(int fret) => SoundingPitch.SnapToNeck(fret, Math.Max(0, Capo));

    private bool CommitPendingFingerEdits()
    {
        if (Template == null || !CanSaveFingering) return false;

        var newState = Template.Clone();

        foreach (var input in StringInputs)
        {
            var fingerId = FingerTextCodec.Parse(input.FingerText);
            // A finger only exists on a string the shape DECLARES. The per-row
            // finger strip is bound straight to `FingerText`, so it reaches this
            // without passing SetFingerDraftForStrings — and a finger on a
            // string with no fret is unexportable junk the L06/L07 rule cannot
            // see, because that rule walks `Frets`.
            if (fingerId >= 0 && newState.Frets.ContainsKey(input.StringIndex))
                newState.Fingers[input.StringIndex] = fingerId;
            else newState.Fingers.Remove(input.StringIndex);
        }

        // ONE undo node for the gesture, the "same grip" templates included
        // when that rung is lit (editing-invariants.md).
        ApplyTemplateEdit(newState, ChordTemplateEditKind.Fingering);

        HasPendingFingerEdits = false;
        DraftTemplate = Template?.Clone();
        return true;
    }

    [RelayCommand]
    private void ToggleNote((int stringIdx, int fret) pos)
    {
        if (Template == null || _isUpdatingUi) return;

        var newState = Template.Clone();
        var fret = SnapFretToNeck(pos.fret);

        if (newState.Frets.TryGetValue(pos.stringIdx, out var existingFret) && existingFret == fret)
        {
            newState.Frets.Remove(pos.stringIdx);
            newState.Fingers.Remove(pos.stringIdx);
        }
        else
        {
            newState.Frets[pos.stringIdx] = fret;
            // A finger on an OPEN string is inspector rule L06
            // (FingerSetForOpenString), and "open" is fret <= capo, not fret 0
            // (frets are absolute; Core's capo invariant). Clicking an open
            // string does not stamp finger 1 on it, since the primary shape-authoring
            // gesture would otherwise mint the exact finding the panel's own Issues tab
            // reports — the same "never invent a finding the editor never
            // showed" rule the phrase-anchor fixer is held to.
            // ChordTemplateFingerSetter.SetSuggestedFingers strips open-string
            // fingers for this reason; do not diverge from it.
            if (fret > Capo) newState.Fingers[pos.stringIdx] = 1;
            else newState.Fingers.Remove(pos.stringIdx);
        }

        ApplyTemplateEdit(newState);
    }

    // ---- chord-box hover gestures (0.36) ----------------------------------
    //
    // Typing over a hovered string on the chord box. These reuse the existing
    // write paths verbatim — ApplyTemplateEdit for the shape, the buffered
    // finger draft for fingering — so nothing here is a second editing route.

    /// <summary>History node the open fret-typing run produced. A run is only
    /// reopened while it is still the CURRENT state, so a digit can never undo
    /// somebody else's work (the anchoring rule the keyboard pen uses).</summary>
    private int _fretRunNodeId = -1;

    /// <summary>
    ///     Hover-typed fret. ONE typing run is ONE history node: a continuation
    ///     digit undoes the run's own node before committing the extended value,
    ///     so <c>Ctrl+Z</c> after typing "12" lands on the fret you had BEFORE
    ///     the run, never on an intermediate "fret 1". That is exactly the
    ///     granularity <c>ChartPenService</c> already applies to typed frets,
    ///     and it is what stops the per-keystroke reshape the audit found.
    /// </summary>
    [RelayCommand]
    private void SetHoveredFret(ChordBoxFretEdit edit)
    {
        if (Template == null || _isUpdatingUi) return;

        if (edit.ContinuesRun && _fretRunNodeId >= 0 && _historyService.CurrentNodeId == _fretRunNodeId &&
            _historyService.CanUndo)
        {
            _historyService.Undo();
        }

        var newState = Template.Clone();
        var fret = SnapFretToNeck(edit.Fret);
        newState.Frets[edit.StringIndex] = fret;
        // Typing the string down to an OPEN fret leaves whatever finger was on
        // it standing on an open string — inspector rule L06. Same rule as
        // ToggleNote; the two are the only shape-authoring gestures.
        if (fret <= Capo) newState.Fingers.Remove(edit.StringIndex);
        var before = _historyService.CurrentNodeId;
        ApplyTemplateEdit(newState);
        // A REFUSED digit pushed nothing: the current node is somebody else's
        // work, and anchoring the run on it let the next digit undo it.
        _fretRunNodeId = _historyService.CurrentNodeId != before ? _historyService.CurrentNodeId : -1;
    }

    /// <summary>
    ///     Hover-typed finger. Fills the DRAFT only — the buffered path the
    ///     panel has always used — so the user still presses Save to commit and
    ///     propagate. What changes is that the draft is now visible on the chord
    ///     box instead of expressed as a disabled button somewhere else.
    /// </summary>
    [RelayCommand]
    private void SetHoveredFinger(ChordBoxFingerEdit edit)
    {
        if (Template == null || _isUpdatingUi) return;
        SetFingerDraftForStrings(edit.Finger, new[] { edit.StringIndex });
    }

    /// <summary>Remove a string from the shape (hover + <c>Del</c>, or a
    /// right-click). Its fingering goes with it — a finger on an unused string
    /// is inspector rule L06.</summary>
    [RelayCommand]
    private void ClearString(int stringIdx)
    {
        if (Template == null || _isUpdatingUi) return;
        if (!Template.Frets.ContainsKey(stringIdx)) return;

        var newState = Template.Clone();
        newState.Frets.Remove(stringIdx);
        newState.Fingers.Remove(stringIdx);
        _fretRunNodeId = -1;
        ApplyTemplateEdit(newState);
    }

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

        var targets = SwitchTargets();
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
