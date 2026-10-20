namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     #618: one mistake, one finding. A grip fingered 2-4 with finger 2 on the anchor fret is
///     I46's "missing first-finger barre" — and E04 inferred the index a fret below and reported
///     every chord under the shape again, offering the other fix. On the verified corpus that was
///     68 of 242 E04 findings. I46 owns the case now and its sentence names both fixes; E04 still
///     reports whenever I46 does not, so the case is never left with no finding at all.
/// </summary>
public sealed class IndexAnchorFindingOwnerTests
{
    private static List<BeatMarker> Beats(int count = 8) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>
    ///     FHP 5; a double stop fingered 2-4 on frets 5 and 7 (no finger 1, finger 2 on the anchor
    ///     fret) strummed at beat 2, under a hand shape of the same grip when <paramref name="shape" />.
    /// </summary>
    private static (Arrangement Arr, ChartLevel Level) Build(bool shape = true)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { [3] = 5, [4] = 7 },
            Fingers = new Dictionary<int, int> { [3] = 2, [4] = 4 }
        });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5, 4));
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 7 });
        level.Chords.Add(chord);
        if (shape)
        {
            level.HandShapes.Add(new HandShape
            {
                TemplateId = 0, Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3)
            });
        }

        return (arr, level);
    }

    private static IReadOnlyList<ArrangementIssue> Validate(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());

    [Fact]
    public void UnderAShapeI46Reports_TheMistakeIsOneFinding_ThatNamesBothFixes()
    {
        var (arr, _) = Build();

        var issues = Validate(arr);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        var finding = Assert.Single(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
        Assert.Contains("missing 1st-finger barre", finding.Message, StringComparison.Ordinal);
        Assert.Contains("index finger on fret 4 (inferred)", finding.Message, StringComparison.Ordinal);
        Assert.Contains("re-finger it with the index on fret 5, or move the anchor to fret 4", finding.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoHandShape_E04StillReportsIt()
    {
        var (arr, _) = Build(shape: false);

        var issues = Validate(arr);

        Assert.Single(issues, i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }

    /// <summary>
    ///     A neighbouring shape that barres with finger 1 under the same anchor EXCUSES I46. E04 then
    ///     owns the disagreement again — the deferral follows what I46 actually reports, not the shape.
    /// </summary>
    [Fact]
    public void WhenANeighbourExcusesI46_E04StillReportsIt()
    {
        var (arr, level) = Build();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1,
            Frets = new Dictionary<int, int> { [3] = 5, [4] = 5 },
            Fingers = new Dictionary<int, int> { [3] = 1, [4] = 1 }
        });
        level.HandShapes.Add(new HandShape
        {
            TemplateId = 1, Position = new FractionalPosition(3), EndPosition = new FractionalPosition(4)
        });

        var issues = Validate(arr);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
        Assert.Single(issues, i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>A shape on ANOTHER grip over the chord is another decision: it does not silence E04.</summary>
    [Fact]
    public void AShapeOnAnotherGrip_DoesNotSilenceE04()
    {
        var (arr, level) = Build();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1,
            Frets = new Dictionary<int, int> { [3] = 5, [4] = 7, [5] = 7 },
            Fingers = new Dictionary<int, int> { [3] = 2, [4] = 3, [5] = 4 }
        });
        level.HandShapes[0].TemplateId = 1;

        var issues = Validate(arr);

        Assert.Single(issues, i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.Single(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }
}
