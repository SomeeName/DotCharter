using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #246: the Song tab's Sections and Tones are the Phrases panel's shape. Each builds inside
///     the inspector grammar and MEASURES non-zero: a name's chevron, rename box, count and
///     Delete, and its places (bar and time) as jump buttons. Apply stays hidden until the
///     name changes. The behaviour is pinned in the gate by <c>MarkerGroupsViewModelTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongInspectorMarkerGroupsUiPerfSmokeTests
{
    [Theory]
    [InlineData("SongSectionsBody", "song-tab-sections.png")]
    [InlineData("SongTonesBody", "song-tab-tones.png")]
    public Task EachList_LaysOutEveryControl_AndApplyWaitsForAChange(string body, string capture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var vm = new SongOverviewViewModel(state, new HistoryService(logger), logger, FakeLocalization.English(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy);
            var tab = new SongInspectorView { DataContext = vm };
            tab.Rail.Choose(body);
            var scope = new UserControl { Content = tab };
            scope.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window
            {
                Width = 420, Height = 720, Content = scope, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x14, 0x15, 0x19))
            };
            window.Show();
            try
            {
                state.ActiveProject = Song();
                Settle(window);
                var list = tab.GetVisualDescendants().OfType<MarkerGroupsView>().Single(v => v.IsEffectivelyVisible);
                foreach (var name in new[] { "MarkerGroupExpand", "MarkerRenameBox", "MarkerDelete", "MarkerJump" })
                {
                    var control = list.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible, $"{name} is hidden on a list with names.");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {control.Bounds} — it rendered nothing.");
                }

                var jump = list.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MarkerJump");
                Assert.Matches(@"^(Starting tone|#1 · bar \d+ · \d:\d\d\.\d) · Lead", jump.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                var apply = list.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MarkerRenameApply");
                Assert.False(apply.IsEffectivelyVisible, "Apply shows before anything was typed.");
                Capture(window, capture);

                var screen = (MarkerGroupsViewModel)list.DataContext!;
                screen.Groups[0].EditName += " A";
                Dispatcher.UIThread.RunJobs();
                Settle(window);
                Assert.True(apply.IsEffectivelyVisible && apply.Bounds.Width > 1, "Apply stays hidden after the name changed.");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>Two arrangements sharing their sections, a tone change each and one placed tone nobody defined.</summary>
    private static ChartProject Song()
    {
        var project = new ChartProject();
        for (var i = 0; i < 128; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120 });
        foreach (var name in new[] { "Lead", "Rhythm" })
        {
            var arrangement = new Arrangement { Name = name, StartingTone = "clean" };
            foreach (var (beat, section) in new[] { (0, "intro"), (16, "verse"), (48, "chorus"), (80, "verse"), (112, "chorus") })
                arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Section = section });
            arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(48), ToneName = "drive" });
            project.Arrangements.Add(arrangement);
        }

        return project;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    /// <summary>
    ///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_PHRASES_CAPTURE_DIR</c> set, writes
    ///     the list as a PNG for review.
    /// </summary>
    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_PHRASES_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
