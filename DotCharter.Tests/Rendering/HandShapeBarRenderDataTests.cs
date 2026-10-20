using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     What the 2D hand-shape bar is told (#480), from the chart to the snapshot:
///     the template's NAME and ARPEGGIO answer through the template table, and the
///     WARNING state from the inspector's own issue list
///     (<see cref="HandShapeFindings.Collect" /> over
///     <see cref="ArrangementValidationService.Validate" />) — never a second rule
///     implementation. <see cref="HandShapeLayerBarStyleTests" /> covers what the
///     layer draws from it.
/// </summary>
public sealed class HandShapeBarRenderDataTests
{
    private const int GripId = 0;
    private const int ArpeggioId = 1;
    private const int CleanId = 2;

    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>
    ///     Four bars, one per state: [0,2) a three-string grip with a sound under
    ///     it on a fret the grip does not name (E05); [2,4) an arpeggio spelled
    ///     ONLY with <c>ForceArpeggioInRs</c> (the RS-XML import's flag); [4,6) no
    ///     template (D09); [6,8) a clean grip.
    /// </summary>
    private static Arrangement Chart()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = GripId, Name = "A5add9",
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7 }
        });
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = ArpeggioId, Name = "Am", ForceArpeggioInRs = true,
            Frets = new Dictionary<int, int> { [1] = 7, [2] = 7, [3] = 5 }
        });
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = CleanId, Name = "G",
            Frets = new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 }
        });

        var level = new ChartLevel();
        level.HandShapes.Add(Bar(GripId, 0, 2));
        level.HandShapes.Add(Bar(ArpeggioId, 2, 4));
        level.HandShapes.Add(Bar(-1, 4, 6));
        level.HandShapes.Add(Bar(CleanId, 6, 8));
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 6 });
        arr.Levels.Add(level);
        return arr;
    }

    private static HandShape Bar(int templateId, int fromBeat, int toBeat) =>
        new()
        {
            TemplateId = templateId,
            Position = new FractionalPosition(fromBeat),
            EndPosition = new FractionalPosition(toBeat)
        };

    private static FrameSnapshot Snapshot(Arrangement arr, IReadOnlySet<HandShape> findings, List<BeatMarker> beats)
    {
        var level = arr.Levels[0];
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arr.ChordTemplates,
            HandShapeFindings = findings
        };
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 5000, 0, snapshot, ctx, []);
        return snapshot;
    }

    [Fact]
    public void TheValidatorsFindings_NameTheE05AndD09Bars_AndNoOthers()
    {
        var arr = Chart();
        var shapes = arr.Levels[0].HandShapes;

        var findings = HandShapeFindings.Collect(
            new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats()));

        Assert.Contains(shapes[0], findings); // E05
        Assert.Contains(shapes[2], findings); // D09
        Assert.DoesNotContain(shapes[1], findings);
        Assert.DoesNotContain(shapes[3], findings);
    }

    [Fact]
    public void NameArpeggioAndWarning_ReachTheRenderData()
    {
        var arr = Chart();
        var beats = Beats();
        var findings = HandShapeFindings.Collect(
            new ArrangementValidationService(new FakeAppLogger()).Validate(arr, beats));

        var snapshot = Snapshot(arr, findings, beats);

        Assert.Equal(4, snapshot.HandShapeCount);
        bool[] warned = [true, false, true, false];
        Assert.Equal(warned, Enumerable.Range(0, 4).Select(i => snapshot.HandShapes[i].HasFinding));

        Assert.Equal("A5add9", snapshot.HandShapes[0].Label);
        Assert.False(snapshot.HandShapes[0].IsArpeggio);

        // ForceArpeggioInRs ALONE — the RS-XML spelling a single-flag reader misses.
        Assert.Equal("Am", snapshot.HandShapes[1].Label);
        Assert.True(snapshot.HandShapes[1].IsArpeggio);

        // No template: nothing to print, however stale HandShape.Name is.
        Assert.Null(snapshot.HandShapes[2].Label);
        Assert.Equal("G", snapshot.HandShapes[3].Label);
    }

    [Fact]
    public void WithNoFindings_NoBarIsWarned()
    {
        var arr = Chart();
        var snapshot = Snapshot(arr, HandShapeFindings.None, Beats());

        Assert.All(Enumerable.Range(0, snapshot.HandShapeCount),
            i => Assert.False(snapshot.HandShapes[i].HasFinding));
    }

    [Fact]
    public void ACommittedBarsLabel_IsItsTemplatesName_KeyedOnTheId()
    {
        // Ids are contiguous by convention only; the validator looks templates up
        // by Id, so the bar's name must too or it names another chord's grip.
        var arr = Chart();
        arr.ChordTemplates.Clear();
        arr.ChordTemplates.Add(new ChordTemplate { Id = 7, Name = "E" });
        arr.ChordTemplates.Add(new ChordTemplate { Id = GripId, Name = "D" });
        arr.ChordTemplates.Add(new ChordTemplate { Id = CleanId, Name = "C" });
        var snapshot = Snapshot(arr, HandShapeFindings.None, Beats());

        Assert.Equal("D", snapshot.HandShapes[0].Label); // id 0 sits at position 1
        Assert.Null(snapshot.HandShapes[1].Label);       // id 1: no template carries it
        Assert.Equal("C", snapshot.HandShapes[3].Label); // id 2 sits where it should
    }

    [Fact]
    public void APreviewBarsLabel_IsTheGripTheGestureNamed()
    {
        var arr = Chart();
        var beats = Beats();
        var preview = Bar(GripId, 0, 2);
        preview.Name = "A5add9 557xxx";
        var level = arr.Levels[0];
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arr.ChordTemplates,
            PreviewHandShapes = [preview]
        };
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 5000, 0, snapshot, ctx, []);

        Assert.Equal(1, snapshot.PreviewHandShapeCount);
        Assert.Equal("A5add9 557xxx", snapshot.PreviewHandShapes[0].Label);
    }

    [Fact]
    public void Collect_ReadsE05AndD09Only_ByReference()
    {
        var a = Bar(0, 0, 1);
        var b = Bar(0, 0, 1); // field-equal to a, a different bar
        var c = Bar(-1, 1, 2);
        ArrangementIssue[] issues =
        [
            new(ArrangementIssueType.SoundViolatesHandShape, "e05", 0, HandShape: a),
            new(ArrangementIssueType.HandShapeWithoutTemplate, "d09", HandShapes: [c]),
            // I46 points at a shape too, but it is not one of the bar's states.
            new(ArrangementIssueType.FingeringAnchorMismatch, "i46", 0, HandShape: b),
            new(ArrangementIssueType.ChordOutsideHandShape, "d11", 0)
        ];

        var found = HandShapeFindings.Collect(issues);

        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.Contains(c, found);
        Assert.DoesNotContain(b, found);
        Assert.Same(HandShapeFindings.None, HandShapeFindings.Collect([]));
    }

    /// <summary>
    ///     The two glue lines the data path depends on, read from source because
    ///     <c>InspectorIssuesViewModel</c> needs a <c>ChartEditorViewModel</c> and <c>ChartRenderer</c>
    ///     is a live control (the same reason <c>InspectorIssueRebuildTests</c> is
    ///     a lint). The Issues tab publishes from the FULL result list — before the
    ///     easier-level filter, since the timeline draws whichever level is on
    ///     screen — and the renderer hands the published set to the snapshot.
    /// </summary>
    [Fact]
    public void TheInspectorPublishes_AndTheRendererPassesItOn()
    {
        var inspector = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor",
            "Inspector", "InspectorIssuesViewModel.cs"));
        var publish = inspector.IndexOf("_chartEditor.PublishHandShapeFindings(HandShapeFindings.Collect(results));",
            StringComparison.Ordinal);
        var filter = inspector.IndexOf("results = [.. results.Where(i => !easier.Contains(i))];", StringComparison.Ordinal);
        Assert.True(publish >= 0, "RecomputeIssues must publish the hand-shape findings");
        Assert.True(filter > publish, "the findings are collected before the easier-level filter");

        var renderer = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls",
            "ChartRenderer.cs"));
        Assert.Contains("_snapshotCtx.HandShapeFindings = editor.HandShapeFindings;", renderer);
        // The bar names are drawn text, so the atlas must be given their glyphs.
        Assert.Contains("atlas.RegisterCharacters(_snapshot.Templates[i].Name);", renderer);
    }

    [Fact]
    public void SuggestedBars_ReachTheRenderData_WindowedToTheView_AndMarkedSuggested()
    {
        // #491: the snapshot only windows the list the Services latch computed.
        var arr = Chart();
        var level = arr.Levels[0];
        var beats = Beats(40);
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arr.ChordTemplates,
            SuggestedHandShapes =
            [
                new HandShape { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), Name = "C" },
                new HandShape { Position = new FractionalPosition(30), EndPosition = new FractionalPosition(32), Name = "G" }
            ]
        };
        var snapshot = new FrameSnapshot();

        FrameSnapshot.BuildSnapshot(0, 5000, 0, snapshot, ctx, []);

        var s = Assert.Single(snapshot.SuggestedHandShapes.Take(snapshot.SuggestedHandShapeCount));
        Assert.True(s.IsSuggested);
        Assert.Equal("C", s.Label);
        Assert.Equal((4000.0, 4500.0), (s.Time, s.EndTime));
        Assert.All(Enumerable.Range(0, snapshot.HandShapeCount), i => Assert.False(snapshot.HandShapes[i].IsSuggested));
    }
}
