using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #847: the song-list name warning in the Rocksmith export dialog, in every shipped language. It has no
///     location, so its text starts at the dot's column instead of after an empty 160 px gap, left-aligned; its
///     Song metadata button is laid out and not clipped; Include vocals reads unticked and disabled while the
///     project has no vocal track. Width-shaped, so it is in the release's <c>$HeadlessLayoutFilter</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RocksmithExportNameWarningUiPerfSmokeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task TheNameWarning_StartsAtTheDot_AndItsButtonIsLaidOut(string culture)
    {
        var problems = new List<string>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            try
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
                var loc = LocalizationService.Instance;
                var message = string.Format(CultureInfo.CurrentCulture, loc["Export.Validation.DisplayNameGlyphs"], "愛傷");
                var view = new RocksmithExportDialogView { DataContext = FakeRocksmithExportScreen.NameWarning(message) };
                var window = new Window { Width = 1280, Height = 900, Content = DialogHost.Preview(view) };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Capture(window, $"rocksmith-export-name-warning-{culture}.png");

                    var text = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == message);
                    var dot = text.GetVisualParent()!.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().First();
                    var gap = text.TranslatePoint(default, view)!.Value.X - dot.TranslatePoint(new Point(dot.Bounds.Width, 0), view)!.Value.X;
                    if (!text.IsEffectivelyVisible || text.Bounds.Height < 10) problems.Add($"{culture}: the warning text is not laid out ({text.Bounds})");
                    if (text.TextAlignment != Avalonia.Media.TextAlignment.Left) problems.Add($"{culture}: the warning text is not left-aligned");
                    if (gap > 20) problems.Add($"{culture}: {gap:0} px between the dot and the text");

                    var button = view.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible &&
                        b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == loc["Dialog_Export_OpenSongMetadata"]));
                    var label = button.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == loc["Dialog_Export_OpenSongMetadata"]);
                    if (button.Bounds is not { Width: > 1, Height: >= 20 }) problems.Add($"{culture}: the button is laid out as {button.Bounds}");
                    if (label.Bounds.Width + 0.5 < label.DesiredSize.Width) problems.Add($"{culture}: the button label is clipped");

                    var vocals = view.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name is null && c.IsEffectivelyVisible && c.IsChecked == false && !c.IsEnabled);
                    if (vocals.Bounds.Width < 1) problems.Add($"{culture}: Include vocals is not laid out");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
            }
        }, CancellationToken.None);

        foreach (var problem in problems) output.WriteLine(problem);
        Assert.Empty(problems);
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(System.IO.Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
