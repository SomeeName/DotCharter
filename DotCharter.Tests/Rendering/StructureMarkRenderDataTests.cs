using DotCharter.Rendering.Data;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The AI chat's sections &amp; phrases review (#561) reaches the 2D ribbon and the song map: the
///     live markers a pending change replaces are flagged per field, the proposed markers are
///     projected for the layer to draw in the review colour, and nothing else is marked.
/// </summary>
public sealed class StructureMarkRenderDataTests
{
    private readonly Arrangement _arr = new() { Name = "Lead" };
    private readonly List<BeatMarker> _beats =
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    public StructureMarkRenderDataTests()
    {
        _arr.Levels.Add(new ChartLevel());
        _arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro", Phrase = "intro" });
        _arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "verse" });
    }

    [Fact]
    public void Replaced_markers_are_flagged_per_field_and_proposed_ones_are_projected()
    {
        var highlight = ChordUseHighlight.ForEvents(_arr, [], [_arr.EventPoints[0]],
            [new EventPoint { Position = new FractionalPosition(4), Section = "verse" }]);

        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 15000, 0, snapshot, Context(highlight), []);

        var first = snapshot.EventPoints.Take(snapshot.EventPointCount).Single(e => e.Time == 0);
        Assert.True(first.PhraseIsEditTarget);
        Assert.False(first.SectionIsEditTarget);
        Assert.False(snapshot.EventPoints.Take(snapshot.EventPointCount).Single(e => e.Time == 4000).PhraseIsEditTarget);
        Assert.Equal(1, snapshot.ProposedEventPointCount);
        Assert.Equal((2000.0, "verse", true), (snapshot.ProposedEventPoints[0].Time, snapshot.ProposedEventPoints[0].Section,
            snapshot.ProposedEventPoints[0].SectionIsEditTarget));

        // Out of the view: not projected. No review: nothing marked.
        var far = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(20000, 30000, 20000, far, Context(highlight), []);
        Assert.Equal(0, far.ProposedEventPointCount);
        var none = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 15000, 0, none, Context(ChordUseHighlight.None), []);
        Assert.Equal(0, none.ProposedEventPointCount);
        Assert.DoesNotContain(none.EventPoints.Take(none.EventPointCount), e => e.PhraseIsEditTarget || e.SectionIsEditTarget);
    }

    [Fact]
    public void The_song_map_marks_each_replaced_and_proposed_marker_from_the_top_edge()
    {
        var marks = new List<SongMapMark>();
        SongMapMarks.AppendEvents([_arr.EventPoints[1], new EventPoint { Position = new FractionalPosition(4) }], _beats,
            0.01, marks);

        Assert.Equal([40.0, 20.0], marks.Select(m => m.Bounds.Center.X));
        Assert.All(marks, m => Assert.Equal(0, m.Bounds.Top));
    }

    /// <summary>The glue: the renderer and the song map hand the published markers on (live controls,
    /// hence read from source, as <see cref="EditTargetRenderDataTests" /> does).</summary>
    [Fact]
    public void The_renderer_and_the_song_map_pass_the_published_markers_on()
    {
        var root = TestPaths.RepoRoot();
        var renderer = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "Controls", "ChartRenderer.cs"));
        Assert.Contains("_snapshotCtx.ProposedEventPoints) =", renderer);
        Assert.Contains("editor.UseHighlight.EventsOn(sourceArr);", renderer);
        var map = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "Controls", "MinimapOverviewControl.cs"));
        Assert.Contains("editor.UseHighlight.EventsOn(editor.ActiveArrangement)", map);
        Assert.Contains("SongMapMarks.AppendEvents(", map);
    }

    private SnapshotUpdateContext Context(ChordUseHighlight highlight)
    {
        var level = _arr.Levels[0];
        var (sections, phrases, proposed) = highlight.EventsOn(_arr);
        return new SnapshotUpdateContext
        {
            ActiveBeats = _beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = _arr.ChordTemplates,
            EventPoints = _arr.EventPoints,
            EditTargetSections = sections,
            EditTargetPhrases = phrases,
            ProposedEventPoints = proposed
        };
    }
}
