using DotCharter.Rendering.Data;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The bulk-edit preview (#485) through the REAL 2D layers and the offscreen
///     Vulkan renderer: the same chart rendered without and with the preview the
///     chord panel resolves for "+ the same grip elsewhere". Only the second frame
///     carries the EditTarget colour, and it carries it at every marked chord's
///     column — none at the unrelated chord's. <c>DOTCHARTER_GPU_DUMP=1</c> writes
///     both frames (the PR's before/after). GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessEditTargetTimelineTests
{
    private const int Width = 1200;
    private const int Height = 420;
    private const double Zoom = 0.1;
    private const double BeatMs = 500;

    [Fact]
    public void ThePreview_MarksExactlyTheTargetColumns()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var (arr, a5) = Chart();
        var level = arr.Levels[0];
        var selected = level.Chords[0];
        var scope = ChordApplyScope.Prepare(arr, a5, new ChordApplySounds([selected], []),
            new ChordApplySounds([selected], []), level.Chords);
        var preview = scope.Preview(ChordApplyRung.SameGrip);
        var highlight = ChordUseHighlight.From(preview.Chords, preview.HandShapes, arr);

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var before = r.Render(Context(Snapshot(arr, ChordUseHighlight.None, selected)), Layers());
        var after = r.Render(Context(Snapshot(arr, highlight, selected)), Layers());
        GoldenFrame.Dump(before, Width, Height, "tl_bulk_edit_preview_before");
        GoldenFrame.Dump(after, Width, Height, "tl_bulk_edit_preview_after");

        Assert.Equal(0, TargetPixels(before, 0, Width));
        foreach (var chord in level.Chords)
        {
            var x = (int)(chord.Position.GetPositionMs(Beats()) * Zoom);
            var marked = TargetPixels(after, x - 20, x + 20);
            if (preview.Chords.Contains(chord))
                Assert.True(marked > 20, $"the chord at x={x} is a target but drew {marked} EditTarget pixels");
            else
                Assert.Equal(0, marked);
        }
    }

    private static ITimelineRenderLayer[] Layers() => [new LaneLayer(), new HandShapeLayer(), new NoteLayer()];

    /// <summary>Pixels within a few levels of the EditTarget colour in columns
    /// [x0, x1) — BGRA8, top row first.</summary>
    private static int TargetPixels(byte[] bgra, int x0, int x1)
    {
        var c = ChartTheme.EditTargetColor;
        var n = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
            {
                var i = (y * Width + x) * 4;
                if (Math.Abs(bgra[i] - c.B) < 12 && Math.Abs(bgra[i + 1] - c.G) < 12 && Math.Abs(bgra[i + 2] - c.R) < 12)
                    n++;
            }
        }

        return n;
    }

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * BeatMs, Bpm = 120 })];

    /// <summary>A5 on beats 2, 6, 10 and 14 under one bar per pair; the same grip
    /// two frets up (B5) on 18; an unrelated E5 on 22.</summary>
    private static (Arrangement Arrangement, ChordTemplate A5) Chart()
    {
        var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.Levels.Add(new ChartLevel());
        var a5 = Template(arr, 0, "A5", 5);
        var b5 = Template(arr, 1, "B5", 7);
        var e5 = Template(arr, 2, "E5", 0);
        foreach (var beat in new[] { 2, 6, 10, 14 }) AddChord(arr, a5, beat);
        AddChord(arr, b5, 18);
        AddChord(arr, e5, 22);
        arr.Levels[0].HandShapes.Add(new HandShape
        {
            TemplateId = a5.Id, Position = new FractionalPosition(2), EndPosition = new FractionalPosition(8)
        });
        arr.Levels[0].HandShapes.Add(new HandShape
        {
            TemplateId = b5.Id, Position = new FractionalPosition(18), EndPosition = new FractionalPosition(20)
        });
        return (arr, a5);
    }

    private static ChordTemplate Template(Arrangement arr, int id, string name, int root)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        t.Frets[0] = root;
        t.Frets[1] = root + 2;
        t.Frets[2] = root + 2;
        arr.ChordTemplates.Add(t);
        return t;
    }

    private static void AddChord(Arrangement arr, ChordTemplate template, int beat)
    {
        var chord = new Chord { TemplateId = template.Id, Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        arr.Levels[0].Chords.Add(chord);
    }

    private static FrameSnapshot Snapshot(Arrangement arr, ChordUseHighlight highlight, Chord selected)
    {
        var level = arr.Levels[0];
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = Beats(),
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arr.ChordTemplates,
            SelectedChords = [selected],
            EditTargetChords = highlight.Chords,
            EditTargetHandShapes = highlight.HandShapes
        };
        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(0, Width / Zoom, 0, snapshot, ctx, []);
        return snapshot;
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot) =>
        new()
        {
            Width = Width,
            Height = Height,
            CurrentZoom = Zoom,
            ViewStartMs = 0,
            ViewEndMs = Width / Zoom,
            TopMargin = 30f,
            BottomMargin = 30f,
            LaneAreaHeight = 330f,
            LaneHeight = 55f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot
        };
}
