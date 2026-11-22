using DotCharter.Rendering.Data;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Themes;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     #785: a REVIEW capture of ghost gems in the 2D timeline WITHOUT a GPU — the real lane,
///     hand-shape and note layers into a <see cref="TimelineBatcher" />, rasterized on the CPU with
///     the timeline shader's own rules (<c>timeline2d_general.frag</c>: AA circle, ring, glyph,
///     plain). A G5 strummed with its octave ghosted, then a tapped harmonic at fret 17 over the held
///     5. Env-gated: <c>DOTCHARTER_CPU_TIMELINE_DUMP=&lt;dir outside the repo&gt;</c> writes
///     <c>ghost_gems_timeline.png</c>; the structural assertions are
///     <see cref="HandShapeGhostGemRenderTests" />.
/// </summary>
public sealed class HandShapeGhostGemCaptureTests
{
    private const int Width = 900;
    private const int Height = 470;
    private const float TopMargin = 40f;
    private const float LaneHeight = 60f;

    [Fact]
    public void Capture_GhostGemsInThe2DTimeline()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_CPU_TIMELINE_DUMP");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "review capture — set DOTCHARTER_CPU_TIMELINE_DUMP to a folder.");

        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 250.0, Bpm = 240, IsMeasure = i % 4 == 0 }).ToList();
        var arrangement = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var g5 = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 } };
        arrangement.ChordTemplates.Add(g5);
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 0 };
        foreach (var (s, f) in g5.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);
        var tapped = new GuitarNote { Position = new FractionalPosition(5), StringIndex = 3, Fret = 17, Sustain = new FractionalPosition(1) };
        level.Notes.Add(tapped);
        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        GhostGems.TappedHarmonic(arrangement, level, [tapped], 12).Action!.Execute();

        Capture(arrangement, level, beats, Path.Combine(dir!, "ghost_gems_timeline.png"));
    }

    [Fact]
    public void Capture_HeldNoteGripsBeforeAndAfter()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_HELD_GRIPS_CAPTURE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "Set DOTCHARTER_HELD_GRIPS_CAPTURE to save the timeline.");
        var chart = new HeldNoteGripsTests.Fixture();
        var run = chart.AddRun(0);
        var beats = chart.Project.Beats.ToList();
        Capture(chart.Arrangement, chart.Level, beats, Path.Combine(dir!, "timeline-before.png"), 0.32, run);
        Assert.Equal((5, 5, 0), chart.Apply(run));
        Capture(chart.Arrangement, chart.Level, beats, Path.Combine(dir!, "timeline-after.png"), 0.32, run);
    }

    /// <summary>
    ///     The bend gestures a charter reads, one string each — a plain bend 0→½→Full, a
    ///     prebend held, a prebend released, a two-step bend (½, hold, Full), a bend tied into a
    ///     release (Full→½→0) and a bend released after a hold. Writes <c>807-bend-labels.png</c>;
    ///     the assertions are <c>BendGestureLabelTests</c> and <c>BendLabelPlacementTests</c>.
    /// </summary>
    [Fact]
    public void Capture_BendGestureLabels()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_CPU_TIMELINE_DUMP");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "review capture — set DOTCHARTER_CPU_TIMELINE_DUMP to a folder.");

        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 250.0, Bpm = 240, IsMeasure = i % 4 == 0 }).ToList();
        var arrangement = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        level.Notes.Add(Bent(0, 1, 2, (0, 0), (1, 1), (2, 2)));
        level.Notes.Add(Bent(1, 1, 2, (0, 2)));
        level.Notes.Add(Bent(2, 1, 2, (0, 2), (2, 2), (4, 0)));
        level.Notes.Add(Bent(3, 1, 2, (0, 0), (1, 1), (2, 1), (3, 2)));
        var tied = Bent(4, 1, 1, (0, 0), (2, 2));
        tied.LinkNext = true;
        level.Notes.Add(tied);
        level.Notes.Add(Bent(4, 2, 1, (0, 2), (1, 1), (2, 0)));
        level.Notes.Add(Bent(5, 1, 2, (0, 0), (1, 2), (3, 2), (4, 0)));

        Capture(arrangement, level, beats, Path.Combine(dir!, "807-bend-labels.png"), 0.6);
    }

    /// <summary>A note on <paramref name="stringIndex" /> at <paramref name="beat" />, held <paramref name="beats" />
    /// beats, its bend points at (quarter beats after the onset, half steps).</summary>
    private static GuitarNote Bent(int stringIndex, int beat, int beats, params (int Quarter, double Step)[] points)
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = 7,
            Sustain = new FractionalPosition(beats)
        };
        foreach (var (quarter, step) in points)
        {
            note.BendValues.Add(new BendValue(
                new FractionalPosition(beat + quarter / 4, new Fraction(quarter % 4, 4)), step));
        }

        return note;
    }

    [Fact]
    public void Capture_LetRingHandShapesBeforeAndAfter()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_LET_RING_CAPTURE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "Set DOTCHARTER_LET_RING_CAPTURE to save the timeline.");
        var chart = LetRingHandShapesTests.OwnerRun();
        var beats = chart.Project.Beats.ToList();
        Capture(chart.Arrangement, chart.Level, beats, Path.Combine(dir!, "742-timeline-before.png"), 0.35);
        Assert.Equal((6, 0), LetRingHandShapesTests.Apply(chart));
        Capture(chart.Arrangement, chart.Level, beats, Path.Combine(dir!, "742-timeline-after.png"), 0.35);
    }

    /// <summary>Also the tempo-map review capture's renderer (<c>TempoMapReflowCaptureTests</c>), which
    /// adds the beat grid with its bar labels.</summary>
    internal static void Capture(Arrangement arrangement, ChartLevel level, List<BeatMarker> beats, string path,
        double zoom = 0.5, HandShape[]? selected = null, bool beatGrid = false)
    {
        var endMs = Width / zoom - 100;
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(-100, endMs, 0, snapshot, new SnapshotUpdateContext
        {
            ActiveBeats = beats, ActiveNotes = level.Notes, ActiveChords = level.Chords, ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes, ActiveTemplates = arrangement.ChordTemplates,
            SelectedHandShapes = selected ?? [],
            HandShapeFindings = new HashSet<HandShape>()
        }, []);
        var atlas = new CapturingAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        var batcher = new TimelineBatcher { FontAtlas = atlas };
        var rc = new ChartRenderContext
        {
            Width = Width, Height = Height, CurrentZoom = zoom, ViewStartMs = -100, ViewEndMs = endMs,
            TopMargin = TopMargin, BottomMargin = 40f, LaneAreaHeight = LaneHeight * 6, LaneHeight = LaneHeight,
            StringCount = 6, DrawLabels = true, Snapshot = snapshot
        };
        new LaneLayer().Render(batcher, rc);
        if (beatGrid) new BeatGridLayer().Render(batcher, rc);
        new HandShapeLayer().Render(batcher, rc);
        new NoteLayer().Render(batcher, rc);
        batcher.FinalizeFrame();

        var pixels = Rasterize(batcher, atlas.Bitmap!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var p = pixels[y * Width + x];
                bitmap.SetPixel(x, y, new SKColor(ToByte(p.R), ToByte(p.G), ToByte(p.B), 255));
            }
        }

        using var file = File.Create(path);
        bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(Math.Round(v * 255f), 0, 255);

    private static (float R, float G, float B)[] Rasterize(TimelineBatcher batcher, SKBitmap font)
    {
        var bg = ChartTheme.TopMarginBgColor;
        var px = Enumerable.Repeat((bg.R / 255f, bg.G / 255f, bg.B / 255f), Width * Height).ToArray();
        foreach (var command in batcher.DrawCommands)
        {
            var solid = command.Kind == BatchKind.Solid;
            var stride = solid ? 6 : 9;
            var buf = solid ? batcher.SolidBuffer : batcher.GeneralBuffer;
            for (var v = command.FirstVertex; v + 2 < command.FirstVertex + command.VertexCount; v += 3)
                Triangle(px, buf, v * stride, stride, solid, font);
        }

        return px;
    }

    private static void Triangle((float R, float G, float B)[] px, float[] b, int i, int stride, bool solid, SKBitmap font)
    {
        float x0 = b[i], y0 = b[i + 1], x1 = b[i + stride], y1 = b[i + stride + 1], x2 = b[i + 2 * stride], y2 = b[i + 2 * stride + 1];
        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-6f) return;
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                float sx = x + 0.5f, sy = y + 0.5f;
                var w0 = ((x1 - sx) * (y2 - sy) - (x2 - sx) * (y1 - sy)) / area;
                var w1 = ((x2 - sx) * (y0 - sy) - (x0 - sx) * (y2 - sy)) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float At(int o) => w0 * b[i + o] + w1 * b[i + stride + o] + w2 * b[i + 2 * stride + o];
                float r = At(2), g = At(3), bl = At(4), a = At(5);
                if (!solid)
                {
                    float u = At(6), vv = At(7), type = b[i + 8];
                    var d = MathF.Sqrt(u * u + vv * vv);
                    if (type < 1.5f)
                    {
                        a *= 1f - SmoothStep(0.9f, 1f, d);
                    }
                    else if (type < 2.5f)
                    {
                        a *= 1f - SmoothStep(0.9f, 1f, d) - (1f - SmoothStep(0.7f, 0.8f, d));
                    }
                    else if (type < 3.5f)
                    {
                        var tx = Math.Clamp((int)(u * font.Width), 0, font.Width - 1);
                        var ty = Math.Clamp((int)(vv * font.Height), 0, font.Height - 1);
                        a *= font.GetPixel(tx, ty).Alpha / 255f;
                    }
                }

                if (a <= 0.01f) continue;
                var p = px[y * Width + x];
                px[y * Width + x] = (r * a + p.R * (1 - a), g * a + p.G * (1 - a), bl * a + p.B * (1 - a));
            }
        }
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Keeps the atlas bitmap the GPU path would upload, so glyph quads can be sampled.</summary>
    private sealed class CapturingAtlas : ChartFontAtlas
    {
        public SKBitmap? Bitmap { get; private set; }

        protected override void UploadAtlas(SKBitmap bitmap) => Bitmap = bitmap.Copy();

        public override void Dispose() => Bitmap?.Dispose();
    }
}
