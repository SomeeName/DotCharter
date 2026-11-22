using System.Globalization;
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
///     The "no show lights" and "tone has no definition" rows of the Rocksmith export dialog, each with its
///     one-click Fix, in every shipped language: the rows are laid out and the Fix buttons measure non-zero.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RocksmithExportOneClickFixesUiPerfSmokeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task TheOneClickFixRows_AreLaidOutWithTheirFixButton(string culture)
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
                var showlights = loc["Export.Validation.NoShowlights"];
                var tone = string.Format(CultureInfo.CurrentCulture, loc["Export.Validation.ToneNotDefined"], "clean_verse");
                var showTip = loc["Export_FixTip_Showlights"];
                var toneTip = string.Format(CultureInfo.CurrentCulture, loc["Export_FixTip_DefineTones"], "Lead");
                var view = new RocksmithExportDialogView { DataContext = FakeRocksmithExportScreen.OneClickFixes(showlights, showTip, tone, toneTip) };
                var window = new Window { Width = 1280, Height = 900, Content = DialogHost.Preview(view) };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Capture(window, $"rocksmith-export-one-click-fixes-{culture}.png");

                    foreach (var (message, tip) in new[] { (showlights, showTip), (tone, toneTip) })
                    {
                        var text = view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == message);
                        if (text is null || !text.IsEffectivelyVisible || text.Bounds.Height < 10)
                        {
                            problems.Add($"{culture}: the row '{message}' is not laid out");
                            continue;
                        }

                        var fix = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible &&
                            Avalonia.Automation.AutomationProperties.GetName(b) == tip);
                        if (fix is null) problems.Add($"{culture}: no Fix button carries '{tip}'");
                        else if (fix.Bounds is not { Width: > 1, Height: >= 20 }) problems.Add($"{culture}: the Fix button is laid out as {fix.Bounds}");
                        else if (fix.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is { } label && label.Bounds.Width + 0.5 < label.DesiredSize.Width)
                            problems.Add($"{culture}: the Fix label is clipped");
                    }
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
