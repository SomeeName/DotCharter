using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Rocksmith draws an ACCENTED note (its "staccato") with a white rim, and an
///     accented chord with a white frame. The accent flag reached the 3D frame
///     snapshot, but neither <c>NoteHeadRenderer</c> nor <c>ChordRenderer</c>
///     read it, so accents looked exactly like plain notes on the 3D highway.
///     These read the staged quads: an accent adds white geometry that frames
///     the head, staged BEFORE it so the coloured head stays on top.
/// </summary>
public sealed class AccentRenderingTests
{
    private const int QuadFloats = QuadBatcher.FloatsPerVertex * 4;

    [Fact]
    public void A_plain_fretted_note_stages_no_white()
    {
        var quads = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 5, NoteFlags.None)));
        Assert.DoesNotContain(quads, q => q.IsWhite);
    }

    [Fact]
    public void An_accented_fretted_note_gets_a_white_rim_larger_than_and_behind_its_head()
    {
        var quads = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 5, NoteFlags.Accent)));

        var rim = Assert.Single(quads, q => q.IsWhite);
        var head = quads.First(q => q.Mode == 3f && !q.IsWhite && q.Index > rim.Index);
        Assert.True(rim.Width > head.Width * 1.1f, $"rim {rim.Width} does not frame head {head.Width}");
    }

    [Fact]
    public void An_accented_open_note_gets_a_taller_white_bar_behind_its_bar()
    {
        var plain = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 0, NoteFlags.None)));
        Assert.DoesNotContain(plain, q => q.IsWhite);

        var quads = Stage(b => NoteHeadRenderer.Render(NoteContext(b, fret: 0, NoteFlags.Accent)));
        var rim = Assert.Single(quads, q => q.IsWhite);
        var bar = quads[rim.Index + 1];
        Assert.True(rim.Height > bar.Height * 1.5f, $"accent bar {rim.Height} vs bar {bar.Height}");
    }

    [Fact]
    public void An_accented_chord_gets_a_white_frame_and_rims_and_a_plain_one_none()
    {
        var plain = Stage(b => ChordRenderer.Render(ChordContext(b, ChordFlags.None), new RenderResources()));
        Assert.DoesNotContain(plain, q => q.IsWhite);

        var quads = Stage(b => ChordRenderer.Render(ChordContext(b, ChordFlags.Accent), new RenderResources()));
        // Six frame quads (two floor halves, two top halves, two posts) plus a
        // rim behind each of the two heads.
        Assert.True(quads.Count(q => q.IsWhite || q.IsWhiteGradient) >= 8,
            $"only {quads.Count(q => q.IsWhite || q.IsWhiteGradient)} white quads for an accented chord");
        Assert.Equal(2, quads.Count(q => q.IsWhite && q.Mode == 3f));
    }

    private static List<Quad> Stage(Action<QuadBatcher> render)
    {
        var batcher = new QuadBatcher(null!);
        render(batcher);
        var data = batcher.StagedVertices.ToArray();
        var quads = new List<Quad>();
        for (var q = 0; q < data.Length; q += QuadFloats) quads.Add(new Quad(quads.Count, data.AsSpan(q, QuadFloats)));
        return quads;
    }

    private sealed class Quad
    {
        public Quad(int index, ReadOnlySpan<float> f)
        {
            Index = index;
            Mode = MathF.Round(f[12]);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var allWhite = true;
            var anyWhite = false;
            for (var v = 0; v < 4; v++)
            {
                var o = v * QuadBatcher.FloatsPerVertex;
                minX = Math.Min(minX, f[o]);
                maxX = Math.Max(maxX, f[o]);
                minY = Math.Min(minY, f[o + 1]);
                maxY = Math.Max(maxY, f[o + 1]);
                var white = f[o + 3] == 1f && f[o + 4] == 1f && f[o + 5] == 1f && f[o + 6] == 1f;
                allWhite &= white;
                anyWhite |= white;
            }

            Width = maxX - minX;
            Height = maxY - minY;
            IsWhite = allWhite;
            IsWhiteGradient = anyWhite && !allWhite;
        }

        public int Index { get; }
        public float Mode { get; }
        public float Width { get; }
        public float Height { get; }
        public bool IsWhite { get; }
        public bool IsWhiteGradient { get; }
    }

    private static RenderContext NoteContext(QuadBatcher batcher, int fret, NoteFlags flags) => Context(batcher,
        new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Notes = [new RenderNote { Time = 1_400, StringIndex = 2, Fret = (byte)fret, Flags = flags }],
            NoteCount = 1
        });

    private static RenderContext ChordContext(QuadBatcher batcher, ChordFlags flags) => Context(batcher,
        new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Chords = [new RenderChord { Time = 1_400, TemplateId = -1, NoteOffset = 0, NoteCount = 2, Flags = flags }],
            ChordCount = 1,
            ChordNotes =
            [
                new RenderChordNote { StringIndex = 1, Fret = 5 },
                new RenderChordNote { StringIndex = 2, Fret = 5 }
            ],
            ChordNoteCount = 2
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
