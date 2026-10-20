using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     What a facet change on a row can move. The NAME feeds the summary and
///     nothing the matcher reads, so it re-describes the row without re-counting
///     the rail — one full match sweep per pattern, per keystroke.
/// </summary>
public sealed class PatternFacetChangedEventArgs(bool affectsMatchCount) : EventArgs
{
    public bool AffectsMatchCount { get; } = affectsMatchCount;

    public static PatternFacetChangedEventArgs Counting { get; } = new(true);

    public static PatternFacetChangedEventArgs DescriptionOnly { get; } = new(false);
}

/// <summary>One technique the pattern can require agreement on.</summary>
public sealed partial class PatternTechniqueToggle(TechniqueFacet facet, string label) : ObservableObject, IPatternToggle
{
    [ObservableProperty] private bool _isSelected;

    public TechniqueFacet Facet { get; } = facet;

    public string Label { get; } = label;

    public string Description => "";
}

/// <summary>One related element a match drags along with it.</summary>
public sealed partial class PatternCompanionToggle(PatternCompanions companion, string label, string description)
    : ObservableObject, IPatternToggle
{
    [ObservableProperty] private bool _isSelected;

    public PatternCompanions Companion { get; } = companion;

    public string Label { get; } = label;

    public string Description { get; } = description;
}

/// <summary>A labelled enum choice for one of the pickers.</summary>
public sealed class PatternChoice<T>(T value, string label) : IPatternChoice where T : struct, Enum
{
    public T Value { get; } = value;

    public string Label { get; } = label;
}

/// <summary>The picker option lists, built once and shared by every row.</summary>
public sealed class PatternChoiceSet : IPatternChoices
{
    /// <summary>The technique rows in the order a charter thinks about them; captions come from the summary's own keys.</summary>
    private static readonly TechniqueFacet[] TechniqueOrder =
    [
        TechniqueFacet.Bend, TechniqueFacet.Slide, TechniqueFacet.Hopo, TechniqueFacet.Harmonic,
        TechniqueFacet.Mute, TechniqueFacet.Vibrato, TechniqueFacet.Tremolo, TechniqueFacet.Accent,
        TechniqueFacet.LinkNext, TechniqueFacet.BassPicking, TechniqueFacet.Ignored,
        TechniqueFacet.PassOtherNotes
    ];

    public required IReadOnlyList<PatternChoice<FretRule>> Frets { get; init; }
    public required IReadOnlyList<PatternChoice<StringRule>> Strings { get; init; }
    public required IReadOnlyList<PatternChoice<PatternGrouping>> Grouping { get; init; }
    public required IReadOnlyList<PatternChoice<RhythmRule>> Rhythm { get; init; }
    public required IReadOnlyList<PatternChoice<SustainRule>> Sustain { get; init; }
    public required IReadOnlyList<PatternChoice<PatternRange>> Range { get; init; }
    public required IReadOnlyList<(TechniqueFacet Facet, string Label)> Techniques { get; init; }
    public required IReadOnlyList<(PatternCompanions Companion, string Label, string Description)> Companions { get; init; }

    IReadOnlyList<IPatternChoice> IPatternChoices.Frets => Frets;
    IReadOnlyList<IPatternChoice> IPatternChoices.Strings => Strings;
    IReadOnlyList<IPatternChoice> IPatternChoices.Grouping => Grouping;
    IReadOnlyList<IPatternChoice> IPatternChoices.Rhythm => Rhythm;
    IReadOnlyList<IPatternChoice> IPatternChoices.Sustain => Sustain;
    IReadOnlyList<IPatternChoice> IPatternChoices.Range => Range;

    public static PatternChoiceSet Build(ILocalizationService loc) => new()
    {
        Frets = Options(loc, (FretRule.Exact, "Pattern_Fret_Exact"), (FretRule.Transposable, "Pattern_Fret_Transposable"),
            (FretRule.Ignore, "Pattern_Fret_Ignore")),
        Strings = Options(loc, (StringRule.Exact, "Pattern_String_Exact"), (StringRule.Relative, "Pattern_String_Relative"),
            (StringRule.Ignore, "Pattern_String_Ignore")),
        Grouping = Options(loc, (PatternGrouping.Individually, "Pattern_Grouping_Individually"),
            (PatternGrouping.AsRun, "Pattern_Grouping_AsRun")),
        Rhythm = Options(loc, (RhythmRule.Exact, "Pattern_Rhythm_Exact"), (RhythmRule.Proportional, "Pattern_Rhythm_Proportional"),
            (RhythmRule.Ignore, "Pattern_Rhythm_Ignore")),
        Sustain = Options(loc, (SustainRule.Ignore, "Pattern_Sustain_Ignore"), (SustainRule.SameKind, "Pattern_Sustain_SameKind"),
            (SustainRule.SameLength, "Pattern_Sustain_SameLength")),
        Range = Options(loc, (PatternRange.Anywhere, "Pattern_Range_Anywhere"), (PatternRange.SameSection, "Pattern_Range_SameSection"),
            (PatternRange.SamePhrase, "Pattern_Range_SamePhrase")),
        Techniques = [.. TechniqueOrder.Select(f => (f, loc[PatternSummary.TechniqueKey(f)]))],
        Companions =
        [
            (PatternCompanions.CoveringHandShape, loc["Pattern_Companion_HandShape"], loc["Pattern_Companion_HandShape_Desc"]),
            (PatternCompanions.GoverningAnchor, loc["Pattern_Companion_Anchor"], loc["Pattern_Companion_Anchor_Desc"]),
            (PatternCompanions.SoundsInside, loc["Pattern_Companion_Sounds"], loc["Pattern_Companion_Sounds_Desc"])
        ]
    };

    private static IReadOnlyList<PatternChoice<T>> Options<T>(ILocalizationService loc, params (T Value, string Key)[] items)
        where T : struct, Enum => [.. items.Select(i => new PatternChoice<T>(i.Value, loc[i.Key]))];
}

/// <summary>
///     One editable pattern: a working COPY of the stored value, so a session of
///     experimenting can be abandoned wholesale. <see cref="FacetChanged" /> is the
///     one subscription the dialog re-counts from.
/// </summary>
public sealed partial class MatchPatternRowViewModel : ObservableObject, IMatchPatternRow
{
    [ObservableProperty] private string _matchCountLabel = string.Empty;
    [ObservableProperty] private bool _matchArpeggio;
    [ObservableProperty] private bool _matchAnchorSpan;
    [ObservableProperty] private bool _matchAnchorWidth;
    [ObservableProperty] private bool _matchBeatOffset;
    [ObservableProperty] private bool _matchChordName;
    [ObservableProperty] private bool _matchFingering;
    [ObservableProperty] private bool _matchSize;
    [ObservableProperty] private bool _matchSoundKind;
    [ObservableProperty] private int _maxFretDistance;
    [ObservableProperty] private string _name;
    [ObservableProperty] private KeyChord _nextKey;
    [ObservableProperty] private KeyChord _previousKey;
    [ObservableProperty] private bool _scopeAnchors;
    [ObservableProperty] private bool _scopeChords;
    [ObservableProperty] private bool _scopeHandShapes;
    [ObservableProperty] private bool _scopeNotes;
    [ObservableProperty] private KeyChord _selectKey;
    [ObservableProperty] private PatternChoice<FretRule> _selectedFretRule;
    [ObservableProperty] private PatternChoice<PatternGrouping> _selectedGrouping;
    [ObservableProperty] private PatternChoice<PatternRange> _selectedRange;
    [ObservableProperty] private PatternChoice<RhythmRule> _selectedRhythm;
    [ObservableProperty] private PatternChoice<StringRule> _selectedStringRule;
    [ObservableProperty] private PatternChoice<SustainRule> _selectedSustain;
    [ObservableProperty] private string _summary = string.Empty;

    public MatchPatternRowViewModel(MatchPattern pattern, PatternChoiceSet choices)
    {
        Id = pattern.Id;
        Choices = choices;
        _name = pattern.Name;
        _scopeNotes = pattern.Scope.HasFlag(PatternScope.Notes);
        _scopeChords = pattern.Scope.HasFlag(PatternScope.Chords);
        _scopeHandShapes = pattern.Scope.HasFlag(PatternScope.HandShapes);
        _scopeAnchors = pattern.Scope.HasFlag(PatternScope.Anchors);
        _matchSize = pattern.MatchSize;
        _matchSoundKind = pattern.MatchSoundKind;
        _matchFingering = pattern.MatchFingering;
        _matchChordName = pattern.MatchChordName;
        _matchArpeggio = pattern.MatchArpeggio;
        _matchBeatOffset = pattern.MatchBeatOffset;
        _matchAnchorWidth = pattern.MatchAnchorWidth;
        _matchAnchorSpan = pattern.MatchAnchorSpan;
        _maxFretDistance = pattern.MaxFretDistance;
        _selectKey = pattern.SelectKey;
        _nextKey = pattern.NextKey;
        _previousKey = pattern.PreviousKey;
        _selectedFretRule = Pick(choices.Frets, pattern.Fret);
        _selectedStringRule = Pick(choices.Strings, pattern.Strings);
        _selectedGrouping = Pick(choices.Grouping, pattern.Grouping);
        _selectedRhythm = Pick(choices.Rhythm, pattern.Rhythm);
        _selectedSustain = Pick(choices.Sustain, pattern.Sustain);
        _selectedRange = Pick(choices.Range, pattern.Range);

        foreach (var (facet, label) in choices.Techniques)
            Techniques.Add(Watch(new PatternTechniqueToggle(facet, label) { IsSelected = pattern.Techniques.HasFlag(facet) }));
        foreach (var (companion, label, description) in choices.Companions)
        {
            Companions.Add(Watch(new PatternCompanionToggle(companion, label, description)
            {
                IsSelected = pattern.Companions.HasFlag(companion)
            }));
        }
    }

    public string Id { get; }

    public PatternChoiceSet Choices { get; }

    public ObservableCollection<PatternTechniqueToggle> Techniques { get; } = [];

    public ObservableCollection<PatternCompanionToggle> Companions { get; } = [];

    public string KeyLabel => string.Join("  ·  ",
        new[] { SelectKey, NextKey, PreviousKey }.Where(k => k.IsBound).Select(k => k.ToString()));

    public bool HasKeys => SelectKey.IsBound || NextKey.IsBound || PreviousKey.IsBound;

    public bool IsRun => SelectedGrouping.Value == PatternGrouping.AsRun;

    public bool ShowsAnchorFacets => ScopeAnchors;

    public bool ShowsTemplateFacets => ScopeChords || ScopeHandShapes;

    IPatternChoices IMatchPatternRow.Choices => Choices;
    IReadOnlyList<IPatternToggle> IMatchPatternRow.Techniques => Techniques;
    IReadOnlyList<IPatternToggle> IMatchPatternRow.Companions => Companions;

    IPatternChoice IMatchPatternRow.SelectedGrouping
    {
        get => SelectedGrouping;
        set => SelectedGrouping = (PatternChoice<PatternGrouping>)value;
    }

    IPatternChoice IMatchPatternRow.SelectedRange
    {
        get => SelectedRange;
        set => SelectedRange = (PatternChoice<PatternRange>)value;
    }

    IPatternChoice IMatchPatternRow.SelectedFretRule
    {
        get => SelectedFretRule;
        set => SelectedFretRule = (PatternChoice<FretRule>)value;
    }

    IPatternChoice IMatchPatternRow.SelectedStringRule
    {
        get => SelectedStringRule;
        set => SelectedStringRule = (PatternChoice<StringRule>)value;
    }

    IPatternChoice IMatchPatternRow.SelectedRhythm
    {
        get => SelectedRhythm;
        set => SelectedRhythm = (PatternChoice<RhythmRule>)value;
    }

    IPatternChoice IMatchPatternRow.SelectedSustain
    {
        get => SelectedSustain;
        set => SelectedSustain = (PatternChoice<SustainRule>)value;
    }

    public event EventHandler<PatternFacetChangedEventArgs>? FacetChanged;

    public MatchPattern ToPattern()
    {
        var scope = PatternScope.None;
        if (ScopeNotes) scope |= PatternScope.Notes;
        if (ScopeChords) scope |= PatternScope.Chords;
        if (ScopeHandShapes) scope |= PatternScope.HandShapes;
        if (ScopeAnchors) scope |= PatternScope.Anchors;

        return new MatchPattern
        {
            Id = Id,
            Name = Name,
            Scope = scope,
            Grouping = SelectedGrouping.Value,
            Fret = SelectedFretRule.Value,
            Strings = SelectedStringRule.Value,
            MatchSize = MatchSize,
            MatchSoundKind = MatchSoundKind,
            Sustain = SelectedSustain.Value,
            MatchFingering = MatchFingering,
            MatchChordName = MatchChordName,
            MatchArpeggio = MatchArpeggio,
            Rhythm = SelectedRhythm.Value,
            MatchBeatOffset = MatchBeatOffset,
            Techniques = Techniques.Where(t => t.IsSelected).Aggregate(TechniqueFacet.None, (all, t) => all | t.Facet),
            MatchAnchorWidth = MatchAnchorWidth,
            MatchAnchorSpan = MatchAnchorSpan,
            Range = SelectedRange.Value,
            MaxFretDistance = MaxFretDistance,
            Companions = Companions.Where(c => c.IsSelected)
                .Aggregate(PatternCompanions.None, (all, c) => all | c.Companion),
            SelectKey = SelectKey,
            NextKey = NextKey,
            PreviousKey = PreviousKey
        };
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Outputs of a facet change: re-raising on them would recurse through the dialog's preview.
        if (e.PropertyName is nameof(Summary) or nameof(MatchCountLabel)) return;

        if (e.PropertyName is nameof(SelectKey) or nameof(NextKey) or nameof(PreviousKey))
        {
            OnPropertyChanged(nameof(KeyLabel));
            OnPropertyChanged(nameof(HasKeys));
        }

        if (e.PropertyName == nameof(SelectedGrouping)) OnPropertyChanged(nameof(IsRun));
        if (e.PropertyName == nameof(ScopeAnchors)) OnPropertyChanged(nameof(ShowsAnchorFacets));
        if (e.PropertyName is nameof(ScopeChords) or nameof(ScopeHandShapes)) OnPropertyChanged(nameof(ShowsTemplateFacets));

        if (e.PropertyName is nameof(KeyLabel) or nameof(HasKeys) or nameof(IsRun) or nameof(ShowsAnchorFacets)
            or nameof(ShowsTemplateFacets))
        {
            return;
        }

        FacetChanged?.Invoke(this, e.PropertyName == nameof(Name)
            ? PatternFacetChangedEventArgs.DescriptionOnly
            : PatternFacetChangedEventArgs.Counting);
    }

    private T Watch<T>(T toggle) where T : ObservableObject
    {
        toggle.PropertyChanged += (_, _) => FacetChanged?.Invoke(this, PatternFacetChangedEventArgs.Counting);
        return toggle;
    }

    private static PatternChoice<T> Pick<T>(IReadOnlyList<PatternChoice<T>> options, T value) where T : struct, Enum =>
        options.FirstOrDefault(o => o.Value.Equals(value)) ?? options[0];
}
