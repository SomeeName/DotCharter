using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The shape editor over a real arrangement: one grip, its SCOPE chosen first. Every change of scope,
///     grip, name or hand-shape switch re-plans through <see cref="ShapeEditPlanner" /> — the same plan Apply
///     executes — so the lit uses, the sentence and the Apply caption cannot promise one reach while the
///     write does another. The uses the scope reaches are marked on the timeline while the dialog is open
///     (<see cref="IChordUseMarks" />). Apply is ONE undo step.
/// </summary>
public sealed class ShapeEditViewModel : DialogViewModel<bool>, IShapeEditScreen, IDialogSetup<ShapeEditRequest>,
    ICancelableDialog, IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly IChordUseMarks _marks;
    private readonly ShapeEditWords _words;
    private List<ShapeEditScopeViewModel> _scopes;
    private List<ShapeEditScope> _kinds = [ShapeEditScope.ThisChord, ShapeEditScope.Selection, ShapeEditScope.EveryUse];
    private readonly RelayCommand _apply;
    private readonly RelayCommand _useMatch;
    private ShapeEditInput? _input;
    private ShapeEditScopeViewModel _selected;
    private ShapeEditPlan? _plan;
    private ChordTemplate _after = new();
    private Dictionary<object, int> _barOf = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<ShapeEditStringViewModel> _strings = [];
    private IReadOnlyList<IShapeUseBar> _useBars = [];
    private string _nameDraft = "";
    private bool _isNoName;
    private bool _keepInSync = true;
    private int _barCount;
    private int? _originBar;
    private bool _configuring;

    public ShapeEditViewModel(ChartEditDialogServices edit, IChordUseMarks marks)
    {
        _edit = edit;
        _marks = marks;
        _words = new ShapeEditWords(edit.Localization);
        _scopes =
        [
            new ShapeEditScopeViewModel(edit.Localization["ShapeEdit_Scope_This"], Select),
            new ShapeEditScopeViewModel(edit.Localization["ShapeEdit_Scope_Selection"], Select),
            new ShapeEditScopeViewModel(edit.Localization["ShapeEdit_Scope_Every"], Select)
        ];
        _selected = _scopes[2];
        _apply = new RelayCommand(Apply, () => _plan?.Action is not null);
        _useMatch = new RelayCommand(Apply, () => HasMatch && _plan?.Action is not null);
        UseSuggestedNameCommand = new RelayCommand(() => NameDraft = SuggestedName);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string ShapeTitle { get; private set; } = "";

    public string OpenedFromText { get; private set; } = "";

    public IReadOnlyList<IShapeEditScope> Scopes => _scopes;

    public IReadOnlyList<IShapeUseBar> UseBars
    {
        get => _useBars;
        private set => SetProperty(ref _useBars, value);
    }

    public string PreviewText { get; private set; } = "";

    public ChordTemplate BeforeDiagram { get; private set; } = new();

    public Chord BeforeChord { get; private set; } = new();

    public ChordTemplate AfterDiagram => _after;

    public Chord AfterChord { get; private set; } = new();

    public int StringCount => _strings.Count;

    public IReadOnlyList<IShapeEditString> Strings => _strings;

    public string NameDraft
    {
        get => _nameDraft;
        set
        {
            if (SetProperty(ref _nameDraft, value ?? "")) Replan();
        }
    }

    public string NamePlaceholder { get; private set; } = "";

    public bool IsNoName
    {
        get => _isNoName;
        set
        {
            if (SetProperty(ref _isNoName, value)) Replan();
        }
    }

    public string SuggestedName { get; private set; } = "";

    public bool HasSuggestedName => SuggestedName.Length > 0 && !_isNoName
                                    && !string.Equals(SuggestedName, _nameDraft.Trim(), StringComparison.Ordinal);

    public ICommand UseSuggestedNameCommand { get; }

    public bool KeepHandShapesInSync
    {
        get => _keepInSync;
        set
        {
            if (SetProperty(ref _keepInSync, value)) Replan();
        }
    }

    public string HandShapeSyncText { get; private set; } = "";

    public bool HasMatch => _plan?.Match is not null;

    public string MatchText { get; private set; } = "";

    public ICommand UseMatchCommand => _useMatch;

    public string ResultText { get; private set; } = "";

    public string ApplyLabel { get; private set; } = "";

    public ICommand ApplyCommand => _apply;

    public ICommand CancelCommand { get; }

    /// <summary>What Apply will do right now: the test seam over the contract's words.</summary>
    internal ShapeEditPlan? CurrentPlan => _plan;

    internal ShapeEditScope SelectedScope => _kinds[_scopes.IndexOf(_selected)];

    internal void ChooseScope(ShapeEditScope scope) => Select(_scopes[_kinds.IndexOf(scope)]);

    public void Configure(ShapeEditRequest request)
    {
        var arrangement = _edit.Editor.ActiveArrangement
                          ?? throw new InvalidOperationException("The shape editor needs an active arrangement.");
        var selection = _edit.Editor.Selection;
        _input = new ShapeEditInput(arrangement, request.Shape, request.Origin,
            [.. selection.SelectedChords], [.. selection.SelectedHandShapes], request.Together);
        _configuring = true;
        if (_input.Group.Count > 1) UseTogetherScopes(_input.Group.Count);
        _keepInSync = _edit.Config.LoadAppConfig().SyncHandShapeToChordTemplate;
        var count = Math.Max(1, arrangement.StringCount);
        BeforeDiagram = request.Shape.Clone();
        BeforeChord = ChordOf(request.Shape);
        ShapeTitle = Title(request.Shape, count);
        _strings =
        [
            .. Enumerable.Range(0, count).Select(s => new ShapeEditStringViewModel(s, StringName(arrangement, s),
                request.Shape.Frets.TryGetValue(s, out var fret) ? fret.ToString(CultureInfo.InvariantCulture) : "x",
                request.Shape.Fingers.TryGetValue(s, out var finger) ? FingerTextCodec.Format(finger) : ""))
        ];
        foreach (var row in _strings) row.PropertyChanged += (_, _) => Replan();
        _nameDraft = request.Shape.Name;
        _isNoName = request.Shape.Unnamed;
        IndexBars(arrangement, request);
        OpenedFromText = _input.Group.Count > 1 ? _edit.Localization.Format("ShapeEdit_OpenedFromTicked", _input.Group.Count)
            : request.Origin is null ? _edit.Localization["ShapeEdit_OpenedFromList"]
            : _originBar is { } bar ? _edit.Localization.Format("ShapeEdit_OpenedFromChord", bar)
            : _edit.Localization["ShapeEdit_OpenedFromChordNoBar"];
        _configuring = false;
        Replan();
    }

    public bool TryCancel()
    {
        Finish(false);
        return true;
    }

    public void Dispose() => _marks.Clear();

    /// <summary>An Edit together offers two scopes: the shape on its own, and every ticked shape (the default).</summary>
    private void UseTogetherScopes(int shapes)
    {
        _scopes =
        [
            new ShapeEditScopeViewModel(_edit.Localization["ShapeEdit_Scope_ThisShape"], Select),
            new ShapeEditScopeViewModel(_edit.Localization.Format("ShapeEdit_Scope_Together", shapes), Select)
        ];
        _kinds = [ShapeEditScope.EveryUse, ShapeEditScope.Together];
        _selected = _scopes[1];
    }

    private void Select(ShapeEditScopeViewModel scope)
    {
        if (!scope.IsAvailable) return;
        _selected = scope;
        Replan();
    }

    private void Apply()
    {
        if (_plan?.Action is not { } action) return;
        _edit.History.Do(action);
        Finish(true);
    }

    private void Finish(bool applied)
    {
        _marks.Clear();
        Close(applied);
    }

    private ShapeEditDraft Draft()
    {
        var frets = new Dictionary<int, int>();
        var fingers = new Dictionary<int, int>();
        foreach (var row in _strings)
        {
            if (!int.TryParse(row.Fret.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var fret)) continue;
            frets[row.StringIndex] = fret;
            var finger = FingerTextCodec.Parse(row.Finger);
            if (finger != FingerTextCodec.InvalidFinger) fingers[row.StringIndex] = finger;
        }

        return new ShapeEditDraft(frets, fingers, _nameDraft, _isNoName);
    }

    private void Replan()
    {
        if (_configuring || _input is not { } input) return;
        var count = _strings.Count;
        var draft = Draft();
        // The scopes first: each one's reach is its own plan, and one that reaches nothing is greyed.
        for (var i = 0; i < _scopes.Count; i++)
        {
            var reach = ShapeEditPlanner.ReachOf(input, _kinds[i], _keepInSync);
            _scopes[i].IsAvailable = reach.Count > 0;
            _scopes[i].Detail = _kinds[i] == ShapeEditScope.Together ? _words.DetailTogether(reach) : _words.Detail(reach, _originBar);
        }

        if (!_selected.IsAvailable) _selected = _scopes.LastOrDefault(s => s.IsAvailable) ?? _selected;
        foreach (var scope in _scopes) scope.IsSelected = ReferenceEquals(scope, _selected);

        var kind = SelectedScope;
        var plan = ShapeEditPlanner.Plan(input, kind, draft, _keepInSync, _edit.Editor);
        _plan = plan;
        _after = ShapeEditPlanner.Apply(input.Shape, draft);
        AfterChord = ChordOf(_after);
        var name = ChordShapeNames.DisplayName(input.Shape, count);
        SuggestedName = NewChordNames.For(input.Arrangement, _after.Frets);
        NamePlaceholder = ChordShapeNames.FretPattern(_after, count);
        PreviewText = _words.Preview(plan, name);
        var matchName = plan.Match is { } match ? ChordShapeNames.DisplayName(match, count) : "";
        MatchText = plan.Match is null ? "" : _words.Match(matchName, plan.MatchUses);
        ResultText = _words.Result(plan, name, matchName, input.Group.Count);
        ApplyLabel = _words.Apply(kind, plan, input.Group.Count);
        HandShapeSyncText = _words.HandShapes(plan.HandShapesAffected, _keepInSync);
        UseBars = BarsOf(input, plan.Reach);
        _marks.Mark(plan.Reach.Chords, plan.Reach.HandShapes);
        _apply.NotifyCanExecuteChanged();
        _useMatch.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    private void IndexBars(Arrangement arrangement, ShapeEditRequest request)
    {
        _barOf = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var beats = _edit.State.ActiveProject?.Beats;
        _barCount = beats?.Count(b => b.IsMeasure) ?? 0;
        _originBar = null;
        if (beats is null || _barCount == 0) return;
        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords)
            {
                if (LiveBarNumbers.BarAt(beats, chord.GetTimeMs(beats)) is { } bar) _barOf[chord] = bar;
            }

            foreach (var handShape in level.HandShapes)
            {
                if (LiveBarNumbers.BarAt(beats, handShape.Position.GetPositionMs(beats)) is { } bar) _barOf[handShape] = bar;
            }
        }

        if (request.Origin is { } origin && _barOf.TryGetValue(origin, out var originBar)) _originBar = originBar;
    }

    private IReadOnlyList<IShapeUseBar> BarsOf(ShapeEditInput input, ShapeEditReach reach)
    {
        if (_barCount == 0) return [];
        var all = ShapeBulkEdits.Normalize(input.Arrangement, input.Group).Select(t => ChordShapeEdits.UsesOf(input.Arrangement, t)).ToList();
        var uses = BarsOfUses(all.SelectMany(u => u.Chords.Cast<object>().Concat(u.HandShapes)));
        var reached = BarsOfUses(reach.Chords.Cast<object>().Concat(reach.HandShapes));
        return [.. Enumerable.Range(1, _barCount).Select(b => new ShapeEditUseBar(uses.Contains(b), reached.Contains(b), b == _originBar))];
    }

    private HashSet<int> BarsOfUses(IEnumerable<object> sounds)
    {
        var bars = new HashSet<int>();
        foreach (var sound in sounds)
        {
            if (_barOf.TryGetValue(sound, out var bar)) bars.Add(bar);
        }

        return bars;
    }

    private static string Title(ChordTemplate template, int stringCount) =>
        ChordShapeNames.IsUnnamed(template)
            ? ChordShapeNames.FretPattern(template, stringCount)
            : $"{template.Name} · {ChordShapeNames.FretPattern(template, stringCount)}";

    internal static Chord ChordOf(ChordTemplate template)
    {
        var chord = new Chord { TemplateId = template.Id };
        foreach (var (stringIndex, fret) in template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    internal static string StringName(Arrangement arrangement, int stringIndex)
    {
        if (SoundingPitch.OpenStringMidi(arrangement, stringIndex) is not { } midi) return "";
        var name = ArrangementTunerLogic.NoteNameOf(midi).TrimEnd('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        // The chord panel prints the arrangement's own tuning names (the high string is "e"): use them while they agree.
        var tuning = arrangement.Tuning;
        return stringIndex < tuning.Length && string.Equals(tuning[stringIndex], name, StringComparison.OrdinalIgnoreCase)
            ? tuning[stringIndex]
            : name;
    }
}
