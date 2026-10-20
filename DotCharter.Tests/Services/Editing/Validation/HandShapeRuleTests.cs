using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     I46 (<see cref="HandShapeRule" />) and the shape lookup it shares with
///     <see cref="HandShapeContentRule" />'s E05.
///     <para>
///         <b>The two rules disagreed about a NEGATIVE template id.</b>
///         <c>HandShapeRule</c> asked the raw id index with no guard, while
///         <c>HandShapeContentRule</c> asked
///         <c>ChordTemplateResolver.FindById</c>, which is null for any negative
///         id. <c>HandShape.TemplateId</c> defaults to <c>-1</c> and
///         <c>ChordTemplate.Id</c> does too — so on an untemplated shape E05 went
///         silent while I46 judged it against whichever UNNUMBERED template
///         happened to sit first in the collection. A false negative and a false
///         positive on the same input.
///     </para>
/// </summary>
public sealed class HandShapeRuleTests
{
    private static List<BeatMarker> Beats(int count = 8) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Run(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new HandShapeRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    /// <summary>
    ///     A template that trips I46: no first finger and no thumb, and its
    ///     finger-2 string sits on the fret the active anchor names.
    /// </summary>
    private static ChordTemplate AnchorMismatchTemplate(int id)
    {
        var template = new ChordTemplate
        {
            Id = id,
            Name = "NoIndex",
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7 },
            Fingers = new Dictionary<int, int> { [0] = 2, [1] = 3, [2] = 4 }
        };
        return template;
    }

    /// <summary>One level, one anchor on fret 5, and one hand shape at beat 1.</summary>
    private static Arrangement WithOneShape(int shapeTemplateId, params ChordTemplate[] templates)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        foreach (var template in templates) arr.ChordTemplates.Add(template);

        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5, 4));
        level.HandShapes.Add(new HandShape
        {
            TemplateId = shapeTemplateId,
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(3)
        });
        arr.Levels.Add(level);
        return arr;
    }

    /// <summary>
    ///     <b>The false positive.</b> The shape declares NO grip, and the only
    ///     template in the arrangement is unnumbered — so the raw id lookup found
    ///     it under the sentinel and judged the shape against a template it never
    ///     named.
    /// </summary>
    [Fact]
    public void AnUntemplatedShape_IsNotJudgedAgainstAnUnnumberedTemplate()
    {
        var arr = WithOneShape(-1, AnchorMismatchTemplate(-1));

        var issues = Run(arr);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }

    /// <summary>…and the silence is not the fix either: the shape states
    /// nothing, and that is what gets reported (D07).</summary>
    [Fact]
    public void AnUntemplatedShape_IsReportedAsDeclaringNoGrip()
    {
        var arr = WithOneShape(-1, AnchorMismatchTemplate(-1));

        var summary = Assert.Single(Run(arr), i => i.Type == ArrangementIssueType.HandShapeWithoutTemplate);

        Assert.Contains("1 hand shape", summary.Message);
        Assert.False(summary.HasTime); // one summary per arrangement, like D05/D06
    }

    /// <summary>Vacuity keeper: a shape that DOES name the same template is
    /// still judged, and is not reported as gripless.</summary>
    [Fact]
    public void AShapeThatNamesItsTemplate_IsStillJudged()
    {
        var arr = WithOneShape(0, AnchorMismatchTemplate(0));

        var issues = Run(arr);

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.HandShapeWithoutTemplate);
    }

    /// <summary>
    ///     The same disagreement one level in: a NEIGHBOUR shape excuses the
    ///     finding when it covers the anchor with a first finger. Read through
    ///     the raw index, an untemplated neighbour picked up the unnumbered
    ///     template's finger 1 and excused a real finding — a false negative
    ///     produced by the false positive's own lookup.
    /// </summary>
    [Fact]
    public void AnUntemplatedNeighbour_CannotExcuseTheFinding()
    {
        var arr = WithOneShape(0, AnchorMismatchTemplate(0), new ChordTemplate
        {
            Id = -1, // unnumbered: what the raw index returns for the sentinel
            Name = "Barre",
            Frets = new Dictionary<int, int> { [0] = 5 },
            Fingers = new Dictionary<int, int> { [0] = 1 }
        });

        arr.Levels[0].HandShapes.Add(new HandShape
        {
            TemplateId = -1,
            Position = new FractionalPosition(3),
            EndPosition = new FractionalPosition(4)
        });

        var issues = Run(arr);

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }

    /// <summary>Vacuity keeper for the neighbour rule: a neighbour that really
    /// does name a first-finger template still excuses it.</summary>
    [Fact]
    public void ATemplatedNeighbourWithAFirstFinger_StillExcusesTheFinding()
    {
        var arr = WithOneShape(0, AnchorMismatchTemplate(0), new ChordTemplate
        {
            Id = 1,
            Name = "Barre",
            Frets = new Dictionary<int, int> { [0] = 5 },
            Fingers = new Dictionary<int, int> { [0] = 1 }
        });

        arr.Levels[0].HandShapes.Add(new HandShape
        {
            TemplateId = 1,
            Position = new FractionalPosition(3),
            EndPosition = new FractionalPosition(4)
        });

        var issues = Run(arr);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }

    /// <summary>
    ///     End to end through the service: the two rules that judge a hand shape
    ///     now read its template the same way, so the untemplated shape is
    ///     neither judged by one and skipped by the other — it is reported.
    /// </summary>
    [Fact]
    public void BothShapeRules_AgreeOnAnUntemplatedShape()
    {
        var arr = WithOneShape(-1, AnchorMismatchTemplate(-1));
        var note = new GuitarNote
        {
            Position = new FractionalPosition(2),
            StringIndex = 4, // a string the unnumbered template never declares
            Fret = 9
        };
        arr.Levels[0].Notes.Add(note);

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.SoundViolatesHandShape);
        Assert.Single(issues, i => i.Type == ArrangementIssueType.HandShapeWithoutTemplate);
    }
}
