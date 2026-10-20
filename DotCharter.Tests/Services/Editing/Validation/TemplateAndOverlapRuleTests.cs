using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <see cref="TemplateAndOverlapRule" />'s fingering half (L06/L07/L08).
///     <para>
///         <b>The rule collected its templates from <c>level.Chords</c> only</b>,
///         so a template that only a HAND SHAPE points at was never
///         fingering-checked — the three wrong-fingering rules could not reach
///         it at all. That is precisely the population most likely to be wrong:
///         the let-ring shapes <c>GpHandShapeBuilder.FindOrCreateTemplate</c>
///         mints carry fingering guessed by
///         <c>ChordTemplateFingerSetter.SetSuggestedFingers</c> (74.6% exact),
///         and nothing else in the chart references them.
///     </para>
/// </summary>
public sealed class TemplateAndOverlapRuleTests
{
    private static List<BeatMarker> Beats(int count, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static IReadOnlyList<ArrangementIssue> Run(Arrangement arr, IList<BeatMarker> beats)
    {
        var issues = new List<ArrangementIssue>();
        new TemplateAndOverlapRule().Validate(ArrangementValidationContext.Build(arr, beats, null), issues);
        return issues;
    }

    /// <summary>A finger on an OPEN string (L06) and nothing else, so a single
    /// assertion pins which check ran.</summary>
    private static ChordTemplate FingerOnAnOpenString(int id)
    {
        var template = new ChordTemplate { Id = id, Name = "LetRing" };
        template.Frets[0] = 0;
        template.Fingers[0] = 1; // L06: a finger on a string that is not fretted
        template.Frets[1] = 5;
        template.Fingers[1] = 3;
        return template;
    }

    [Fact]
    public void ATemplateOnlyAHandShapeUses_IsStillFingeringChecked()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(FingerOnAnOpenString(0));
        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape
        {
            TemplateId = 0,
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(2)
        });
        arr.Levels.Add(level);

        var issues = Run(arr, Beats(8));

        // Before the fix this arrangement produced ZERO issues: no chord
        // referenced template 0, so it never entered the first-use map.
        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.FingerSetForOpenString);
        Assert.Same(level.HandShapes[0], issue.HandShape);
        Assert.Null(issue.Chord);
        Assert.Equal(500.0, issue.TimeMs!.Value, 3); // anchored at the SHAPE's position
        Assert.Equal(0, issue.StringIndex);
    }

    /// <summary>Vacuity keeper: the chord path must still anchor on the CHORD,
    /// at the chord's position — the shape sweep only fills gaps.</summary>
    [Fact]
    public void AChordUsingTheSameTemplate_StillOwnsTheFinding()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(FingerOnAnOpenString(0));
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape
        {
            TemplateId = 0,
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(2)
        });
        arr.Levels.Add(level);

        var issues = Run(arr, Beats(8));

        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.FingerSetForOpenString);
        Assert.Same(chord, issue.Chord);
        Assert.Null(issue.HandShape);
        Assert.Equal(1000.0, issue.TimeMs!.Value, 3);
    }

    /// <summary>…and one template is still judged ONCE however many shapes use
    /// it.</summary>
    [Fact]
    public void ATemplateIsJudgedOnce_HoweverManyShapesUseIt()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(FingerOnAnOpenString(0));
        var level = new ChartLevel();
        for (var i = 1; i <= 4; i++)
        {
            level.HandShapes.Add(new HandShape
            {
                TemplateId = 0,
                Position = new FractionalPosition(i),
                EndPosition = new FractionalPosition(i + 1)
            });
        }

        arr.Levels.Add(level);

        var issues = Run(arr, Beats(8));

        Assert.Single(issues, i => i.Type == ArrangementIssueType.FingerSetForOpenString);
    }

    /// <summary>
    ///     <b>The other half of the same guard: <c>TemplateId &lt; 0 ⇒
    ///     continue</c>.</b> L06/L07/L08 skipped a chord left on the <c>-1</c>
    ///     sentinel entirely, which reads as "this chord's fingering is fine".
    ///     The shape it plays is knowable — it is the template
    ///     <c>ChartSpacingEnforcer</c> repoints it at on the next save.
    /// </summary>
    [Fact]
    public void AChordOnTheSentinel_IsFingeringCheckedByTheShapeItPlays()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(FingerOnAnOpenString(0));
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        arr.Levels.Add(level);

        var issues = Run(arr, Beats(8));

        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.FingerSetForOpenString);
        Assert.Same(chord, issue.Chord);
    }

    /// <summary>An untemplated shape declares no grip, so there is no fingering
    /// to judge — it must not resolve onto somebody else's template.</summary>
    [Fact]
    public void AShapeWithNoTemplate_AddsNoFingeringFinding()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(FingerOnAnOpenString(0));
        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape
        {
            TemplateId = -1,
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(2)
        });
        arr.Levels.Add(level);

        Assert.DoesNotContain(Run(arr, Beats(8)), i => i.Type == ArrangementIssueType.FingerSetForOpenString);
    }
}
