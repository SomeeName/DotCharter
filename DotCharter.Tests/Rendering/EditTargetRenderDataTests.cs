using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Resolver → render snapshot for the bulk-edit preview (#485): the set the
///     chord panel's <see cref="ChordApplyScope.Preview" /> answers, published as a
///     <see cref="ChordUseHighlight" />, reaches the 2D render data as
///     <see cref="ChordFlags.EditTarget" /> / <see cref="RenderHandShape.IsEditTarget" />
///     on EXACTLY its members and on nothing else. <see cref="EditTargetLayerTests" />
///     covers what the layers draw from the flags.
/// </summary>
public sealed class EditTargetRenderDataTests
{
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplate _a5;
    private readonly ChordTemplate _b5;
    private readonly List<BeatMarker> _beats =
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    public EditTargetRenderDataTests()
    {
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        _a5 = Template(0, "A5", 5);
        _b5 = Template(1, "B5", 7);
    }

    [Theory]
    [InlineData(ChordApplyRung.Selected)]
    [InlineData(ChordApplyRung.EveryUse)]
    [InlineData(ChordApplyRung.SameGrip)]
    public void ThePreviewSet_FlagsExactlyItsMembers(ChordApplyRung rung)
    {
        var level = _arr.Levels[0];
        var a = Chord(0, _a5, 0);
        var b = Chord(0, _a5, 2);
        Chord(0, _b5, 4);
        Chord(0, _a5, 6);
        var bar = Bar(0, _a5, 0, 3);
        Bar(0, _b5, 4, 5);
        Chord(1, _a5, 0); // another level: in the set, never in this level's snapshot
        var scope = ChordApplyScope.Prepare(_arr, _a5, new ChordApplySounds([a], [bar]),
            new ChordApplySounds([a, b], [bar]), level.Chords);
        var preview = scope.Preview(rung);
        var highlight = ChordUseHighlight.From(preview.Chords, preview.HandShapes, _arr);

        var snapshot = Snapshot(highlight);

        Assert.Equal(level.Chords.Count, snapshot.ChordCount);
        for (var i = 0; i < snapshot.ChordCount; i++)
        {
            var flagged = (snapshot.Chords[i].Flags & ChordFlags.EditTarget) != 0;
            Assert.Equal(preview.Chords.Contains(level.Chords[i]), flagged);
        }

        for (var i = 0; i < snapshot.HandShapeCount; i++)
            Assert.Equal(preview.HandShapes.Contains(level.HandShapes[i]), snapshot.HandShapes[i].IsEditTarget);

        Assert.Contains(snapshot.Chords.Take(snapshot.ChordCount), c => (c.Flags & ChordFlags.EditTarget) != 0);
    }

    [Fact]
    public void WithNoPreview_NothingIsFlagged()
    {
        Chord(0, _a5, 0);
        Bar(0, _a5, 0, 2);

        var snapshot = Snapshot(ChordUseHighlight.None);

        Assert.All(snapshot.Chords.Take(snapshot.ChordCount), c => Assert.Equal(ChordFlags.None, c.Flags & ChordFlags.EditTarget));
        Assert.All(snapshot.HandShapes.Take(snapshot.HandShapeCount), h => Assert.False(h.IsEditTarget));
    }

    /// <summary>The flag is by REFERENCE: a field-equal chord that is not a member
    /// is not marked.</summary>
    [Fact]
    public void AFieldEqualChordThatIsNotAMember_IsNotFlagged()
    {
        var member = Chord(0, _a5, 0);
        var twin = member.Clone();
        twin.Position = new FractionalPosition(1);
        _arr.Levels[0].Chords.Add(twin);

        var snapshot = Snapshot(ChordUseHighlight.From([member], [], _arr));

        Assert.NotEqual(ChordFlags.None, snapshot.Chords[0].Flags & ChordFlags.EditTarget);
        Assert.Equal(ChordFlags.None, snapshot.Chords[1].Flags & ChordFlags.EditTarget);
    }

    /// <summary>
    ///     The AI chart pass review (#506): a live anchor the review would replace is flagged
    ///     by REFERENCE, and the anchors it would add are projected on their own, windowed,
    ///     each spanning to the next proposal.
    /// </summary>
    [Fact]
    public void TheReviewsAnchors_FlagTheReplacedOne_AndProjectTheProposals()
    {
        var level = _arr.Levels[0];
        var kept = new FretAnchor(new FractionalPosition(0), 5, 4);
        var replaced = new FretAnchor(new FractionalPosition(8), 3, 4);
        level.Anchors.Add(kept);
        level.Anchors.Add(replaced);
        var twin = new FretAnchor(new FractionalPosition(8), 3, 4); // field-equal, not live
        var proposed = new[] { new FretAnchor(new FractionalPosition(8), 3, 6), new FretAnchor(new FractionalPosition(12), 7, 4) };
        var highlight = ChordUseHighlight.ForLevel(_arr, level, [], [], [replaced, twin], proposed);

        var snapshot = Snapshot(highlight);

        Assert.Equal(2, snapshot.AnchorCount);
        Assert.False(snapshot.Anchors[0].IsEditTarget);
        Assert.True(snapshot.Anchors[1].IsEditTarget);
        Assert.Equal(2, snapshot.ProposedAnchorCount);
        Assert.Equal((4000.0, 6000.0, (byte)6), (snapshot.ProposedAnchors[0].Time, snapshot.ProposedAnchors[0].EndTime,
            snapshot.ProposedAnchors[0].Width));
        Assert.Equal((6000.0, (byte)7), (snapshot.ProposedAnchors[1].Time, snapshot.ProposedAnchors[1].Fret));
        Assert.All(snapshot.ProposedAnchors.Take(2), a => Assert.True(a.IsEditTarget));
        Assert.Single(highlight.Anchors);

        // Out of the view: none projected.
        var far = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(20000, 30000, 20000, far, Context(highlight), []);
        Assert.Equal(0, far.ProposedAnchorCount);

        Assert.Equal(0, Snapshot(ChordUseHighlight.None).ProposedAnchorCount);
    }

    /// <summary>The glue the data path depends on: the renderer hands the editor's
    /// published set to the snapshot (a live control, hence read from source, as
    /// <c>HandShapeBarRenderDataTests</c> does for the findings).</summary>
    [Fact]
    public void TheRendererPassesThePublishedSetOn()
    {
        var renderer = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls",
            "ChartRenderer.cs"));
        Assert.Contains("_snapshotCtx.EditTargetChords = editor.UseHighlight.Chords;", renderer);
        Assert.Contains("_snapshotCtx.EditTargetHandShapes = editor.UseHighlight.HandShapes;", renderer);
        Assert.Contains("_snapshotCtx.EditTargetAnchors = editor.UseHighlight.Anchors;", renderer);
        Assert.Contains("_snapshotCtx.ProposedAnchors = editor.UseHighlight.On(", renderer);
    }

    private FrameSnapshot Snapshot(ChordUseHighlight highlight)
    {
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 15000, 0, snapshot, Context(highlight), []);
        return snapshot;
    }

    private SnapshotUpdateContext Context(ChordUseHighlight highlight)
    {
        var level = _arr.Levels[0];
        return new SnapshotUpdateContext
        {
            ActiveBeats = _beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = _arr.ChordTemplates,
            EditTargetChords = highlight.Chords,
            EditTargetHandShapes = highlight.HandShapes,
            EditTargetAnchors = highlight.Anchors,
            ProposedAnchors = highlight.On(level.Chords).ProposedAnchors
        };
    }

    private ChordTemplate Template(int id, string name, int root)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        t.Frets[0] = root;
        t.Frets[1] = root + 2;
        t.Frets[2] = root + 2;
        _arr.ChordTemplates.Add(t);
        return t;
    }

    private Chord Chord(int level, ChordTemplate template, int beat)
    {
        var chord = new Chord { TemplateId = template.Id, Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        _arr.Levels[level].Chords.Add(chord);
        return chord;
    }

    private HandShape Bar(int level, ChordTemplate template, int start, int end)
    {
        var bar = new HandShape
        {
            TemplateId = template.Id, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end)
        };
        _arr.Levels[level].HandShapes.Add(bar);
        return bar;
    }
}
