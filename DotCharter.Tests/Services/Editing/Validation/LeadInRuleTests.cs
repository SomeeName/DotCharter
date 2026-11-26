using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D42-D44 (<see cref="LeadInRule" />): about 10 s of lead-in, about 2 s after the last note, and an empty
///     count measure before the first note. 120 BPM, 4/4: one beat 500 ms, one measure 2 s.
/// </summary>
public sealed class LeadInRuleTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static Arrangement WithNoteAt(double beat, double sustain = 0)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = ChordHandShapePlannerTests.Pos(beat), StringIndex = 1, Fret = 5,
            Sustain = ChordHandShapePlannerTests.Pos(sustain)
        });
        return arr;
    }

    private static List<ArrangementIssueType> Run(Arrangement arr, double? audioMs = null)
    {
        var issues = new List<ArrangementIssue>();
        new LeadInRule().Validate(ArrangementValidationContext.Build(arr, Beats(), audioMs), issues);
        return [.. issues.Select(i => i.Type)];
    }

    [Fact]
    public void TheCodesAreD42ToD44_Warnings()
    {
        Assert.Equal("D42", ArrangementIssueType.LeadInShort.GetCode());
        Assert.Equal("D43", ArrangementIssueType.TailShort.GetCode());
        Assert.Equal("D44", ArrangementIssueType.NoCountMeasure.GetCode());
        Assert.All(new[] { ArrangementIssueType.LeadInShort, ArrangementIssueType.TailShort, ArrangementIssueType.NoCountMeasure },
            t => Assert.Equal(ArrangementIssueSeverity.Warning, t.GetSeverity()));
    }

    [Fact]
    public void TheRuleRunsByDefault()
    {
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(WithNoteAt(4), Beats(), null);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LeadInShort);
    }

    [Theory]
    [InlineData(1, false)] // 0.5 s: D02's error, not this suggestion
    [InlineData(2, true)] // 1 s
    [InlineData(19.5, true)] // 9.75 s
    [InlineData(20, false)] // exactly 10 s
    public void TheLeadInSuggestion(double beat, bool expected) =>
        Assert.Equal(expected, Run(WithNoteAt(beat)).Contains(ArrangementIssueType.LeadInShort));

    [Fact]
    public void TheHardLimitStaysOneSecond()
    {
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(WithNoteAt(4), Beats(), null);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FirstNoteTooEarly);
    }

    [Theory]
    [InlineData(21000, true)] // the note ends at 20.5 s: 0.5 s
    [InlineData(22499, true)]
    [InlineData(22500, false)] // exactly 2 s
    public void TheTailSuggestion(double audioMs, bool expected) =>
        Assert.Equal(expected, Run(WithNoteAt(40, sustain: 1), audioMs).Contains(ArrangementIssueType.TailShort));

    [Fact]
    public void NoTailFinding_WithoutAudio() =>
        Assert.DoesNotContain(ArrangementIssueType.TailShort, Run(WithNoteAt(40)));

    [Theory]
    [InlineData(3.5, true)] // inside measure 1
    [InlineData(4, false)] // on the second downbeat: measure 1 is empty
    [InlineData(22, false)]
    public void TheCountMeasureSuggestion(double beat, bool expected) =>
        Assert.Equal(expected, Run(WithNoteAt(beat)).Contains(ArrangementIssueType.NoCountMeasure));

    [Fact]
    public void TheCountMeasureFinding_NeverTouchesTheBeatMap()
    {
        var beats = Beats();
        var issues = new List<ArrangementIssue>();
        new LeadInRule().Validate(ArrangementValidationContext.Build(WithNoteAt(1), beats, null), issues);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoCountMeasure);
        Assert.Equal(64, beats.Count);
        Assert.Equal(0.0, beats[0].Time);
    }

    [Fact]
    public void AnEmptyArrangement_HasNoFindings() =>
        Assert.Empty(Run(ChordHandShapePlannerTests.Arr(), 1000));
}
