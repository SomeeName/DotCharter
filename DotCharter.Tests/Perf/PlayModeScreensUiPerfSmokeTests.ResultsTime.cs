// Results section timestamps must fit their shared time column, including songs longer than a day.
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    [Fact]
    public async Task ResultsSectionTimes_KeepFullElapsedHoursInsideAlignedColumns()
    {
        await WithView((view, vm) =>
        {
            var result = new LivePlaySessionResult(1000, 1, 2, 0, 2, 2, 90_062, 100, 1);
            var best = new PracticeBests(0, 0, 0, 0);
            vm.RunResults.Show(new LivePlaySessionReport(result, best, best, true, "long-chart", "Lead",
                [new SectionResult("Intro", 0, 500, 1, 1, 0, 1, 2),
                 new SectionResult("Long outro", 90_061_000, 90_062_000, 1, 1, 0, 3, 4)]));
            vm.CurrentScreen = PlayModeScreen.Results;
            view.UpdateLayout();
            // Shared-size columns settle on the dispatcher layout pass after realization.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            AssertLaidOut(view, "ResultsSectionList");
            var list = Find(view, "ResultsSectionList");
            var times = list.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is SectionRow row && t.Text == row.TimeLabel).ToArray();
            Assert.Equal(2, times.Length);
            var longTime = Assert.Single(times, t => t.Text == "25:01:01");
            CaptureResultsTime(view);
            var formatted = new FormattedText(longTime.Text!, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(longTime.FontFamily, longTime.FontStyle, longTime.FontWeight),
                longTime.FontSize, longTime.Foreground);
            Assert.True(longTime.Bounds.Width >= formatted.WidthIncludingTrailingWhitespace,
                $"'{longTime.Text}' needs {formatted.WidthIncludingTrailingWhitespace}px but has {longTime.Bounds.Width}px");
            Assert.Equal(times[0].Bounds.Width, times[1].Bounds.Width, 2);
            foreach (var time in times)
            {
                var row = Assert.IsType<Grid>(time.Parent);
                var name = Assert.Single(row.Children.OfType<TextBlock>(), t => Grid.GetColumn(t) == 2);
                Assert.True(time.Bounds.Right <= name.Bounds.Left);
                Assert.True(row.Bounds is { Width: > 1, Height: > 1 });
            }
            LeaveResults(view, vm);
        });
    }

    private static void CaptureResultsTime(Control view)
    {
        var directory = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(directory, "results-time-column.png"), new PngBitmapEncoderOptions());
    }
}
