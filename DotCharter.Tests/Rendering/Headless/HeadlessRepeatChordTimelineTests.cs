using DotCharter.Rendering.Data;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Repeat chords (#564) through the REAL 2D layers and the offscreen Vulkan renderer: one A5 strum,
///     then the same shape six more times — plain, plain, palm-muted, fret-hand-muted, accented and
///     selected. The export's own rule (<see cref="RepeatChords" />) decides which strums are repeats,
///     exactly as the app's latch does. A repeat's head must read DIMMER than the original's at the same
///     string, and the selected repeat must not. <c>DOTCHARTER_GPU_DUMP=1</c> writes the frame
///     (<c>tl_repeat_chords</c>) — the PR's evidence. GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessRepeatChordTimelineTests
{
    private const int Width = 1200;
    private const int Height = 420;
    private const double Zoom = 0.25;
    private const double BeatMs = 500;
    private const float TopMargin = 30f;
    private const float LaneHeight = 55f;

    [Fact]
    public void ARunOfRepeats_DrawsDimmedHeads_AndASelectedRepeatStaysBright()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var (arr, chords) = Chart();
        var level = arr.Levels[0];
        var repeats = RepeatChords.Compute(arr.ChordTemplates, level.Notes, level.Chords, level.HandShapes, Beats());
        Assert.Equal(chords.Skip(1), level.Chords.Where(repeats.Contains));

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var frame = r.Render(Context(Snapshot(arr, repeats, chords[^1])), Layers());
        GoldenFrame.Dump(frame, Width, Height, "tl_repeat_chords");

        var original = HeadLuma(frame, chords[0]);
        var plainRepeat = HeadLuma(frame, chords[1]);
        var selectedRepeat = HeadLuma(frame, chords[^1]);
        Assert.True(plainRepeat < original * 0.8,
            $"a repeat's head reads {plainRepeat:0} against the original's {original:0} — not dimmed");
        Assert.True(selectedRepeat > plainRepeat,
            $"the selected repeat's head reads {selectedRepeat:0}, no brighter than a plain repeat's {plainRepeat:0}");
    }

    private static ITimelineRenderLayer[] Layers() => [new LaneLayer(), new HandShapeLayer(), new NoteLayer()];

    /// <summary>Mean luma of a 14 px box on the chord's string-1 head — BGRA8, top row first.</summary>
    private static double HeadLuma(byte[] bgra, Chord chord)
    {
        var cx = (int)(chord.Position.GetPositionMs(Beats()) * Zoom);
        var cy = (int)(TopMargin + 1 * LaneHeight + LaneHeight / 2f);
        double sum = 0;
        var n = 0;
        for (var y = cy - 7; y < cy + 7; y++)
        {
            for (var x = cx - 7; x < cx + 7; x++)
            {
                var i = (y * Width + x) * 4;
                sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
                n++;
            }
        }

        return sum / n;
    }

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * BeatMs, Bpm = 120 })];

    /// <summary>A5 on beats 1..7; strums 4 and 5 palm- and fret-hand-muted, 6 accented, 7 selected.</summary>
    private static (Arrangement Arrangement, List<Chord> Chords) Chart()
    {
        var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.Levels.Add(new ChartLevel());
        var a5 = new ChordTemplate { Id = 0, Name = "A5" };
        a5.Frets[0] = 5;
        a5.Frets[1] = 7;
        a5.Frets[2] = 7;
        arr.ChordTemplates.Add(a5);

        var chords = new List<Chord>();
        for (var beat = 1; beat <= 7; beat++)
        {
            var chord = new Chord { TemplateId = a5.Id, Position = new FractionalPosition(beat), Accent = beat == 6 };
            foreach (var (stringIndex, fret) in a5.Frets)
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = stringIndex,
                    Fret = fret,
                    Accent = chord.Accent,
                    Mute = beat == 4 ? MuteType.Palm : beat == 5 ? MuteType.Full : MuteType.None
                });
            }

            arr.Levels[0].Chords.Add(chord);
            chords.Add(chord);
        }

        return (arr, chords);
    }

    private static FrameSnapshot Snapshot(Arrangement arr, IReadOnlySet<Chord> repeats, Chord selected)
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
            RepeatChords = repeats
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
            TopMargin = TopMargin,
            BottomMargin = 30f,
            LaneAreaHeight = 330f,
            LaneHeight = LaneHeight,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot
        };
}
