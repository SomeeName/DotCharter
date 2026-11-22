using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Services.Export.Validation;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #858: the six-string drop finding, in the swept culture (en/de/ja through DOTCHARTER_HEADLESS_CULTURE),
///     is drawn in the export dialog's findings list and MEASURES; a "before" frame without it is captured too.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SixStringDropFindingUiPerfSmokeTests
{
    [Fact]
    public async Task The_finding_is_shown_with_its_counts_and_measures()
    {
        var loc = FakeLocalization.For(HeadlessSessionFixture.SweptCulture);
        var finding = new ExportValidationFinding("Lead", ExportFindingSeverity.Warning, ChartExportValidator.KeySixStringDrop, null, "7|406|244|12");
        var message = ExportFindingFormatter.FormatMessage(finding, loc);
        Assert.NotEqual(ChartExportValidator.KeySixStringDrop, message);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Show(FakeRocksmithExportScreen.Default(), null, "export-dialog-858-before");
            Show(FakeRocksmithExportScreen.WithWarning("Lead", message), message, "export-dialog-858-after");
        }, CancellationToken.None);
    }

    private static void Show(FakeRocksmithExportScreen screen, string? message, string capture)
    {
        var view = new RocksmithExportDialogView { DataContext = screen };
        var window = new Window { Width = 1600, Height = 1000, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var shown = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == message).ToList();
            if (message is null)
            {
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("406") == true);
            }
            else
            {
                var text = Assert.Single(shown);
                Assert.True(text.Bounds is { Width: > 1, Height: > 1 }, $"the finding laid out as {text.Bounds}");
                Assert.Contains("406", text.Text);
                Assert.Contains("244", text.Text);
                Assert.True(text.TranslatePoint(new Point(text.Bounds.Width, 0), view)!.Value.X <= view.Bounds.Width + 1, "the finding runs off the dialog");
            }

            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            NarrowWidthLayout.Capture(window, capture);
        }
        finally
        {
            window.Close();
        }
    }
}
