using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Note tab's Shape page (inspector design D): one row per string with the chord's fret
///     ("Note"), the hand shape's grip fret ("Grip"), its ghost toggle and its finger, beside a fretboard.
///     Every edit is a DRAFT until Save. A grip edit writes hand shapes only and never adds a note;
///     a Note edit re-frets a note the chord already has. Its targets follow the footer's Apply-to rung
///     (<see cref="ChordTemplateEditorViewModel.ApplyRung" />), resolved by <see cref="GripInspectorTargets" />.
///     Exposed as <c>InspectorViewModel.GripInspector</c>; idle unless the Shape section is on screen.
/// </summary>
public sealed partial class GripInspectorViewModel : ViewModelBase, IDisposable
{
    private static readonly ChordApplyRung[] ScopeOrder =
        [ChordApplyRung.ThisChord, ChordApplyRung.Selected, ChordApplyRung.SameGrip, ChordApplyRung.EveryUse];

    private readonly ChartEditorViewModel _editor;
    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _loc;
    private readonly SoundSectionsViewModel _sections;
    private readonly ChordTemplateEditorViewModel _ladder;
    private readonly Dictionary<ChartLevel, Func<HandShape, ChordTemplate, int>> _openingMasks = [];
    private GripInspectorDraft _draft = new();
    private GripInspectorTargets _targets = new([], []);
    private Arrangement? _arrangement;
    private ChartLevel? _level;
    private HandShape? _selectedShape;
    private HandShape? _shape;
    private Chord? _chord;
    private long _selectionVersion = -1;
    private GripRun? _run;
    private ChordTemplate[] _grips = [];
    private bool _syncingScope;

    internal GripInspectorViewModel(ChartEditorViewModel editor, ProjectState state, IHistoryService history,
        ILocalizationService localization, SoundSectionsViewModel sections, ChordTemplateEditorViewModel ladder)
    {
        _editor = editor;
        _state = state;
        _history = history;
        _loc = localization;
        _sections = sections;
        _ladder = ladder;
        Scopes =
        [
            _loc["GripInspector_Scope_ThisChord"], _loc["GripInspector_Scope_Selected"],
            _loc["GripInspector_Scope_SameGrip"], _loc["GripInspector_Scope_EveryUse"]
        ];
        _scopeIndex = Math.Max(0, Array.IndexOf(ScopeOrder, ladder.ApplyRung));
        editor.Selection.SelectionChanged += OnSelectionChanged;
        history.HistoryChanged += OnHistoryChanged;
        state.PropertyChanged += OnProjectStateChanged;
        sections.PropertyChanged += OnSectionsChanged;
        ladder.PropertyChanged += OnLadderChanged;
        Sync();
    }

    public IReadOnlyList<string> Scopes { get; }
    public bool IsActive => _sections.IsShape;
    public bool IsAvailable => _shape != null || _chord != null;
    public bool CanEditGrip => _targets.Shapes.Count > 0;
    public bool InvertStrings => _state.InvertStrings;
    public int StringCount => _arrangement?.StringCount ?? 6;
    public int Capo => _arrangement?.Capo ?? 0;
    public StringEditMask Mask => _state.StringEditMask;
    public IReadOnlyList<string> TuningNames { get; private set; } = [];
    public IReadOnlyList<GripRow> Rows { get; private set; } = [];
    public ChordTemplate FretboardTemplate { get; private set; } = new();
    public Chord? FretboardChord { get; private set; }
    public Chord? BoardNotes { get; private set; }
    public IReadOnlyList<int> MutedStrings { get; private set; } = [];
    public string TargetCount { get; private set; } = "";

    /// <summary>The chords a Save reaches, short ("12 chords"): the footer Save's second line (the tooltip says all).</summary>
    public string TargetChords { get; private set; } = "";
    public int RebindCount { get; private set; }
    public bool HasRebindWarning => RebindCount != 0;
    public string RebindWarning => HasRebindWarning
        ? _loc.Plural("GripInspector_RebindOne", "GripInspector_Rebind", RebindCount)
        : _loc["Inspector_Grip_NoRebind"];
    public bool HasDraft => _draft.HasGripEdits || _draft.ChordFrets.Count > 0;
    public bool CanName => CanEditGrip && NoName != true;
    public bool CanEditPin => CanEditGrip && _grips.All(g => g.GhostMask == 0);
    public string NamePlaceholder { get; private set; } = "";
    public string GripSummary { get; private set; } = "";
    public IReadOnlyList<string> KnownNames { get; private set; } = [];

    /// <summary>The names the grip's frets suggest, offered as "Use …" buttons on the Name page.</summary>
    public IReadOnlyList<GripNameSuggestion> SuggestedNames { get; private set; } = [];
    public bool HasSuggestedNames => SuggestedNames.Count > 0;
    public IReadOnlyList<GripTemplateOption> TemplateOptions { get; private set; } = [];
    public bool NoTemplates => TemplateOptions.Count == 0;
    public string RunPosition => _run is { Position: > 0 } run ? _loc.Format("GripInspector_RunPosition", run.Position, run.Count) : "";

    /// <summary>Raised after a navigation verb selected another shape, so the view can keep keyboard focus on the board.</summary>
    public event Action? FocusDestination;

    public void Dispose()
    {
        _editor.Selection.SelectionChanged -= OnSelectionChanged;
        _history.HistoryChanged -= OnHistoryChanged;
        _state.PropertyChanged -= OnProjectStateChanged;
        _sections.PropertyChanged -= OnSectionsChanged;
        _ladder.PropertyChanged -= OnLadderChanged;
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Sync();

    private void OnHistoryChanged()
    {
        Sync(chartChanged: true);
        RefreshUndoSave();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveLevel)) Sync();
        if (e.PropertyName is nameof(ProjectState.InvertStrings) or nameof(ProjectState.StringEditMask)) RefreshStringOrder();
    }

    private void OnSectionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SoundSectionsViewModel.IsShape)) return;
        OnPropertyChanged(nameof(IsActive));
        if (IsActive) Sync(chartChanged: true);
    }

    // The footer's Apply-to ladder and the Scope page are ONE choice.
    private void OnLadderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChordTemplateEditorViewModel.ApplyRung) || _syncingScope) return;
        var index = Array.IndexOf(ScopeOrder, _ladder.ApplyRung);
        if (index >= 0) ScopeIndex = index;
    }

    [ObservableProperty] private int _scopeIndex;
    [ObservableProperty] private string _query = "";

    public string ChordNameText
    {
        get => _draft.Name ?? Common(_grips.Select(g => g.Name), "");
        set
        {
            if (!CanName || ChordNameText == value && NamePlaceholder.Length == 0) return;
            _draft.Name = value;
            _draft.NoName = false;
            DraftChanged(nameof(ChordNameText), nameof(NoName));
        }
    }

    public bool? Arpeggio
    {
        get => CommonFlag(g => g.IsArpeggio);
        set => SetFlag(value, Arpeggio, v => _draft.Arpeggio = v, nameof(Arpeggio));
    }

    public bool? DoubleStop
    {
        get => CommonFlag(g => g.NopInRs);
        set => SetFlag(value, DoubleStop, v => _draft.DoubleStop = v, nameof(DoubleStop));
    }

    public bool? NoName
    {
        get => CommonFlag(g => g.Unnamed);
        set => SetFlag(value, NoName, v => _draft.NoName = v, nameof(NoName), nameof(CanName), nameof(ChordNameText));
    }

    public bool? Fingerless
    {
        get => CommonFlag(g => g.Fingerless);
        set => SetFlag(value, Fingerless, v => _draft.Fingerless = v, nameof(Fingerless));
    }

    public bool? IsPinned
    {
        get => CommonFlag(g => g.IsPinned);
        set
        {
            if (CanEditPin) SetFlag(value, IsPinned, v => _draft.Pin = v, nameof(IsPinned));
        }
    }

    private bool? CommonFlag(Func<ChordTemplate, bool> selector) => Common(_grips.Select(g => (bool?)selector(g)), null);

    private void SetFlag(bool? value, bool? current, Action<bool> change, params string[] properties)
    {
        if (!CanEditGrip || value is not { } next || current == value) return;
        change(next);
        DraftChanged(properties);
    }

    partial void OnScopeIndexChanged(int value)
    {
        if (value < 0 || value >= ScopeOrder.Length) return;
        _syncingScope = true;
        _ladder.ApplyRung = ScopeOrder[value];
        _syncingScope = false;
        if (!IsActive) return;
        ResolvePrimaryShape();
        ResolveTargets();
        RefreshProjection();
        RefreshRun();
    }

    // Typing coalesces: one pass per burst, at Background priority (CoalescedFilterPass).
    partial void OnQueryChanged(string value) => NameOffersPass.Request();

    internal void Sync(bool chartChanged = false)
    {
        var level = _editor.ActiveLevel;
        var arrangement = _editor.ActiveArrangement;
        var selection = _editor.Selection;
        var selectionChanged = _selectionVersion != selection.SelectionVersion || _level != level || _arrangement != arrangement;
        if (!selectionChanged && !chartChanged) return;
        if (selectionChanged)
        {
            _draft = new GripInspectorDraft();
            ClearIdenticalShapes();
        }
        if (!IsActive)
        {
            // Idle while another section shows: the next show re-syncs from scratch.
            _selectionVersion = -1;
            return;
        }

        if (chartChanged || _arrangement != arrangement)
        {
            _openingMasks.Clear();
            _nameOffersKey = null;
        }
        _selectionVersion = selection.SelectionVersion;
        _level = level;
        _arrangement = arrangement;
        _chord = level == null ? null : selection.SelectedChords.FirstOrDefault(level.Chords.Contains);
        _selectedShape = level == null ? null : selection.SelectedHandShapes.FirstOrDefault(level.HandShapes.Contains);
        if (_chord == null && _selectedShape != null)
            _chord = level!.Chords.Where(c => _selectedShape.Covers(c.Position)).MinBy(c => c.Position);
        ResolvePrimaryShape();
        OnPropertyChanged(nameof(IsAvailable));
        TuningNames = TuningDisplayNames.For(arrangement) ?? [];
        Notify(nameof(TuningNames), nameof(StringCount), nameof(Capo));
        ResolveTargets();
        RefreshProjection();
        RefreshRun();
    }

    internal void RefreshStringOrder()
    {
        Notify(nameof(InvertStrings), nameof(Mask));
        if (IsActive && IsAvailable) RefreshRows();
    }

    private ChordApplyRung Scope => ScopeOrder[Math.Clamp(ScopeIndex, 0, ScopeOrder.Length - 1)];

    private void ResolvePrimaryShape() =>
        _shape = _level != null && (_selectedShape == null || Scope == ChordApplyRung.ThisChord)
            ? GripInspectorTargets.CoveringShape(_level, _selectedShape, _chord) : _selectedShape;

    private void ResolveTargets()
    {
        _targets = _arrangement == null || _level == null || !IsAvailable ? new([], []) :
            GripInspectorTargets.Resolve(_arrangement, _level, _shape, _chord,
                _editor.Selection.SelectedHandShapes, _editor.Selection.SelectedChords, Scope);
        var chords = _loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", _targets.Chords.Count);
        var shapes = _loc.Plural("ChordTemplate_Reach_HandShapesOne", "ChordTemplate_Reach_HandShapes", _targets.Shapes.Count);
        TargetCount = $"{chords} · {shapes}";
        TargetChords = chords;
        Notify(nameof(TargetCount), nameof(TargetChords), nameof(CanEditGrip));
    }

    private ChordTemplate GripOf(HandShape shape) => _arrangement?.FindChordTemplate(shape.TemplateId) ?? new ChordTemplate();

    private Func<HandShape, ChordTemplate, int> OpeningMasks(ChartLevel level)
    {
        if (_openingMasks.TryGetValue(level, out var masks)) return masks;
        masks = GhostGems.IndexOpeningGhostMasks(level, ProjectOpeningFret);
        _openingMasks.Add(level, masks);
        return masks;
    }

    private int ProjectOpeningFret(Chord chord, int index, int fret) =>
        _targets.ChordSet.Contains(chord) ? _draft.ChordFrets.GetValueOrDefault(index, fret) : fret;

    private ChordTemplate ProjectGrip(GripShapeTarget target) =>
        _draft.Project(target.Shape, GripOf(target.Shape), OpeningMasks(target.Level), target.Fields);

    private void DraftChanged(params string[] properties)
    {
        RefreshProjection();
        Notify(properties);
    }

    private void RefreshProjection()
    {
        _grips = _targets.Shapes.Select(ProjectGrip).ToArray();
        FretboardTemplate = _shape != null && _level != null ? ProjectGrip(new(_level, _shape)) : new();
        FretboardChord = _chord == null ? null : GripEdit.Refret(_chord, _draft.ChordFrets) ?? _chord;
        MutedStrings = FretboardChord?.ChordNotes.Where(n => n.Mute == MuteType.Full)
            .Select(n => n.StringIndex).ToArray() ?? [];
        BoardNotes = new Chord
        {
            ChordNotes = new(FretboardTemplate.Frets.Where(f => !MutedStrings.Contains(f.Key))
                .Select(f => new ChordNote { StringIndex = f.Key, Fret = f.Value }))
        };
        if (FretboardTemplate.Frets.Count == 0) BoardNotes = FretboardChord;
        RebindCount = _targets.Chords.Count(t => NeedsRebind(t.Chord));
        NamePlaceholder = _grips.Select(g => g.Name).Distinct().Skip(1).Any() ? _loc["GripInspector_Mixed"] : "";
        GripSummary = DisplayTemplate(FretboardTemplate);
        RefreshRows();
        RefreshNameOffers();
        RefreshDoubleStopPair();
        Notify(nameof(FretboardTemplate), nameof(FretboardChord), nameof(BoardNotes), nameof(MutedStrings), nameof(HasRebindWarning),
            nameof(RebindCount), nameof(RebindWarning), nameof(GripSummary),
            nameof(ChordNameText), nameof(NamePlaceholder), nameof(Arpeggio), nameof(DoubleStop), nameof(NoName),
            nameof(Fingerless), nameof(IsPinned), nameof(CanName), nameof(CanEditPin), nameof(HasDraft));
        SaveFingeringCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Every template entry shows its name AND its frets.</summary>
    private string DisplayTemplate(ChordTemplate template)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? _loc["GripInspector_Unnamed"] : template.Name;
        return $"{name} · {GripEdit.FretText(template, StringCount)}";
    }

    // ---- the Name page's lists: built only while the page shows, and only when what they read changed ----

    private string? _nameOffersKey;
    private CoalescedFilterPass? _nameOffersPass;

    /// <summary>How a coalesced search pass is scheduled; null is the UI dispatcher at Background priority.</summary>
    internal Action<Action>? PostNameOffers { get; set; }

    /// <summary>How many times the Name page's lists were rebuilt (a structural cost probe for tests).</summary>
    internal int NameOfferPasses { get; private set; }

    private CoalescedFilterPass NameOffersPass => _nameOffersPass ??= new CoalescedFilterPass(RefreshNameOffers, PostNameOffers);

    /// <summary>
    ///     Rebuilds the template list, the known names and the suggested names when the Name page is on screen and
    ///     their inputs (the grip's frets, the chord's struck notes, the search, the chart) changed. A finger, ghost
    ///     or flag edit changes none of them, so a keystroke there rebuilds nothing.
    /// </summary>
    private void RefreshNameOffers()
    {
        if (!IsActive || !IsNamePage || _arrangement is not { } arrangement) return;
        var struck = FretboardChord?.ChordNotes.Where(n => n.Mute != MuteType.Full).ToArray() ?? [];
        var key = string.Join('|', GripEdit.FretText(FretboardTemplate, StringCount),
            string.Join(',', struck.Select(n => $"{n.StringIndex}:{n.Fret}")), Query.Trim());
        if (key == _nameOffersKey) return;
        _nameOffersKey = key;
        NameOfferPasses++;
        // A chord with no hand shape is named from its own struck frets, as the old chord-shape editor did.
        IReadOnlyDictionary<int, int> frets = FretboardTemplate.Frets.Count > 0
            ? FretboardTemplate.Frets
            : struck.GroupBy(n => n.StringIndex).ToDictionary(g => g.Key, g => g.First().Fret);
        var offers = NewChordNames.Offers(arrangement, frets, NewChordNames.NamingFor(arrangement)).ToArray();
        SuggestedNames = offers.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => new GripNameSuggestion(n, _loc.Format("Inspector_Grip_UseName", n))).ToArray();
        KnownNames = offers.Concat(arrangement.ChordTemplates.Select(t => t.Name)).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        RefreshTemplateOptions(struck);
        Notify(nameof(KnownNames), nameof(SuggestedNames), nameof(HasSuggestedNames));
    }

    private void RefreshTemplateOptions(ChordNote[] struck)
    {
        var query = Query.Trim();
        var options = _arrangement?.ChordTemplates.Select(t => new GripTemplateOption(t, DisplayTemplate(t), true))
            .Where(t => t.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                t.Display.Replace(" ", "", StringComparison.Ordinal).Contains(query.Replace(" ", "", StringComparison.Ordinal),
                    StringComparison.CurrentCultureIgnoreCase))
            .ToArray() ?? [];
        var covering = options.Where(t => struck.Length > 0 &&
            struck.All(n => t.Template!.GetFret(n.StringIndex) == n.Fret)).ToArray();
        var other = options.Except(covering).ToArray();
        var entries = new List<GripTemplateOption>();
        if (covering.Length > 0)
        {
            entries.Add(new(null, _loc["GripStudy_PartOfShape"], false));
            entries.AddRange(covering);
        }
        if (other.Length > 0)
        {
            entries.Add(new(null, _loc["GripStudy_OtherTemplates"], false));
            entries.AddRange(other);
        }
        TemplateOptions = entries;
        Notify(nameof(TemplateOptions), nameof(NoTemplates));
    }

    private void RefreshRows()
    {
        var indices = Enumerable.Range(0, StringCount);
        if (InvertStrings) indices = indices.Reverse();
        var ordered = indices.ToArray();
        var labels = ordered.Select(index =>
        {
            var lane = StringCount - index;
            var tuning = index < TuningNames.Count ? TuningNames[index] : "";
            return $"{lane} {tuning}";
        }).ToArray();
        if (!Rows.Select(row => row.StringIndex).SequenceEqual(ordered) || !Rows.Select(row => row.Label).SequenceEqual(labels))
        {
            Rows = ordered.Select((index, i) => new GripRow(this, index, labels[i], _loc["GripInspector_MixedShort"],
                _loc["GripInspector_FingerMixed"])).ToArray();
            OnPropertyChanged(nameof(Rows));
        }

        foreach (var row in Rows)
        {
            var index = row.StringIndex;
            var frets = _targets.Chords.Select(t =>
            {
                var note = t.Chord.ChordNotes.FirstOrDefault(n => n.StringIndex == index);
                return note == null || note.Mute == MuteType.Full ? (int?)null : _draft.ChordFrets.GetValueOrDefault(index, note.Fret);
            }).Distinct().ToArray();
            row.Update(frets, _grips, _arrangement);
        }
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    private static T Common<T>(IEnumerable<T> values, T mixed)
    {
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext()) return mixed;
        var first = iterator.Current;
        while (iterator.MoveNext())
            if (!EqualityComparer<T>.Default.Equals(first, iterator.Current)) return mixed;
        return first;
    }
}

public sealed record GripTemplateOption(ChordTemplate? Template, string Display, bool IsChoice)
{
    public override string ToString() => Display;
}

/// <summary>A name the grip's frets suggest, and its button caption ("Use Abm").</summary>
public sealed record GripNameSuggestion(string Name, string Caption)
{
    public override string ToString() => Caption;
}
