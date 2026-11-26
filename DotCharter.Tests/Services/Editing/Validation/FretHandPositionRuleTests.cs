using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D50-D56 (<see cref="FretHandPositionRule" />): an FHP before the first note or inside COUNT, between
///     notes, inside a sustain, a hand move on a hammer-on target, an anchor above fret 21 or 24, a bend
///     that falls to the pinky; and the move onto the next note (<see cref="FhpPlacement.MoveToNextSound" />).
///     The grid is 120 BPM, one beat 500 ms; COUNT starts at beat 0 and the phrase "riff" at beat 2.
/// </summary>
public sealed class FretHandPositionRuleTests
{
    private static readonly ArrangementIssueType[] Ours =
    [
        ArrangementIssueType.AnchorBeforeFirstNote, ArrangementIssueType.AnchorBetweenNotes,
        ArrangementIssueType.AnchorInsideSustain, ArrangementIssueType.AnchorOnHammerOnTarget,
        ArrangementIssueType.AnchorFretAbove21, ArrangementIssueType.AnchorFretAbove24,
        ArrangementIssueType.BendNeedsPinky
    ];

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static FractionalPosition Pos(double beats) => ChordHandShapePlannerTests.Pos(beats);

    private static Arrangement Arr()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.EventPoints.Add(new EventPoint { Position = Pos(0), Phrase = "COUNT" });
        arr.EventPoints.Add(new EventPoint { Position = Pos(2), Phrase = "riff" });
        return arr;
    }

    private static GuitarNote Note(double beat, int fret, double sustain = 0, HopoType hopo = HopoType.None) => new()
    {
        Position = Pos(beat), StringIndex = 1, Fret = fret, Sustain = Pos(sustain), Hopo = hopo
    };

    private static FretAnchor Anchor(double beat, int fret) => new(Pos(beat), fret, 4);

    internal static List<ArrangementIssue> Run(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger(), [new FretHandPositionRule()]).Validate(arr, Beats(), null)
            .Where(i => Ours.Contains(i.Type))
            .ToList();

    [Fact]
    public void TheCodesAreD50ToD56_AndOnlyAbove24IsImportant()
    {
        Assert.Equal(["D50", "D51", "D52", "D53", "D54", "D55", "D56"], Ours.Select(t => t.GetCode()));
        Assert.All(Ours.Where(t => t != ArrangementIssueType.AnchorFretAbove24),
            t => Assert.Equal(ArrangementIssueSeverity.Warning, t.GetSeverity()));
        Assert.Equal(ArrangementIssueSeverity.Important, ArrangementIssueType.AnchorFretAbove24.GetSeverity());
    }

    [Fact]
    public void TheRuleRunsByDefault()
    {
        var arr = Arr();
        arr.Levels[0].Notes.Add(Note(3, 5));
        arr.Levels[0].Anchors.Add(Anchor(0, 5));

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats(), null);

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.AnchorBeforeFirstNote);
    }

    [Fact]
    public void AnAnchorInsideCount_IsBeforeTheFirstNote_ButOnThePhraseStartOrTheNoteItIsClean()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(3, 5));
        var early = Anchor(0.5, 5);
        level.Anchors.Add(early);

        var finding = Assert.Single(Run(arr));
        Assert.Equal(ArrangementIssueType.AnchorBeforeFirstNote, finding.Type);
        Assert.Same(early, finding.Anchor);
        Assert.Equal(250, finding.TimeMs);

        early.Position = Pos(2);
        Assert.Empty(Run(arr));
        early.Position = Pos(3);
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AnAnchorBetweenNotesOrAfterTheLast_IsD51_UnlessAPhraseStartsThere()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5));
        level.Notes.Add(Note(4, 7));
        level.Anchors.Add(Anchor(2, 5));
        var between = Anchor(3, 7);
        level.Anchors.Add(between);
        var after = Anchor(6, 7);
        level.Anchors.Add(after);

        var findings = Run(arr);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(ArrangementIssueType.AnchorBetweenNotes, f.Type));
        Assert.Contains(findings, f => f.Anchor == between);
        Assert.Contains(findings, f => f.Anchor == after);

        arr.EventPoints.Add(new EventPoint { Position = Pos(3), Phrase = "fill" });
        arr.EventPoints.Add(new EventPoint { Position = Pos(6), Phrase = "outro" });
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AnAnchorOnAHandShapeStartBeforeItsChord_IsClean_TheHandFormsTheGripThere()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5));
        level.Notes.Add(Note(4, 7));
        level.HandShapes.Add(new HandShape { Position = Pos(3.5), EndPosition = Pos(4.5) });
        level.Anchors.Add(Anchor(2, 5));
        level.Anchors.Add(Anchor(3.5, 7));

        Assert.Empty(Run(arr));

        level.HandShapes.Clear();
        Assert.Equal(ArrangementIssueType.AnchorBetweenNotes, Assert.Single(Run(arr)).Type);
    }

    [Fact]
    public void AnAnchorInsideARingingSustain_IsD52_AndOnTheSustainEndItIsBetweenNotes()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5, sustain: 2));
        level.Notes.Add(Note(6, 7));
        level.Anchors.Add(Anchor(2, 5));
        var inside = Anchor(3, 7);
        level.Anchors.Add(inside);

        var finding = Assert.Single(Run(arr));
        Assert.Equal(ArrangementIssueType.AnchorInsideSustain, finding.Type);

        inside.Position = Pos(4);
        Assert.Equal(ArrangementIssueType.AnchorBetweenNotes, Assert.Single(Run(arr)).Type);
    }

    [Fact]
    public void AnAvoidableHandMoveOnAHammerOnTarget_IsD53_ButNotWithoutAMoveForAStretchOrOnAPullOff()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        var picked = Note(2, 6);
        level.Notes.Add(picked);
        var target = Note(3, 8, hopo: HopoType.HammerOn);
        level.Notes.Add(target);
        level.Anchors.Add(Anchor(2, 5));
        var shift = Anchor(3, 6);
        level.Anchors.Add(shift);

        var finding = Assert.Single(Run(arr));
        Assert.Equal(ArrangementIssueType.AnchorOnHammerOnTarget, finding.Type);
        Assert.Same(shift, finding.Anchor);

        shift.Fret = 5;
        Assert.Empty(Run(arr));

        // From fret 5 the new FHP on 6 cannot hold the picked note: the stretch needs the move.
        shift.Fret = 6;
        picked.Fret = 5;
        Assert.Empty(Run(arr));
        picked.Fret = 6;

        shift.Fret = 6;
        target.Hopo = HopoType.PullOff;
        Assert.Empty(Run(arr));
    }

    [Theory]
    [InlineData(21, null)]
    [InlineData(22, ArrangementIssueType.AnchorFretAbove21)]
    [InlineData(24, ArrangementIssueType.AnchorFretAbove21)]
    [InlineData(25, ArrangementIssueType.AnchorFretAbove24)]
    public void AnAnchorAbove21_Warns_AndAbove24_IsInvalid(int fret, ArrangementIssueType? expected)
    {
        var arr = Arr();
        arr.Levels[0].Notes.Add(Note(2, Math.Min(fret, 24)));
        arr.Levels[0].Anchors.Add(Anchor(2, fret));

        var findings = Run(arr);

        if (expected == null) Assert.Empty(findings);
        else Assert.Equal(expected, Assert.Single(findings).Type);
    }

    [Fact]
    public void ABendThreeFretsAboveTheFhp_NeedsThePinky_UnlessAnotherFingerIsStated()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Anchors.Add(Anchor(2, 5));
        var bent = Note(2, 8, sustain: 1);
        bent.BendValues.Add(new BendValue(Pos(2.5), 1));
        level.Notes.Add(bent);

        var finding = Assert.Single(Run(arr));
        Assert.Equal(ArrangementIssueType.BendNeedsPinky, finding.Type);
        Assert.Same(bent, finding.Note);

        bent.LeftHandFinger = 3;
        Assert.Empty(Run(arr));

        bent.Fret = 7;
        bent.LeftHandFinger = 4;
        Assert.Single(Run(arr));

        bent.LeftHandFinger = -1;
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void ABentChordNoteOnFingerFour_IsOneFindingOnTheChord()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Frets = { [0] = 5, [1] = 8 }, Fingers = { [0] = 1, [1] = 4 }
        });
        var chord = ChordHandShapePlannerTests.Chord(2, 1, (0, 5), (1, 8));
        chord.TemplateId = 0;
        chord.ChordNotes[1].BendValues.Add(new BendValue(Pos(2.5), 1));
        level.Chords.Add(chord);
        level.Anchors.Add(Anchor(2, 5));

        var finding = Assert.Single(Run(arr));
        Assert.Equal(ArrangementIssueType.BendNeedsPinky, finding.Type);
        Assert.Same(chord, finding.Chord);
    }

    [Fact]
    public void TheMove_PutsAnAnchorBetweenNotesOnTheNextNote_AndUndoes()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5));
        level.Notes.Add(Note(4, 7));
        level.Anchors.Add(Anchor(2, 5));
        var between = Anchor(3, 7);
        level.Anchors.Add(between);

        var move = FhpPlacement.MoveToNextSound(level, Assert.Single(Run(arr)));
        Assert.NotNull(move);
        move.Execute();
        Assert.Equal(Pos(4), between.Position);
        Assert.Equal(7, between.Fret);
        Assert.Empty(Run(arr));

        move.Undo();
        Assert.Equal(Pos(3), between.Position);
        Assert.Single(Run(arr));
    }

    [Fact]
    public void TheMove_TakesAnAnchorInsideCountToTheFirstNote()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(3, 5));
        var early = Anchor(0, 5);
        level.Anchors.Add(early);

        FhpPlacement.MoveToNextSound(level, Assert.Single(Run(arr)))!.Execute();

        Assert.Equal(Pos(3), early.Position);
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void TheMove_RemovesAnAnchorTheNextNoteAlreadyHas_OrOneAfterTheLastNote()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5));
        level.Notes.Add(Note(4, 7));
        level.Anchors.Add(Anchor(2, 5));
        level.Anchors.Add(Anchor(3, 6));
        level.Anchors.Add(Anchor(4, 7));
        level.Anchors.Add(Anchor(6, 7));

        foreach (var finding in Run(arr))
        {
            var fix = FhpPlacement.MoveToNextSound(level, finding);
            Assert.IsType<RemoveAnchorsAction>(fix);
            fix.Execute();
        }

        Assert.Equal([Pos(2), Pos(4)], level.Anchors.Select(a => a.Position).Order());
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void TheMove_IsWithheld_WhenTheNextNoteIsAHammerOnTarget_OrForAnotherFinding()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Notes.Add(Note(2, 5));
        level.Notes.Add(Note(4, 7, hopo: HopoType.HammerOn));
        level.Anchors.Add(Anchor(2, 5));
        level.Anchors.Add(Anchor(3, 26));

        var findings = Run(arr);
        Assert.Null(FhpPlacement.MoveToNextSound(level, Assert.Single(findings, f => f.Type == ArrangementIssueType.AnchorBetweenNotes)));
        Assert.Null(FhpPlacement.MoveToNextSound(level, Assert.Single(findings, f => f.Type == ArrangementIssueType.AnchorFretAbove24)));
    }
}
