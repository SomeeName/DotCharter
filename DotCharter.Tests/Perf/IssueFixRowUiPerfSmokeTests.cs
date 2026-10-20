using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #618: the Issues tab's fix menu (<c>IssueFixRow</c> in <c>IssuesInspectorView.axaml</c>) builds,
///     attaches inside the inspector grammar and MEASURES non-zero over the app's own
///     <see cref="InspectorViewModel" /> once a finding with two fixes is on offer — a row whose
///     styles or bindings failed still arranges, it just has no size. The behaviour is pinned in the
///     gate by <c>IssueFixViewModelTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class IssueFixRowUiPerfSmokeTests
{
    [Fact]
    public async Task TheFixMenu_ShowsBothFixes_AndMeasures() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
                .GetRequiredService<InspectorViewModel>();
            var (arr, level, chord, anchor) = Fixture();
            vm.Issues.IssueFix.Offer(new ArrangementIssue(ArrangementIssueType.IndexFingerNotOnAnchorFret, "E04", 0,
                Chord: chord, Anchor: anchor), arr, level);

            var view = new IssuesInspectorView { DataContext = vm };
            var host = new UserControl { Content = view };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window { Width = 420, Height = 900, Content = host, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x14, 0x15, 0x19)) };
            window.Show();
            try
            {
                window.UpdateLayout();
                foreach (var name in new[] { "IssueFixRow", "IssueFixDefaultButton", "IssueFixAlternativeButton" })
                {
                    var control = view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible, $"{name} is hidden with two fixes on offer.");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {control.Bounds} — it rendered nothing.");
                }

                var fix = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "IssueFixDefaultButton");
                Assert.Equal(vm.Issues.IssueFix.DefaultLabel, fix.Content);
                Capture(window, "issue-fix-row-618.png");
            }
            finally
            {
                vm.Issues.IssueFix.Offer(null, null, null);
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_ISSUEFIX_CAPTURE_DIR</c> set, writes
    ///     the Issues tab with the fix menu open as a PNG for review.
    /// </summary>
    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_ISSUEFIX_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A double stop fingered 2-3 under FHP 2 that the arrangement also plays 1-3 there.</summary>
    private static (Arrangement Arr, ChartLevel Level, Chord Chord, FretAnchor Anchor) Fixture()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        foreach (var (id, index) in new[] { (0, 2), (1, 1) })
        {
            arr.ChordTemplates.Add(new ChordTemplate
            {
                Id = id,
                Frets = new Dictionary<int, int> { [3] = 2, [4] = 3 },
                Fingers = new Dictionary<int, int> { [3] = index, [4] = 3 }
            });
        }

        var anchor = new FretAnchor(new FractionalPosition(0), 2, 4);
        level.Anchors.Add(anchor);
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        level.Chords.Add(chord);
        level.Chords.Add(new Chord { Position = new FractionalPosition(6), TemplateId = 1 });
        return (arr, level, chord, anchor);
    }
}
