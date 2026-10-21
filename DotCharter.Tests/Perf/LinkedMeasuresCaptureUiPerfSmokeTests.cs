using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.Tests.UI.Linking;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Views.Editor.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The linked-measures surfaces for review: the toast and the hover card over the timeline,
///     the link panel at a chip, and the Inspector banner. The timeline under them is the GPU
///     frame <c>Rendering/Headless/HeadlessLinkTimelineTests</c> dumps (same chart, same
///     geometry) when it has run with <c>DOTCHARTER_GPU_DUMP=1</c>; otherwise a plain background.
///     Writes PNGs only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c>;
///     the layout assertions run every time. The panel is placed inline here: a popup is its
///     own top level and is not in the window's frame.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LinkedMeasuresCaptureUiPerfSmokeTests
{
    private const int Width = 1600;
    private const int Height = 420;

    [Fact]
    public async Task The_linked_measures_surfaces_lay_out_over_the_timeline() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var t = LinkedMeasuresHarness.WithRhythm();
            var h = t.Links;
            var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 4));
            h.Link((h.Lead, 7), (h.Rhythm, 7));

            // 1. The toast after an edit in bar 4.
            var timeline = new object();
            var overlay = new LinkOverlayView { DataContext = t.Vm, Timeline = timeline };
            Capture(Frame("tl_links_row", overlay), "links_toast.png", () =>
            {
                h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8);
                Settle(overlay);
                Assert.True(overlay.FindControl<Border>("LinkToast")!.Bounds.Width > 1);
            });

            // 2. The hover card at bar 4's chip, and the panel opened on it.
            t.Vm.DismissToast();
            var overlay2 = new LinkOverlayView { DataContext = t.Vm, Timeline = timeline };
            var panel = new LinkPanelView { DataContext = t.Vm.Panel };
            var (chipX, chipY) = Chip(h, 4);
            Canvas.SetLeft(panel, chipX + 330);
            Canvas.SetTop(panel, 8);
            var panelLayer = new Canvas { Children = { panel } };
            Capture(Frame("tl_links_hover", overlay2, panelLayer), "links_hover_panel.png", () =>
            {
                t.Editor.LinkHighlight.SetHovered(group.Id, timeline, new Point(chipX + 6, chipY));
                t.Vm.OpenPanel(group.Id, h.Lead, At(4, 1));
                Settle(overlay2);
                Assert.True(overlay2.FindControl<Border>("LinkHoverCard")!.Bounds.Width > 1);
                Assert.True(panel.Bounds.Height > 1);
            });

            // 3. The Inspector banner for a selection in bar 4.
            var banner = new LinkBannerView { DataContext = t.Vm };
            var pane = new Border
            {
                Width = 460, Padding = new Thickness(0, 6), Background = new SolidColorBrush(Color.FromRgb(24, 26, 34)),
                Child = banner
            };
            Capture(pane, "links_banner.png", () =>
            {
                t.Select(t.NoteIn(4));
                Settle(pane);
                Assert.True(banner.Bounds.Height > 1);
            }, 460, 50);

            // 4. The panel on a group whose other runs play the same: the re-link suggestions.
            var s = LinkedMeasuresHarness.WithRhythm();
            var riff = s.Links.Link((s.Links.Lead, 1), (s.Links.Lead, 4));
            s.Vm.OpenPanel(riff.Id, s.Links.Lead, At(4, 1));
            var suggesting = new LinkPanelView { DataContext = s.Vm.Panel };
            Capture(new Border { Padding = new Thickness(10), Child = suggesting }, "links_panel_suggestions.png", () =>
            {
                Settle(suggesting);
                Assert.True(suggesting.FindControl<StackPanel>("LinkPanelSuggestions")!.Bounds.Height > 1);
            }, 440, 420);
        }, CancellationToken.None);

    private static (double X, double Y) Chip(LinkHarness h, int measure)
    {
        var endMs = At(10, 0).GetPositionMs(h.Project.Beats);
        var ribbon = RibbonLaneRules.Compose(new RibbonVisibility(), new RibbonContent(0, false, false, false, HasLinks: true));
        return (At(measure, 0).GetPositionMs(h.Project.Beats) * Width / endMs, ribbon.BarTop(ribbon.LinkY) + ribbon.Bar * 0.5);
    }

    /// <summary>The timeline frame (if dumped) with the given layers over it.</summary>
    private static Control Frame(string frameName, params Control[] layers)
    {
        var grid = new Grid { Width = Width, Height = Height, Background = new SolidColorBrush(Color.FromRgb(14, 15, 20)) };
        var path = Path.Combine(AppContext.BaseDirectory, "TestResults", "headless", frameName + ".png");
        if (File.Exists(path)) grid.Children.Add(new Image { Source = new Bitmap(path), Stretch = Stretch.None });
        foreach (var layer in layers) grid.Children.Add(layer);
        return grid;
    }

    private static void Settle(Control control)
    {
        Dispatcher.UIThread.RunJobs();
        for (var pass = 0; pass < 3; pass++) control.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Control content, string file, Action act, int width = Width, int height = Height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        try
        {
            act();
            var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
            if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
        }
    }
}
