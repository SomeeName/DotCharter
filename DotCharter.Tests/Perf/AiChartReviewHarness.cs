using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Ai.Chart;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI review card's headless fixture (#506, #613): a pass on a FAKE model that holds
///     its proposals, the card hung where <c>MainWindow.axaml</c> hangs it (the timeline's
///     top-right corner, <c>PaddingCardSm</c> in), and synthetic section lists for the sizes
///     a real song reaches.
/// </summary>
internal static partial class AiChartReviewHarness
{
    /// <summary>The timeline row at 1920x1080: <c>TimelineAreaMinHeight.TimelineMinPx</c>.</summary>
    public const double DefaultTimelineHeight = 360;

    /// <summary>The timeline row at 1280x720: <c>EditorRowFloors.TimelineCompactPx</c>.</summary>
    public const double ShortTimelineHeight = 270;

    public sealed record Review(AiChartReviewViewModel Vm, AiChartPassRun Run, ProjectState State, HistoryService History);

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    /// <summary>A view model over a pass that has NOT run yet; <c>await Run.RunAsync(…)</c> holds the
    /// fixture's two sections (intro 0:00–0:08, verse 0:08–0:16).</summary>
    public static Review NewReview(ILocalizationService? loc = null)
    {
        var f = new AiChartFixture();
        var state = new ProjectState { ActiveProject = f.Project, ActiveArrangement = f.Arrangement, ActiveLevel = f.Level };
        var history = new HistoryService(new FakeAppLogger());
        var selection = new SelectionService();
        var config = new FakeConfigurationService();
        var logger = new FakeAppLogger();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
        var model = new FakeChartModel((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = f.Chunks().Single(c => c.StartMs == ms);
            var reply = f.EchoReply(chunk);
            AiChartReviewTests.Proposal(chunk, reply);
            return reply.Json();
        });
        var rig = new RigConfig { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
        loc ??= FakeLocalization.English();
        var run = new AiChartPassRun(state, history, null, loc, logger, new FingeringGenerationService(),
            new Factory(model), () => rig, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase
        };
        return new Review(new AiChartReviewViewModel(run, editor, history, state, loc), run, state, history);
    }

    /// <summary>
    ///     Replaces the held review's rows with <paramref name="count" /> sections in every state, the
    ///     second one named past any card width, worded in <paramref name="loc" />. The rows are the
    ///     card's only input then — decide nothing on them (they are not the review's); one real accept
    ///     on the held review keeps Apply live, and the counts are restated for the rows.
    /// </summary>
    public static void Fill(AiChartReviewViewModel vm, int count, ILocalizationService loc)
    {
        string[] names = ["intro", "verse 1", "pre-chorus", "chorus", "verse 2", "bridge", "solo", "outro"];
        vm.Rows[^1].AcceptCommand.Execute(null);
        vm.Rows.Clear();
        var counts = new int[3];
        for (var i = 0; i < count; i++)
        {
            var name = i == 1
                ? "pre-chorus, chorus, post-chorus, bridge, breakdown, solo, outro and the fade-out"
                : $"{names[i % names.Length]}{(i >= names.Length ? $" {(i / names.Length) + 1}" : string.Empty)}";
            var decision = (i % 4) switch
            {
                1 => AiChartDecision.Accepted,
                3 => AiChartDecision.Rejected,
                _ => AiChartDecision.Pending
            };
            counts[(int)decision]++;
            var chunk = new AiChartReviewChunk(new AiChartChunkPlan(i, new(i * 8), new((i + 1) * 8), null, []),
                i * 9300, i == count - 1 ? null : (i + 1) * 9300, name, new AiChartApplySummary(0, 3 + (i % 5), 0, 0, 0, 0),
                AiChartApplyTargets.None)
            {
                Decision = decision
            };
            var fingerings = loc.Plural("AiChartReview_FingeringsOne", "AiChartReview_Fingerings", 3 + (i % 5));
            var detail = i % 3 == 0
                ? $"{loc.Plural("AiChartReview_AnchorsOne", "AiChartReview_Anchors", 1 + (i % 2))} · {fingerings}"
                : fingerings;
            var row = new AiChartReviewRowViewModel(vm, chunk, detail);
            row.Refresh(loc);
            vm.Rows.Add(row);
        }

        vm.CountsText = loc.Format("AiChartReview_Counts", counts[(int)AiChartDecision.Accepted],
            counts[(int)AiChartDecision.Rejected], counts[(int)AiChartDecision.Pending]);
        vm.ApplyText = loc.Format("AiChartReview_Apply", counts[(int)AiChartDecision.Accepted]);
    }

    /// <summary>A panel the size of the timeline row, on the timeline's ground, with the card hung
    /// off its top-right corner as <c>MainWindow.axaml</c> hangs it.</summary>
    public static (Window Window, Panel Host) NewTimelineHost(double width, double height, AiChartReviewView view)
    {
        view.HorizontalAlignment = HorizontalAlignment.Right;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(12, 10);
        var host = new Panel { Width = width, Height = height };
        if (Application.Current is { } app
            && app.TryGetResource("CardBackground", app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            host.Background = brush;
        }

        host.Children.Add(view);
        return (NarrowWidthLayout.NewWindow(width, height, host), host);
    }

    /// <summary>The realized row containers, in list order.</summary>
    public static List<Control> Rows(AiChartReviewView view) =>
        view.FindControl<ItemsControl>("AiChartReviewRows")!.GetRealizedContainers().OfType<Control>().ToList();

    /// <summary>How many section rows lie wholly inside the list's viewport.</summary>
    public static int WhollyVisibleRows(AiChartReviewView view)
    {
        var scroll = view.FindControl<ScrollViewer>("AiChartReviewScroll")!;
        return Rows(view).Count(row =>
        {
            var top = row.TranslatePoint(default, scroll)!.Value.Y;
            return top >= -0.5 && top + row.Bounds.Height <= scroll.Bounds.Height + 0.5;
        });
    }

    /// <summary>The list's vertical scrollbar.</summary>
    public static ScrollBar VerticalBar(AiChartReviewView view) =>
        view.FindControl<ScrollViewer>("AiChartReviewScroll")!.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Orientation.Vertical);

    public static T Named<T>(Visual root, string name) where T : Control => NarrowWidthLayout.Named<T>(root, name);

    /// <summary>The control's bounds in <paramref name="space" />'s coordinates.</summary>
    public static Rect BoundsIn(Visual control, Visual space) =>
        new(control.TranslatePoint(default, space)!.Value, control.Bounds.Size);

    public static void AssertMeasured(Control control) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{control.Name ?? control.GetType().Name} laid out as {control.Bounds} — it rendered nothing.");

    /// <summary>The control lies wholly inside the card — the width-shaped claim de-DE and ja-JP grade.</summary>
    public static void AssertInsideCard(Control control, Border card)
    {
        AssertMeasured(control);
        var r = BoundsIn(control, card);
        Assert.True(r.X >= -0.5 && r.Right <= card.Bounds.Width + 0.5 && r.Y >= -0.5 && r.Bottom <= card.Bounds.Height + 0.5,
            $"{control.Name ?? control.GetType().Name} at {r} leaves the {card.Bounds.Size} card ({HeadlessSessionFixture.SweptCulture})");
    }

    /// <summary>The swept culture's real strings, for captures a reader compares across languages.</summary>
    public static ILocalizationService SweptLocalization => LocalizationService.Instance;

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
