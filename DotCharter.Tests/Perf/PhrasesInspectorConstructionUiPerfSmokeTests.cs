using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Phrases;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The one Phrases panel builds, attaches inside the inspector grammar and MEASURES non-zero on
///     a markers-only arrangement: the phrase line's chevron, rename box and Delete, an iteration's
///     jump and Propagate, and the export note. Apply stays hidden until the rename text changes.
///     The behaviour is pinned in the gate by <c>PhrasesInspectorViewModelTests</c> and
///     <c>PhraseEditsTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class PhrasesInspectorConstructionUiPerfSmokeTests
{
    private sealed class NoPropagation : IPhraseIterationPropagationService
    {
        public int PropagateToAllIterations(PhraseIteration source) => 0;
    }

    [Fact]
    public async Task ThePanel_LaysOutEveryControl_AndApplyWaitsForAChange() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var beats = Enumerable.Range(0, 128).Select(i => new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 }).ToList();
            var arrangement = new Arrangement { Name = "Lead" };
            foreach (var (beat, name) in new[] { (0, "intro"), (8, "verse"), (24, "chorus"), (40, "verse"), (56, "chorus"), (72, "outro") })
                arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name });
            var vm = new PhrasesInspectorViewModel(() => arrangement, () => beats, _ => { }, new NoPropagation(),
                FakeLocalization.English(), new HistoryService(new FakeAppLogger()));
            vm.Refresh();

            var view = new PhrasesInspectorView { DataContext = vm };
            var host = new UserControl { Content = view };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window
            {
                Width = 420, Height = 640, Content = host, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x14, 0x15, 0x19))
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                foreach (var name in new[] { "PhraseGroupExpand", "PhraseRenameBox", "PhraseDelete", "PhrasesExportNote" })
                {
                    var control = view.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible, $"{name} is hidden on a panel with phrases.");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {control.Bounds} — it rendered nothing.");
                }

                var apply = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PhraseRenameApply");
                Assert.False(apply.IsEffectivelyVisible, "Apply shows before anything was typed.");
                Capture(window, "phrases-panel-1-idle.png");

                vm.Phrases[0].EditName = "intro A";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(apply.IsEffectivelyVisible && apply.Bounds.Width > 1, "Apply stays hidden after the name changed.");
                Capture(window, "phrases-panel-2-renaming.png");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_PHRASES_CAPTURE_DIR</c> set, writes
    ///     the panel as a PNG for review.
    /// </summary>
    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_PHRASES_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
