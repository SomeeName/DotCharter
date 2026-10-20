using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IPatternBuilderScreen" />; Save answers true.</summary>
public sealed class FakePatternBuilderScreen : FakeDialog<bool>, IPatternBuilderScreen
{
    private static readonly Choices EnglishChoices = new(
        ["Exact frets", "Same shape, any position", "Ignore frets"],
        ["Exact strings", "Same string spacing", "Ignore strings"],
        ["One at a time", "As a run"],
        ["Exact rhythm", "Proportional", "Ignore rhythm"],
        ["Ignore sustain", "Same kind", "Same length"],
        ["Anywhere", "Same section", "Same phrase"]);

    private static readonly string[] Techniques =
        ["Bend", "Slide", "Hammer-on / pull-off", "Harmonic", "Mute", "Vibrato", "Tremolo", "Accent", "Link next",
            "Bass picking", "Ignored", "Pass other notes"];

    private static readonly (string Label, string Description)[] Companions =
    [
        ("Covering hand shape", "The hand shape a matched chord sits under."),
        ("Governing anchor", "The fret-hand position a match is played in."),
        ("Sounds inside", "The notes and chords inside a matched hand shape.")
    ];

    private IMatchPatternRow? _selectedPattern;

    private FakePatternBuilderScreen(IReadOnlyList<Row> rows, int selected, string matchSummary, int seed,
        bool foreignSeed = false, string conflict = "")
    {
        Patterns = rows;
        _selectedPattern = selected >= 0 ? rows[selected] : null;
        MatchSummary = matchSummary;
        SeedSummary = $"{seed} item{(seed == 1 ? "" : "s")} selected as the seed";
        HasSeed = seed > 0;
        SeedIsFromAnotherArrangement = foreignSeed;
        ConflictSummary = conflict;
        static ICommand Idle() => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var picked = selected >= 0 ? Idle() : Disabled();
        AddPatternCommand = Idle();
        DuplicatePatternCommand = picked;
        DeletePatternCommand = selected >= 0 ? Idle() : Disabled();
        RestoreBuiltInsCommand = Idle();
        var runnable = selected >= 0 && seed > 0 && !foreignSeed ? Idle() : Disabled();
        RunSelectCommand = runnable;
        RunNextCommand = runnable;
        RunPreviousCommand = runnable;
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IPatternBuilderScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>The built-in patterns counted against a three-note seed, the first one picked.</summary>
    public static FakePatternBuilderScreen Default() => new(
    [
        new Row("Same chord", "Chords · exact frets · same size", "12", chords: true, select: "Ctrl+Shift+C"),
        new Row("Same riff", "Notes as a run · same shape, any position", "4", run: true, next: "Alt+Right", previous: "Alt+Left"),
        new Row("Same hand shape", "Hand shapes · exact frets", "7", shapes: true)
    ], 0, "12 matches for this pattern", 3);

    /// <summary>Every pattern deleted: the five verbs are greyed and say why.</summary>
    public static FakePatternBuilderScreen Empty() => new([], -1, "Pick a pattern on the left.", 0);

    /// <summary>German names and summaries, a Japanese pattern, a seed from another arrangement, a key clash.</summary>
    public static FakePatternBuilderScreen LongText() => new(
    [
        new Row("Gleicher Akkord mit derselben Griffhaltung über alle Lagen", "Akkorde · beliebige Lage · gleiche Größe · gleicher Fingersatz",
            "0", chords: true, select: "Ctrl+Shift+Alt+F12"),
        new Row("同じリフ（ポジションを問わない、長いパターン名のテスト）", "ノート（ラン）· 同じ形", "0", run: true)
    ], 0, "Die Auswahl liegt in einem anderen Arrangement als die aktive Spur – jede Zählung ist 0.", 2, foreignSeed: true,
        conflict: "Konflikt: Strg+Umschalt+Alt+F12 (Auswählen) → Metronom umschalten; Alt+Rechts (Weiter) → Nächster Takt");

    /// <summary>Nine patterns, every section open: anchors in scope, a run, companions ticked.</summary>
    public static FakePatternBuilderScreen Full() => new(
    [
        .. Enumerable.Range(1, 9).Select(i => new Row($"Pattern {i}", $"Notes, chords, hand shapes, anchors · run · rule {i}",
            (i * 3).ToString(System.Globalization.CultureInfo.InvariantCulture), chords: true, shapes: true, anchors: true, run: true,
            select: i == 1 ? "Ctrl+F1" : null, next: i == 1 ? "Ctrl+F2" : null, previous: i == 1 ? "Ctrl+F3" : null,
            companions: true))
    ], 0, "3 matches for this pattern", 5);

    public IReadOnlyList<IMatchPatternRow> Patterns { get; }

    public IMatchPatternRow? SelectedPattern
    {
        get => _selectedPattern;
        set => SetProperty(ref _selectedPattern, value);
    }

    public bool HasSelectedPattern => SelectedPattern is not null;

    public string NoPatternSelectedReason => HasSelectedPattern ? "" : "Pick a pattern to duplicate, delete or run it.";

    public string MatchSummary { get; }

    public string SeedSummary { get; }

    public bool HasSeed { get; }

    public bool SeedIsFromAnotherArrangement { get; }

    public bool SeedIsUsable => HasSeed && !SeedIsFromAnotherArrangement;

    public bool HasConflict => ConflictSummary.Length > 0;

    public string ConflictSummary { get; }

    public ICommand AddPatternCommand { get; }

    public ICommand DuplicatePatternCommand { get; }

    public ICommand DeletePatternCommand { get; }

    public ICommand RestoreBuiltInsCommand { get; }

    public ICommand RunSelectCommand { get; }

    public ICommand RunNextCommand { get; }

    public ICommand RunPreviousCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed record Choice(string Label) : IPatternChoice;

    private sealed class Choices(params string[][] lists) : IPatternChoices
    {
        private readonly IReadOnlyList<IPatternChoice>[] _lists =
            [.. lists.Select(l => (IReadOnlyList<IPatternChoice>)[.. l.Select(x => new Choice(x))])];

        public IReadOnlyList<IPatternChoice> Frets => _lists[0];
        public IReadOnlyList<IPatternChoice> Strings => _lists[1];
        public IReadOnlyList<IPatternChoice> Grouping => _lists[2];
        public IReadOnlyList<IPatternChoice> Rhythm => _lists[3];
        public IReadOnlyList<IPatternChoice> Sustain => _lists[4];
        public IReadOnlyList<IPatternChoice> Range => _lists[5];
    }

    private sealed class Toggle(string label, string description, bool selected) : ObservableObject, IPatternToggle
    {
        private bool _isSelected = selected;

        public string Label { get; } = label;

        public string Description { get; } = description;

        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    private sealed class Row : ObservableObject, IMatchPatternRow
    {
        public Row(string name, string summary, string count, bool chords = false, bool shapes = false, bool anchors = false,
            bool run = false, string? select = null, string? next = null, string? previous = null, bool companions = false)
        {
            Name = name;
            Summary = summary;
            MatchCountLabel = count;
            ScopeNotes = !chords && !shapes;
            ScopeChords = chords;
            ScopeHandShapes = shapes;
            ScopeAnchors = anchors;
            SelectKey = KeyChord.Parse(select);
            NextKey = KeyChord.Parse(next);
            PreviousKey = KeyChord.Parse(previous);
            SelectedGrouping = Choices.Grouping[run ? 1 : 0];
            SelectedRange = Choices.Range[0];
            SelectedFretRule = Choices.Frets[run ? 1 : 0];
            SelectedStringRule = Choices.Strings[0];
            SelectedRhythm = Choices.Rhythm[1];
            SelectedSustain = Choices.Sustain[0];
            MaxFretDistance = 5;
            MatchSize = true;
            MatchFingering = chords;
            Techniques = [.. FakePatternBuilderScreen.Techniques.Select((t, i) => new Toggle(t, "", i < 2))];
            Companions = [.. FakePatternBuilderScreen.Companions.Select(c => new Toggle(c.Label, c.Description, companions))];
        }

        public string Name { get; set; }
        public string MatchCountLabel { get; }
        public string Summary { get; }
        public string KeyLabel => string.Join("  ·  ", new[] { SelectKey, NextKey, PreviousKey }.Where(k => k.IsBound));
        public bool HasKeys => SelectKey.IsBound || NextKey.IsBound || PreviousKey.IsBound;
        public bool ScopeNotes { get; set; }
        public bool ScopeChords { get; set; }
        public bool ScopeHandShapes { get; set; }
        public bool ScopeAnchors { get; set; }
        public IPatternChoices Choices => EnglishChoices;
        public IPatternChoice SelectedGrouping { get; set; }
        public IPatternChoice SelectedRange { get; set; }
        public IPatternChoice SelectedFretRule { get; set; }
        public IPatternChoice SelectedStringRule { get; set; }
        public IPatternChoice SelectedRhythm { get; set; }
        public IPatternChoice SelectedSustain { get; set; }
        public int MaxFretDistance { get; set; }
        public bool MatchSize { get; set; }
        public bool MatchSoundKind { get; set; }
        public bool MatchBeatOffset { get; set; }
        public bool MatchFingering { get; set; }
        public bool MatchChordName { get; set; }
        public bool MatchArpeggio { get; set; }
        public bool MatchAnchorWidth { get; set; }
        public bool MatchAnchorSpan { get; set; }
        public bool IsRun => SelectedGrouping == Choices.Grouping[1];
        public bool ShowsTemplateFacets => ScopeChords || ScopeHandShapes;
        public bool ShowsAnchorFacets => ScopeAnchors;
        public IReadOnlyList<IPatternToggle> Techniques { get; }
        public IReadOnlyList<IPatternToggle> Companions { get; }
        public KeyChord SelectKey { get; set; }
        public KeyChord NextKey { get; set; }
        public KeyChord PreviousKey { get; set; }
    }
}
