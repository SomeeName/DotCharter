using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Rocksmith ties a single note to the highway floor with glowing stems: one under a
///     fretted head, one under each end of an open note's bar. Chords have none (the
///     chord box frames them). These read the staged quads: a stem is a thin vertical
///     quad standing on <see cref="NoteStemRenderer.FloorY" />.
/// </summary>
public sealed class NoteStemTests
{
    private const int QuadFloats = QuadBatcher.FloatsPerVertex * 4;

    [Fact]
    public void A_fretted_note_stands_on_one_stem_under_its_head_and_a_floor_marker_across_its_fret()
    {
        var quads = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 5)));

        var stem = Assert.Single(Stems(quads));
        Assert.Equal(FretboardConstants.GetLinearFretMiddle(5), stem.CenterX, 3);
        var marker = Assert.Single(quads, q => q.IsFlat && q.MinY > NoteStemRenderer.FloorY);
        Assert.Equal(FretboardConstants.GetLinearFretPosition(4), marker.MinX, 3);
        Assert.Equal(FretboardConstants.GetLinearFretPosition(5), marker.MaxX, 3);
    }

    [Fact]
    public void An_open_note_gets_a_stem_under_each_end_of_its_bar_and_no_floor_marker()
    {
        var quads = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 0)));

        var stems = Stems(quads).OrderBy(q => q.CenterX).ToList();
        Assert.Equal(2, stems.Count);
        // The bar is staged right after its stems (the stems go first so the bar covers them).
        var bar = quads[quads.FindLastIndex(q => q.IsStem) + 1];
        Assert.Equal(RenderingConstants.OpenNoteBarVertexType, bar.Mode);
        Assert.Equal(bar.MinX, stems[0].MinX, 3);
        Assert.Equal(bar.MaxX, stems[1].MaxX, 3);
        Assert.DoesNotContain(quads, q => q.IsFlat && q.MinY > NoteStemRenderer.FloorY);
    }

    [Fact]
    public void A_chord_has_no_stems_and_no_floor_markers()
    {
        var quads = Stage(b => ChordRenderer.Render(ChordContext(b), new RenderResources()));

        Assert.Contains(quads, q => q.Mode == 3f);
        Assert.Empty(Stems(quads));
        Assert.DoesNotContain(quads, q => q.IsFlat && Math.Abs(q.MinY - NoteStemRenderer.FloorY -
            NoteStemRenderer.FloorMarkerLift) < 1e-4f);
    }

    private static IEnumerable<Quad> Stems(List<Quad> quads) => quads.Where(q => q.IsStem);

    private static List<Quad> Stage(Action<QuadBatcher> render)
    {
        var batcher = new QuadBatcher(null!);
        render(batcher);
        var data = batcher.StagedVertices.ToArray();
        var quads = new List<Quad>();
        for (var q = 0; q < data.Length; q += QuadFloats) quads.Add(new Quad(data.AsSpan(q, QuadFloats)));
        return quads;
    }

    private sealed class Quad
    {
        public Quad(ReadOnlySpan<float> f)
        {
            Mode = MathF.Round(f[12]);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (var v = 0; v < 4; v++)
            {
                var o = v * QuadBatcher.FloatsPerVertex;
                minX = Math.Min(minX, f[o]);
                maxX = Math.Max(maxX, f[o]);
                minY = Math.Min(minY, f[o + 1]);
                maxY = Math.Max(maxY, f[o + 1]);
            }

            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        public float Mode { get; }
        public float MinX { get; }
        public float MaxX { get; }
        public float MinY { get; }
        public float MaxY { get; }
        public float CenterX => (MinX + MaxX) / 2f;
        public bool IsFlat => MaxY - MinY < 1e-4f;

        public bool IsStem => Math.Abs(MaxX - MinX - 2 * NoteStemRenderer.StemHalfWidth) < 1e-4f &&
                              Math.Abs(MinY - NoteStemRenderer.FloorY) < 1e-4f && MaxY > MinY;
    }

    private static RenderContext NoteContext(QuadBatcher batcher, int fret) => Context(batcher,
        new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Notes = [new RenderNote { Time = 1_400, StringIndex = 2, Fret = (byte)fret }],
            NoteCount = 1
        });

    private static RenderContext ChordContext(QuadBatcher batcher) => Context(batcher,
        new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Chords = [new RenderChord { Time = 1_400, TemplateId = -1, NoteOffset = 0, NoteCount = 3 }],
            ChordCount = 1,
            ChordNotes =
            [
                new RenderChordNote { StringIndex = 0, Fret = 0 },
                new RenderChordNote { StringIndex = 1, Fret = 5 },
                new RenderChordNote { StringIndex = 2, Fret = 5 }
            ],
            ChordNoteCount = 3
        });

    private static RenderContext Context(QuadBatcher batcher, FrameSnapshot snapshot) => new()
    {
        Snapshot = snapshot,
        TimeMs = 1_000,
        BoardWidth = 10f,
        QuadBatcher = batcher,
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.DefaultScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.DefaultDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.DefaultDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(
            HighwaySpeedPolicy.DefaultScrollSpeed, HighwaySpeedPolicy.DefaultDrawDistanceZ)
    };
}
