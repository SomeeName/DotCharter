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
    private static readonly ArrangementIssueType[] FingeringTypes =
    [
        ArrangementIssueType.FingerSetForOpenString,
        ArrangementIssueType.FingerMissingOnFrettedString,
        ArrangementIssueType.FirstFingerNotOnLowestFret
    ];

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

    [Fact]
    public void LinkedLandingDoesNotUseItsTemplateFingering()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Frets = new Dictionary<int, int> { [0] = 3, [1] = 5 }
        });
        level.Notes.Add(new GuitarNote
        {
            StringIndex = 0, Position = FractionalPosition.Zero, Sustain = new FractionalPosition(1), LinkNext = true
        });
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);

        Assert.DoesNotContain(Run(arr, Beats(4)), issue => issue.Type == ArrangementIssueType.FingerMissingOnFrettedString);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void LinkLandingsSkipAllThreeFingeringFindingsButOtherUsesKeepThem(bool chordSource, int otherUse)
    {
        var (arrangement, landing) = LinkedFingeringFaults(chordSource);
        var level = arrangement.Levels[0];
        Chord? ordinary = null;
        HandShape? shape = null;
        if (otherUse == 1)
        {
            ordinary = landing.Clone();
            ordinary.Position = new FractionalPosition(2);
            level.Chords.Add(ordinary);
        }
        else if (otherUse == 2)
        {
            shape = new HandShape { Position = landing.Position, EndPosition = new FractionalPosition(2), TemplateId = 0 };
            level.HandShapes.Add(shape);
        }

        var issues = Run(arrangement, Beats(4));
        if (otherUse == 0)
        {
            Assert.Empty(issues);
            return;
        }

        Assert.Equal(FingeringTypes, issues.Select(issue => issue.Type));
        Assert.All(issues, issue =>
        {
            Assert.Same(ordinary, issue.Chord);
            Assert.Same(shape, issue.HandShape);
            Assert.Equal((ordinary?.Position ?? shape!.Position).GetPositionMs(Beats(4)), issue.TimeMs);
        });
    }

    [Fact]
    public void ALinkLandingStillReportsStructuralOverlap()
    {
        var (arrangement, landing) = LinkedFingeringFaults(chordSource: false);
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = landing.Position, StringIndex = 1, Fret = 5 });

        var issue = Assert.Single(Run(arrangement, Beats(4)));
        Assert.Equal(ArrangementIssueType.OverlappingNotes, issue.Type);
        Assert.Same(landing, issue.Chord);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void OrdinaryAndFingerlessGripsKeepTheSameCapoAwareFindings(int capo, bool fingerless)
    {
        var (arrangement, ordinary) = LinkedFingeringFaults(chordSource: false);
        arrangement.Levels[0].Notes.Clear();
        arrangement.Capo = capo;
        arrangement.ChordTemplates[0].Frets[0] = capo;
        ordinary.ChordNotes[0].Fret = capo;
        arrangement.ChordTemplates[0].Fingerless = fingerless;

        var issues = Run(arrangement, Beats(4));
        if (fingerless) Assert.Empty(issues);
        else Assert.Equal(FingeringTypes, issues.Select(issue => issue.Type));
    }

    private static (Arrangement Arrangement, Chord Landing) LinkedFingeringFaults(bool chordSource)
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var template = new ChordTemplate
        {
            Id = 0, Frets = new Dictionary<int, int> { [0] = 0, [1] = 5, [2] = 7 },
            Fingers = new Dictionary<int, int> { [0] = 1, [2] = 1 }
        };
        arrangement.ChordTemplates.Add(template);
        var landing = new Chord { TemplateId = 0, Position = new FractionalPosition(1) };
        foreach (var (s, fret) in template.Frets) landing.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        level.Chords.Add(landing);
        if (chordSource)
        {
            var source = new Chord { TemplateId = 1 };
            source.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true });
            source.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
            arrangement.ChordTemplates.Add(new ChordTemplate
            {
                Id = 1, Frets = new Dictionary<int, int> { [1] = 5, [2] = 7 },
                Fingers = new Dictionary<int, int> { [1] = 1, [2] = 3 }
            });
            level.Chords.Add(source);
        }
        else
        {
            level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true });
        }

        return (arrangement, landing);
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
