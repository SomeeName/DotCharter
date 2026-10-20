using DotCharter.Rendering.Data;
using DotCharter.Rendering.Headless;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;
using static DotCharter.Tests.Rendering.PartialChordSlideFixture;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Guitar Pro pick scrapes (#583) through the REAL gpif import and the REAL
///     renderers, on the Shinbatsu-m197-shaped score (<see cref="PickScrapeScenes" />)
///     plus a fourth bar: an A5 at fret 5 and a dead low E scraped UP. The import
///     charts every scrape as tap + tremolo + fret-hand mute + ignore + an
///     unpitched slide; a two-string scrape is ONE chord split into notes (#599):
///     A+D 24 → 18 three times and E+A 24 → 12 off FHP 21, and the one-string
///     E 12 → 24 off FHP 5 is a note (#586: a scrape starts on 24 or 12). The 3D
///     frame must change when the scrapes are removed, so they draw something. <c>DOTCHARTER_GPU_DUMP=1</c> writes the
///     review frames (<c>vk_pick_scrape_*</c>, <c>tl_pick_scrape_*</c>).
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessPickScrapeRenderTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double ShortScrapesMs = FirstBeatMs + 4 * BeatMs;
    private const double LongScrapeMs = FirstBeatMs + 8 * BeatMs;
    private const double UpScrapeMs = FirstBeatMs + 13 * BeatMs;
    private const int TimelineWidth = 1200;
    private const int TimelineHeight = 420;

    [Fact]
    public void ImportedPickScrapes_RenderAsTappedTremoloUnpitchedSlides()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var arrangement = PickScrapeScenes.Import(Scene());
        var level = arrangement.Levels[0];
        var scrapeChords = level.Chords.Where(GpPickScrape.IsScrapeChord).ToList();
        Assert.Equal(new[] { 4, 5, 6, 8 }, scrapeChords.Select(c => c.Position.BeatId).ToArray());
        Assert.All(scrapeChords, c => Assert.True(c.Ignore));
        var up = Assert.Single(level.Notes, n => n.Ignore);
        Assert.Equal((13, 12, 24), (up.Position.BeatId, up.Fret, up.SlideTo!.Value));
        Assert.All(scrapeChords.SelectMany(c => c.ChordNotes).Cast<IGuitarSound>().Append(up),
            n => Assert.Equal((HopoType.Tap, true, true, MuteType.Full), (n.Hopo, n.Tremolo, n.UnpitchedSlide, n.Mute)));
        Assert.All(scrapeChords.Where(c => c.Position.BeatId is 4 or 5 or 6).SelectMany(c => c.ChordNotes),
            n => Assert.Equal((24, 18), (n.Fret, n.SlideTo!.Value)));
        Assert.All(scrapeChords.Single(c => c.Position.BeatId == 8).ChordNotes,
            n => Assert.Equal((24, 12), (n.Fret, n.SlideTo!.Value)));

        var withScrapes = Highway(arrangement);
        var stripped = PickScrapeScenes.Import(Scene());
        var strippedLevel = stripped.Levels[0];
        foreach (var n in strippedLevel.Notes.Where(n => n.Ignore).ToList()) strippedLevel.Notes.Remove(n);
        foreach (var c in strippedLevel.Chords.Where(GpPickScrape.IsScrapeChord).ToList()) strippedLevel.Chords.Remove(c);
        var withoutScrapes = Highway(stripped);

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var approach = r.Render(withScrapes, Width, Height, timeMs: ShortScrapesMs - 500);
        var approachBare = r.Render(withoutScrapes, Width, Height, timeMs: ShortScrapesMs - 500);
        GoldenFrame.Dump(approach, Width, Height, "vk_pick_scrape_approach");
        GoldenFrame.Dump(r.Render(withScrapes, Width, Height, timeMs: ShortScrapesMs + 250), Width, Height,
            "vk_pick_scrape_short_midslide");
        GoldenFrame.Dump(r.Render(withScrapes, Width, Height, timeMs: LongScrapeMs - 500), Width, Height,
            "vk_pick_scrape_long_approach");
        GoldenFrame.Dump(r.Render(withScrapes, Width, Height, timeMs: LongScrapeMs + 700), Width, Height,
            "vk_pick_scrape_long_midslide");
        GoldenFrame.Dump(r.Render(withScrapes, Width, Height, timeMs: UpScrapeMs + 400), Width, Height,
            "vk_pick_scrape_up_midslide");

        Assert.True(GoldenFrame.NonBlackRatio(approach) > 0.05, "the highway frame rendered (near-)black");
        var box = GoldenFrame.DifferenceBounds(approach, approachBare, Width, Height);
        Assert.True(box is { Pixels: > 500 }, $"the scrapes changed only {box?.Pixels ?? 0} pixels of the highway");

        // A split chord ships as single notes, so it draws no chord box: the same
        // chords NOT split must draw differently (the frame).
        var unsplit = PickScrapeScenes.Import(Scene());
        foreach (var c in unsplit.Levels[0].Chords) c.SplitIntoNotes = false;
        var approachUnsplit = r.Render(Highway(unsplit), Width, Height, timeMs: ShortScrapesMs - 500);
        GoldenFrame.Dump(approachUnsplit, Width, Height, "vk_pick_scrape_approach_unsplit");
        var frame = GoldenFrame.DifferenceBounds(approach, approachUnsplit, Width, Height);
        Assert.True(frame is { Pixels: > 500 }, $"splitting the scrapes changed only {frame?.Pixels ?? 0} pixels");

        var timeline = Timeline(withScrapes, ShortScrapesMs - 1600, 0.2);
        GoldenFrame.Dump(timeline, TimelineWidth, TimelineHeight, "tl_pick_scrape_m197");
        GoldenFrame.Dump(Timeline(withScrapes, UpScrapeMs - 2000, 0.3), TimelineWidth, TimelineHeight,
            "tl_pick_scrape_up");
        Assert.True(GoldenFrame.NonBlackRatio(timeline) > 0.05, "the timeline frame rendered (near-)black");
    }

    private static byte[] Timeline(FrameSnapshot snapshot, double startMs, double zoom)
    {
        using var r = new HeadlessVulkanTimelineRenderer(TimelineWidth, TimelineHeight);
        var rc = new ChartRenderContext
        {
            Width = TimelineWidth,
            Height = TimelineHeight,
            CurrentZoom = zoom,
            ViewStartMs = startMs,
            ViewEndMs = startMs + TimelineWidth / zoom,
            TopMargin = 30f,
            BottomMargin = 30f,
            LaneAreaHeight = 360f,
            LaneHeight = 60f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot
        };
        return r.Render(rc, new ITimelineRenderLayer[] { new LaneLayer(), new HandShapeLayer(), new NoteLayer() });
    }

    private static FrameSnapshot Highway(Arrangement arrangement)
    {
        var s = Snapshot(arrangement);
        s.HighwayScrollSpeed = 1.3f;
        return s;
    }

    /// <summary>The Shinbatsu bars, then an A5 at fret 5 and a dead low E scraped
    /// up for a quarter (beat 13).</summary>
    private static string Scene() => PickScrapeScenes.Gpif(
    [
        .. PickScrapeScenes.Shinbatsu(),
        [
            new("Quarter", [new(0, 5), new(1, 7)]), new("Quarter", [new(0, 0, GpPickScrape.UpFlag)]),
            new("Half", [])
        ]
    ]);
}
