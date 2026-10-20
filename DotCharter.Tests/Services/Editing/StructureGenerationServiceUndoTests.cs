using DotCharter.Services.Editing.Structure;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The structure pass promises ONE undoable step, and it merged its
///     COUNT / END / noguitar onto an existing marker with a bare assignment,
///     outside the history action it recorded — which carried only the NEW
///     markers.
///     <para>
///         So Ctrl+Z removed the added markers and left every merged one wearing
///         the phrase the generator wrote, and when every placement merged (no
///         additions at all) no node was recorded whatsoever: the chart changed
///         with nothing to undo, while the pass still reported a non-empty
///         result. Same shape as the phrase-end stretch in
///         <c>InsertVocalAtPlayhead</c>.
///     </para>
/// </summary>
public sealed class StructureGenerationServiceUndoTests
{
    private static List<BeatMarker> Beats(int count = 200)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        return beats;
    }

    private sealed record Rig(ProjectState State, HistoryService History, StructureGenerationService Service,
        Arrangement Arrangement);

    /// <summary>A chart with a silent lead-in and a silent tail, so the planner
    /// always has a COUNT and an END to place.</summary>
    private static Rig Build(Action<Arrangement>? seed = null)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        for (var beat = 20; beat < 40; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        seed?.Invoke(arrangement);
        project.Arrangements.Add(arrangement);

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;

        var history = new HistoryService(logger);
        return new Rig(state, history, new StructureGenerationService(state, history, logger), arrangement);
    }

    [Fact]
    public void OneUndo_TakesBackTheMergeOntoAnExistingMarkerToo()
    {
        // Find where the pass wants to put its markers, then plant a bare
        // section marker on that spot so the placement has to MERGE.
        var probe = Build();
        Assert.False(probe.Service.Generate().IsEmpty);
        var mergePosition = probe.Arrangement.EventPoints
            .First(ep => string.Equals(ep.Phrase, "COUNT", StringComparison.Ordinal)).Position;

        EventPoint existing = null!;
        var rig = Build(a =>
        {
            existing = new EventPoint { Position = mergePosition, Section = "intro", Events = "B0" };
            a.EventPoints.Add(existing);
        });

        Assert.False(rig.Service.Generate().IsEmpty);
        Assert.Equal("COUNT", existing.Phrase);

        Assert.True(rig.History.CanUndo);
        rig.History.Undo();

        Assert.Equal("", existing.Phrase);
        Assert.Equal("intro", existing.Section);
        // The action transfers Position/Phrase/Section only, on purpose.
        Assert.Equal("B0", existing.Events);
        Assert.False(rig.History.CanUndo);
    }

    [Fact]
    public void APassThatOnlyMerges_IsStillOneUndoableNode()
    {
        var probe = Build();
        Assert.False(probe.Service.Generate().IsEmpty);
        var planned = probe.Arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase))
            .Select(ep => ep.Position)
            .ToList();
        Assert.NotEmpty(planned);

        var markers = new List<EventPoint>();
        var rig = Build(a =>
        {
            foreach (var position in planned)
            {
                var marker = new EventPoint { Position = position, Section = "s" };
                markers.Add(marker);
                a.EventPoints.Add(marker);
            }
        });

        // Every placement merges, so the old body recorded NO history node at
        // all and mutated the chart anyway.
        Assert.False(rig.Service.Generate().IsEmpty);
        Assert.Contains(markers, m => !string.IsNullOrWhiteSpace(m.Phrase));
        Assert.True(rig.History.CanUndo);

        rig.History.Undo();
        Assert.All(markers, m => Assert.Equal("", m.Phrase));
    }

    [Fact]
    public void GeneratingTwice_IsANoOpTheSecondTime()
    {
        var rig = Build();
        Assert.False(rig.Service.Generate().IsEmpty);
        var markerCount = rig.Arrangement.EventPoints.Count;

        Assert.True(rig.Service.Generate().IsEmpty);
        Assert.Equal(markerCount, rig.Arrangement.EventPoints.Count);
    }
}
