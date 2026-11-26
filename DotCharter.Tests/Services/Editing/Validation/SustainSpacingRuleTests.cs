using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D90/D91 (<see cref="SustainSpacingRule" />): a sustain ending closer to the next note than a
///     32nd note, a note sustain under 5 ms, and the D15/D90/D91 fixes (<see cref="ChartSpacingFix" />).
///     The grid is 120 BPM 4/4, one beat 500 ms, so a 32nd note is 1/8 beat.
/// </summary>
public sealed class SustainSpacingRuleTests
{
    private static List<BeatMarker> Beats(int denominator = 4) =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, NoteDenominator = denominator })];

    private static FractionalPosition At(int numerator, int denominator) => new(0, new Fraction(numerator, denominator));

    private static GuitarNote Note(FractionalPosition position, FractionalPosition sustain, int stringIndex = 1,
        bool linkNext = false) =>
        new() { Position = position, StringIndex = stringIndex, Fret = 5, Sustain = sustain, LinkNext = linkNext };

    private static List<ArrangementIssue> Run(Arrangement arr, List<BeatMarker>? beats = null)
    {
        var issues = new List<ArrangementIssue>();
        new SustainSpacingRule().Validate(ArrangementValidationContext.Build(arr, beats ?? Beats(), null), issues);
        return issues;
    }

    /// <summary>A sustained note at beat 0 and a plain note on another string at beat 1.</summary>
    private static (Arrangement Arr, GuitarNote Held) HeldThenNext(FractionalPosition sustain)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var held = Note(At(0, 1), sustain);
        arr.Levels[0].Notes.Add(held);
        arr.Levels[0].Notes.Add(Note(At(1, 1), FractionalPosition.Zero, stringIndex: 3));
        return (arr, held);
    }

    [Fact]
    public void TheCodesAreD90AndD91_WarningsThatRunByDefault()
    {
        Assert.Equal("D90", ArrangementIssueType.SustainTooCloseToNext.GetCode());
        Assert.Equal("D91", ArrangementIssueType.SustainUnderFiveMs.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.SustainTooCloseToNext.GetSeverity());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.SustainUnderFiveMs.GetSeverity());
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(HeldThenNext(At(15, 16)).Arr, Beats(), null);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.SustainTooCloseToNext);
    }

    /// <summary>The spacing clean-up's 1/16 beat is too close; a 32nd note (1/8 beat) is the gap.</summary>
    [Theory]
    [InlineData(16, true)]
    [InlineData(15, true)]
    [InlineData(14, false)]
    [InlineData(8, false)]
    public void ASustainNeedsA32ndNoteBeforeTheNextNote(int sixteenths, bool warns)
    {
        var issues = Run(HeldThenNext(At(sixteenths, 16)).Arr);

        if (!warns) Assert.Empty(issues);
        else Assert.Equal(["1/8"], Assert.Single(issues, i => i.Type == ArrangementIssueType.SustainTooCloseToNext).MessageArgs);
    }

    [Fact]
    public void TheGapIsA32ndNote_SoAnEighthNoteBeatAsksForAQuarterBeat()
    {
        Assert.Equal(At(1, 8), SpacingGap.ThirtySecondNote(At(0, 1), Beats()));
        Assert.Equal(At(1, 4), SpacingGap.ThirtySecondNote(At(0, 1), Beats(denominator: 8)));
        Assert.Single(Run(HeldThenNext(At(7, 8)).Arr, Beats(denominator: 8)));
    }

    /// <summary>A Guitar Pro strum keeps a strike sustain of min(1/4 beat, half its written length):
    /// eighth and sixteenth strums leave their 32nd note, a 32nd strum has no room and is left alone.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void StrumsWithTheirStrikeSustain_AreNeverReported(int perBeat)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var strike = FractionalPosition.Min(At(1, 4), At(1, perBeat * 2));
        for (var i = 0; i < perBeat * 2; i++)
        {
            var chord = new Chord { Position = At(i, perBeat) };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Sustain = strike });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = strike });
            arr.Levels[0].Chords.Add(chord);
        }

        Assert.Empty(Run(arr));
    }

    /// <summary>Triplets: a sustain 1/16 beat short of the next triplet is reported and trimmed to
    /// leave exactly 1/8 beat, one undo step, after which nothing is reported.</summary>
    [Fact]
    public void ATripletSustain_IsTrimmedToLeaveA32ndNote_AndTheTrimUndoes()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        var notes = new[] { Note(At(0, 3), At(13, 48)), Note(At(1, 3), At(13, 48), 2), Note(At(2, 3), FractionalPosition.Zero) };
        foreach (var note in notes) level.Notes.Add(note);
        var findings = Run(arr);
        Assert.Equal(2, findings.Count);

        var fix = ChartSpacingFix.For(level, findings[0], Beats())!;
        fix.Execute();
        Assert.Equal(At(5, 24), notes[0].Sustain);
        fix.Undo();
        Assert.Equal(At(13, 48), notes[0].Sustain);

        foreach (var finding in findings) ChartSpacingFix.For(level, finding, Beats())!.Execute();
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AChordSustain_IsTrimmedOnEveryNote()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var chord = ChordHandShapePlannerTests.Chord(0, 0, (0, 3), (1, 5));
        chord.ChordNotes[0].Sustain = At(15, 16);
        chord.ChordNotes[1].Sustain = At(1, 2);
        arr.Levels[0].Chords.Add(chord);
        arr.Levels[0].Notes.Add(Note(At(1, 1), FractionalPosition.Zero, stringIndex: 3));

        ChartSpacingFix.For(arr.Levels[0], Assert.Single(Run(arr)), Beats())!.Execute();

        Assert.Equal([At(7, 8), At(1, 2)], arr.Levels[0].Chords[0].ChordNotes.Select(n => n.Sustain));
    }

    [Fact]
    public void ALinkedNote_IsNotReported()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Notes.Add(Note(At(0, 1), At(15, 16), linkNext: true));
        arr.Levels[0].Notes.Add(Note(At(1, 1), FractionalPosition.Zero));

        Assert.Empty(Run(arr));
    }

    /// <summary>A slide's sustain is the slide's length, so it is not reported.</summary>
    [Fact]
    public void ASlide_IsNotReported()
    {
        var (arr, held) = HeldThenNext(At(15, 16));
        held.UnpitchedSlide = true;

        Assert.Empty(Run(arr));
    }

    /// <summary>At 120 BPM 1/128 beat is 3.9 ms (a leftover), 1/64 beat 7.8 ms (kept).</summary>
    [Theory]
    [InlineData(128, true)]
    [InlineData(64, false)]
    public void ASustainUnder5Ms_IsReportedAndRemoved(int denominator, bool warns)
    {
        var (arr, held) = HeldThenNext(At(1, denominator));
        var issues = Run(arr);
        if (!warns)
        {
            Assert.Empty(issues);
            return;
        }

        var finding = Assert.Single(issues);
        Assert.Equal(ArrangementIssueType.SustainUnderFiveMs, finding.Type);
        Assert.True(IssueFixViewModel.CanOffer(finding, arr, arr.Levels[0], Beats()));
        IssueFixViewModel.DefaultFix(finding, arr, arr.Levels[0], Beats())!.Execute();
        Assert.Equal(FractionalPosition.Zero, held.Sustain);
    }

    /// <summary>D15's fix ends a shape that stops 1/16 beat short of the next chord a 32nd note
    /// before it.</summary>
    [Fact]
    public void AHandShapeGapFinding_IsTrimmedToA32ndNote()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, (0, 3), (1, 5)));
        level.Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, (0, 5), (1, 7)));
        level.HandShapes.Add(new HandShape { Position = At(0, 1), EndPosition = At(31, 16) });
        level.HandShapes.Add(new HandShape { Position = At(2, 1), EndPosition = At(3, 1) });
        var issues = new List<ArrangementIssue>();
        new HandShapeGapRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);

        IssueFixViewModel.DefaultFix(Assert.Single(issues), arr, level, Beats())!.Execute();

        Assert.Equal(At(15, 8), level.HandShapes[0].EndPosition);
    }
}
