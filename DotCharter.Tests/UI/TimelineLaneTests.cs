using System.Text.Json;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Layout;
using DotCharter.UI.ViewModels.Editor;
using SkiaSharp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The timeline lane rules (#81): which ribbon lanes get a row
///     (<see cref="RibbonLaneRules" />), what hiding or compacting one gives the note lanes,
///     that the lane settings survive a restart, and that the editor split restores the
///     user's splitter. The lane MENU's construction and its measured rows are
///     <c>Perf/TimelineLaneMenuUiPerfSmokeTests</c>.
/// </summary>
public sealed class TimelineLaneTests
{
    private const double BottomMargin = 60;

    private static readonly RibbonContent Everything = new(DistinctTones: 2, HasSections: true, HasPhrases: true, HasLyrics: true);

    private static RibbonVisibility Visibility(bool autoHide = true, bool compact = false) =>
        new() { AutoHideEmpty = autoHide, CompactRows = compact };

    // ── one tone counts as empty ───────────────────────────────────────────────

    [Fact]
    public void OneTone_AutoHidesTheTonesLane()
    {
        var oneTone = Everything with { DistinctTones = 1 };
        var layout = RibbonLaneRules.Compose(Visibility(), oneTone);
        Assert.False(layout.ShowTones, "a chart with one tone still spends a row on TONES");
        Assert.Equal(RibbonLaneHiddenReason.OneTone,
            RibbonLaneRules.HiddenReason(RibbonLanes.Tones, RibbonLanes.All, autoHideEmpty: true, oneTone));

        // Two tones are content; and with auto-hide off the user's toggle wins.
        Assert.True(RibbonLaneRules.Compose(Visibility(), Everything).ShowTones);
        Assert.True(RibbonLaneRules.Compose(Visibility(autoHide: false), oneTone).ShowTones);
    }

    [Theory]
    [InlineData("", new string[0], 0)]
    [InlineData("Clean", new string[0], 1)]
    [InlineData("", new[] { "Clean" }, 1)]
    [InlineData("Clean", new[] { "Clean", "Clean" }, 1)]
    [InlineData("Clean", new[] { "Lead" }, 2)]
    [InlineData("", new[] { "Clean", "", "Lead" }, 2)]
    public void DistinctTones_CountsTheBaseToneAndEveryChangeByName(string baseTone, string[] changes, int expected)
    {
        var list = changes.Select(n => new ToneChange { ToneName = n }).ToList();
        Assert.Equal(expected, RibbonLaneRules.DistinctTones(baseTone, list));
    }

    /// <summary>SECTIONS stays, empty, as the event dialog's entry point while the track has no
    /// event content at all — one tone included, which is no longer content (#81).</summary>
    [Fact]
    public void Sections_StaysTheEntryPoint_WhenThereIsNoEventContent()
    {
        var bare = new RibbonContent(DistinctTones: 1, HasSections: false, HasPhrases: false, HasLyrics: false);
        var layout = RibbonLaneRules.Compose(Visibility(), bare);
        Assert.Equal(RibbonLanes.Sections | RibbonLanes.MeasureMetadata, layout.Lanes);

        // …but once a phrase exists, an empty Sections lane hides like any other.
        Assert.False(RibbonLaneRules.Compose(Visibility(), bare with { HasPhrases = true }).ShowSections);
    }

    // ── the height goes to the note lanes ──────────────────────────────────────

    [Fact]
    public void HidingALane_GivesItsHeightToTheNoteArea()
    {
        var all = TimelineGutterLayout.For(600, RibbonLaneRules.Compose(Visibility(), Everything), 6, false, BottomMargin);
        var off = Visibility();
        off.ShowLyrics = false;
        var noLyrics = TimelineGutterLayout.For(600, RibbonLaneRules.Compose(off, Everything), 6, false, BottomMargin);
        Assert.Equal(all.LaneAreaHeight + RibbonLayout.RowHeight, noLyrics.LaneAreaHeight, 3);

        // The same for an AUTO-hidden lane: an empty lyrics track.
        var empty = TimelineGutterLayout.For(600,
            RibbonLaneRules.Compose(Visibility(), Everything with { HasLyrics = false }), 6, false, BottomMargin);
        Assert.Equal(noLyrics.LaneAreaHeight, empty.LaneAreaHeight, 3);
    }

    /// <summary>Compact rows: every ribbon row and the measures band shorter, the
    /// difference handed to the note lanes, and each chip still inside its row.</summary>
    [Fact]
    public void CompactRows_AreShorter_AndTheNoteLanesGetTheDifference()
    {
        var normal = RibbonLaneRules.Compose(Visibility(), Everything);
        var compact = RibbonLaneRules.Compose(Visibility(compact: true), Everything);
        Assert.Equal(RibbonDensity.Compact, compact.Density);

        // The approved mockup: 18 px rows and a 15 px measures band, +29 px of note area for
        // four lanes and the band — five lanes here, so at least that.
        Assert.True(normal.TotalHeight - compact.TotalHeight >= 29,
            $"compact rows save {normal.TotalHeight - compact.TotalHeight} px — the mockup's rows save 29");
        var saved = (4 * (RibbonLayout.RowHeight - RibbonLayout.CompactRowHeight))
                    + (RibbonLayout.MetadataBandHeight - RibbonLayout.CompactMetadataBandHeight);
        Assert.Equal(normal.TotalHeight - saved, compact.TotalHeight, 3);

        var normalLanes = TimelineGutterLayout.For(600, normal, 6, false, BottomMargin).LaneAreaHeight;
        var compactLanes = TimelineGutterLayout.For(600, compact, 6, false, BottomMargin).LaneAreaHeight;
        Assert.Equal(normalLanes + saved, compactLanes, 3);

        // The chip sits inside its compact row, and the pill inside its band.
        Assert.True(compact.BarTop(compact.SectionY) >= compact.SectionY);
        Assert.True(compact.BarBottom(compact.SectionY) <= compact.SectionY + compact.RowPitch);
        Assert.True(compact.PillTop(compact.MetadataY) >= compact.MetadataY);
        Assert.True(compact.PillTop(compact.MetadataY) + compact.Pill <= compact.MetadataY + compact.Band);
        Assert.Equal(EventRow.Lyric, compact.HitTestRow(compact.BarTop(compact.LyricY) + 1));
    }

    /// <summary>
    ///     The text on a compact chip stays readable: the chart font's caps, digits and
    ///     x-height letters, centred the way <c>TimelineBatcher.AddText</c> centres them (on the
    ///     middle of the '0' digit, <c>ChartFontAtlas.TextCenterYOffset</c>), fit inside the
    ///     15 px compact chip — section names, phrase names and measure pills alike.
    /// </summary>
    [Fact]
    public void CompactChips_HoldTheChartFontsText()
    {
        using var font = new SKFont(SKTypeface.Default, ChartEditorConstants.RenderMetrics.DefaultFontSize);
        font.MeasureText("0", out var zero);
        font.MeasureText("VERSE CHORUS M17 120 BPM 4/4 chorus verse", out var ink);
        var above = zero.MidY - ink.Top;
        var below = ink.Bottom - zero.MidY;
        var half = RibbonLayout.CompactBarHeight / 2f;
        Assert.True(above <= half + 0.5f && below <= half + 0.5f,
            $"the chart font's text spans {above:0.#} px above and {below:0.#} px below its centre — " +
            $"more than the compact chip's {half} px each way");
    }

    /// <summary>The renderer reads the lanes through the shared rules, not a copy of them.</summary>
    [Fact]
    public void TheRenderer_LaysTheRibbonOutThroughTheLaneRules()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ChartRenderer.cs")));
        Assert.Contains("RibbonLaneRules.Compose(ctx.Ribbon, RibbonContentOf(ctx))", src, StringComparison.Ordinal);
    }

    // ── settings survive a restart ─────────────────────────────────────────────

    [Fact]
    public void TheLaneSettings_RoundTripThroughTheAppConfig()
    {
        var config = new FakeConfigurationService();
        var (ribbon, _) = ActiveRibbon(config);
        Assert.True(ribbon.AutoHideEmpty, "auto-hide is on by default");
        Assert.False(ribbon.CompactRows, "compact rows are off by default");

        ribbon.AutoHideEmpty = false;
        ribbon.CompactRows = true;
        ribbon.ShowPhrases = false;
        Assert.False(config.AppConfig.RibbonAutoHideEmpty);
        Assert.True(config.AppConfig.RibbonCompactRows);

        // Through the file format, into a fresh session.
        var json = JsonSerializer.Serialize(config.AppConfig);
        var next = new FakeConfigurationService { AppConfig = JsonSerializer.Deserialize<AppConfig>(json)! };
        var (restored, _) = ActiveRibbon(next);
        Assert.False(restored.AutoHideEmpty);
        Assert.True(restored.CompactRows);
        Assert.False(restored.ShowPhrases);
    }

    private static (RibbonVisibility Ribbon, MultiTrackOverlayViewModel Overlay) ActiveRibbon(FakeConfigurationService config)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var registry = new TrackContextRegistry(state, new SelectionService());
        var overlay = new MultiTrackOverlayViewModel(
            new GhostArrangementsService(), new StackedArrangementsService(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IGlobalEditingCoordinator>().Proxy,
            state, registry, config, logger);

        var project = new ChartProject();
        var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        lead.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(lead);
        state.ActiveArrangement = lead;
        state.ActiveProject = project;
        return (registry.GetOrCreate(lead).Ribbon, overlay);
    }

    // ── the editor split keeps the user's splitter (#81) ───────────────────────

    [Fact]
    public void TheSavedSplit_RestoresAsTheExactRatio()
    {
        Assert.Equal((500.0, 400.0), MainWindowLayoutController.SavedSplitStars(500, 400));

        // Incomplete or unusable: the dock-only estimate takes over.
        Assert.Null(MainWindowLayoutController.SavedSplitStars(null, 400));
        Assert.Null(MainWindowLayoutController.SavedSplitStars(500, null));
        Assert.Null(MainWindowLayoutController.SavedSplitStars(10, 400));
        Assert.Null(MainWindowLayoutController.SavedSplitStars(500, double.NaN));
    }
}
