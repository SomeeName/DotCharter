using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Fakes.Dialogs;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IShapeEditScreen" /> over an old chart's shapes: picking a scope
///     really relights the song strip and rewrites the preview, the result and the Apply caption,
///     and the hand-shape switch really rewrites its line. Apply answers true, Cancel false.
/// </summary>
public sealed class FakeShapeEditScreen : FakeDialog<bool>, IShapeEditScreen
{
    private readonly FakeShapeEditSpec _spec;
    private readonly FakeShapeEditWords _words;
    private readonly List<FakeShapeEditScope> _scopes;
    private FakeShapeEditScope _selected;
    private IReadOnlyList<IShapeUseBar> _useBars = [];
    private bool _keepInSync = true;
    private bool _isNoName;
    private string _nameDraft;

    private FakeShapeEditScreen(FakeShapeEditSpec spec, FakeShapeEditWords words)
    {
        _spec = spec;
        _words = words;
        _nameDraft = spec.After.Name;
        _isNoName = spec.After.Name.Length == 0;
        _scopes = spec.Scopes.Select(s => new FakeShapeEditScope(s.Label, s.Detail, s.Reach, s.HandShapes, s.Reach > 0, Select)).ToList();
        _selected = _scopes[Math.Clamp(spec.SelectedScope, 0, _scopes.Count - 1)];
        _selected.IsSelected = true;
        ShapeTitle = spec.Before.Name.Length == 0
            ? FakeShapeGrips.FretsText(spec.Before)
            : $"{spec.Before.Name} · {FakeShapeGrips.FretsText(spec.Before)}";
        BeforeDiagram = spec.Before.ToTemplate();
        BeforeChord = spec.Before.ToChord();
        AfterDiagram = spec.After.ToTemplate();
        AfterChord = spec.After.ToChord();
        Strings = spec.StringNames.Select((name, s) => (IShapeEditString)new FakeShapeEditString(name,
            Fret(spec.Before, s), Finger(spec.Before, s), Fret(spec.After, s), Finger(spec.After, s))).ToList();
        NamePlaceholder = FakeShapeGrips.FretsText(spec.After);
        UseSuggestedNameCommand = new RelayCommand(() => NameDraft = SuggestedName);
        UseMatchCommand = new RelayCommand(() => { });
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
        Relight();
    }

    public static IReadOnlyList<FakeVariant<IShapeEditScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("ThisChord", ThisChord),
        new("Selection", Selection),
        new("MatchesExisting", MatchesExisting),
        new("HandShapesKept", HandShapesKept),
        new("NoName", NoName),
        new("FromHandShape", FromHandShape),
        new("SelectedShapes", SelectedShapes)
    ];

    /// <summary>Am refingered to Am7 from one of its chords, on every use, hand shapes following.</summary>
    public static FakeShapeEditScreen Default() => new(FakeShapeEditSpecs.AmToAm7(selected: 2), FakeShapeEditWords.English);

    /// <summary>A chord on a shape of its own: one use, so there is no scope to choose.</summary>
    public static FakeShapeEditScreen Empty() => new(FakeShapeEditSpecs.SingleUse(), FakeShapeEditWords.English);

    public static FakeShapeEditScreen LongText() => new(FakeShapeEditSpecs.LongNames(), FakeShapeEditWords.German);

    /// <summary>An eight-string arrangement 220 bars long, its shape used on three levels.</summary>
    public static FakeShapeEditScreen Full() => new(FakeShapeEditSpecs.EightString(), FakeShapeEditWords.English);

    /// <summary>Only the chord it was opened from: a new shape is made for it, the rest keep Am.</summary>
    public static FakeShapeEditScreen ThisChord() => new(FakeShapeEditSpecs.AmToAm7(selected: 0), FakeShapeEditWords.English);

    public static FakeShapeEditScreen Selection() => new(FakeShapeEditSpecs.AmToAm7(selected: 1), FakeShapeEditWords.English);

    /// <summary>The edited grip already exists, so the uses move onto it instead of making a copy.</summary>
    public static FakeShapeEditScreen MatchesExisting() =>
        new(FakeShapeEditSpecs.AmToAm7(selected: 1) with { MatchUses = 9, MatchName = "Am7" }, FakeShapeEditWords.English);

    /// <summary>The hand-shape switch off: the line warns that they stop matching their chords.</summary>
    public static FakeShapeEditScreen HandShapesKept()
    {
        var screen = Default();
        screen.KeepHandShapesInSync = false;
        return screen;
    }

    /// <summary>An unnamed dyad set to No name on purpose.</summary>
    public static FakeShapeEditScreen NoName() => new(FakeShapeEditSpecs.Dyad(), FakeShapeEditWords.English);

    /// <summary>Opened from a hand shape: its scopes are the hand shape alone, it with its chords, every use.</summary>
    public static FakeShapeEditScreen FromHandShape() => new(FakeShapeEditSpecs.HandShape(), FakeShapeEditWords.English);

    /// <summary>Opened on several ticked shapes: one fingering for all of them.</summary>
    public static FakeShapeEditScreen SelectedShapes() => new(FakeShapeEditSpecs.SelectedCopies(), FakeShapeEditWords.English);

    public string ShapeTitle { get; }

    public string OpenedFromText => _spec.OpenedFrom;

    public IReadOnlyList<IShapeEditScope> Scopes => _scopes;

    public IReadOnlyList<IShapeUseBar> UseBars
    {
        get => _useBars;
        private set => SetProperty(ref _useBars, value);
    }

    public string PreviewText => _words.Preview(_selected.Reach, _spec.TotalUses - _selected.Reach, FakeShapeGrips.Title(_spec.Before));

    public ChordTemplate BeforeDiagram { get; }

    public Chord BeforeChord { get; }

    public ChordTemplate AfterDiagram { get; }

    public Chord AfterChord { get; }

    public int StringCount => _spec.StringNames.Length;

    public IReadOnlyList<IShapeEditString> Strings { get; }

    public string NameDraft
    {
        get => _nameDraft;
        set => SetProperty(ref _nameDraft, value);
    }

    public string NamePlaceholder { get; }

    public bool IsNoName
    {
        get => _isNoName;
        set => SetProperty(ref _isNoName, value);
    }

    public string SuggestedName => _spec.Suggested;

    public bool HasSuggestedName => _spec.Suggested.Length > 0 && !_isNoName;

    public ICommand UseSuggestedNameCommand { get; }

    public bool KeepHandShapesInSync
    {
        get => _keepInSync;
        set
        {
            if (SetProperty(ref _keepInSync, value)) OnPropertyChanged(nameof(HandShapeSyncText));
        }
    }

    public string HandShapeSyncText => _words.HandShapes(HandShapesReached, _keepInSync);

    public bool HasMatch => _spec.MatchUses > 0;

    public string MatchText => HasMatch ? _words.Match(_spec.MatchName, _spec.MatchUses) : "";

    public ICommand UseMatchCommand { get; }

    public string ResultText =>
        _spec.ResultOverride.Length > 0 && _scopes.IndexOf(_selected) == _spec.SelectedScope ? _spec.ResultOverride
        : _words.Result(_selected.Reach, _spec.TotalUses, FakeShapeGrips.Title(_spec.Before), HasMatch ? _spec.MatchName : "");

    public string ApplyLabel =>
        _spec.ApplyOverride.Length > 0 && _scopes.IndexOf(_selected) == _spec.SelectedScope ? _spec.ApplyOverride
        : _words.Apply(_selected.Reach, _spec.TotalUses);

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private int HandShapesReached =>
        _selected.HandShapes >= 0 ? _selected.HandShapes
        : _spec.TotalUses == 0 ? 0 : (int)Math.Ceiling((double)_spec.HandShapes * _selected.Reach / _spec.TotalUses);

    private static string Fret(FakeShape shape, int s) =>
        s < shape.Frets.Length && shape.Frets[s] >= 0 ? shape.Frets[s].ToString(System.Globalization.CultureInfo.InvariantCulture) : "x";

    private static string Finger(FakeShape shape, int s) =>
        s < shape.Fingers.Length && shape.Fingers[s] > 0 ? shape.Fingers[s].ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    private void Select(FakeShapeEditScope scope)
    {
        _selected.IsSelected = false;
        _selected = scope;
        scope.IsSelected = true;
        Relight();
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(HandShapeSyncText));
    }

    /// <summary>Lights the use bars nearest the origin, as many as the scope reaches (scaled to the strip).</summary>
    private void Relight()
    {
        var uses = _spec.UseBars;
        var lit = _spec.TotalUses == 0 ? 0 : (int)Math.Ceiling((double)uses.Count * _selected.Reach / _spec.TotalUses);
        var reached = uses.OrderBy(b => Math.Abs(b - _spec.OriginBar)).Take(lit).ToHashSet();
        var useSet = uses.ToHashSet();
        UseBars = Enumerable.Range(0, _spec.Bars)
            .Select(b => (IShapeUseBar)new FakeShapeUseBar(useSet.Contains(b), reached.Contains(b), b == _spec.OriginBar))
            .ToList();
    }
}
