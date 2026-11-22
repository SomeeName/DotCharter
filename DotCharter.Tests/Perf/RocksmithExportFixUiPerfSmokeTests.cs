using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The export dialog's #184 surfaces, attached to a real window and laid out: the findings
///     come FIRST (above the target section), every finding with a verb behind it shows a Fix
///     that MEASURES, and one without shows a blank one that keeps the column: not drawn, not
///     clickable, not focusable, out of the screen reader's view.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RocksmithExportFixUiPerfSmokeTests
{
    private const double Width = 1600;
    private const double Height = 1000;

    private static void WithDialog(IRocksmithExportScreen screen, Action<RocksmithExportDialogView> assert)
    {
        var view = new RocksmithExportDialogView { DataContext = screen };
        var window = new Window { Width = Width, Height = Height, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            assert(view);
        }
        finally
        {
            window.Close();
        }
    }

    private static List<Button> FixButtons(Control view) =>
    [
        .. view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.DataContext is IExportFinding finding && ReferenceEquals(b.Command, finding.FixCommand))
    ];

    private static bool Shown(Button link) => link.IsEffectivelyVisible && link.Opacity > 0 && link.IsHitTestVisible;

    [Fact]
    public async Task Fix_Measures_AndTheFindingsComeFirst()
    {
        await HeadlessSessionFixture.Dispatch(() => WithDialog(FakeRocksmithExportScreen.Default(), view =>
        {
            var screen = (IRocksmithExportScreen)view.DataContext!;
            var fixes = FixButtons(view);
            Assert.Equal(screen.ValidationItems.Count, fixes.Count);
            foreach (var fix in fixes)
            {
                var finding = (IExportFinding)fix.DataContext!;
                Assert.Equal(finding.HasFix, Shown(fix));
                Assert.Equal(finding.HasFix, fix.Focusable);
                Assert.Equal(finding.HasFix, AutomationProperties.GetAccessibilityView(fix) != AccessibilityView.Raw);
                Assert.True(fix.Bounds is { Width: > 1, Height: > 1 }, $"Fix for '{finding.Message}' laid out as {fix.Bounds}");
            }

            Assert.Contains(fixes, Shown);
            Assert.Contains(fixes, f => !Shown(f));

            // One column per verb whichever a row shows: every visible Go to and every visible Fix line up.
            static double Left(Control c, Control root) => c.TranslatePoint(default, root)!.Value.X;
            var jumps = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.DataContext is IExportFinding f && ReferenceEquals(b.Command, f.JumpCommand) && Shown(b))
                .ToList();
            Assert.True(jumps.Count >= 2, $"only {jumps.Count} visible Go to");
            Assert.Single(jumps.Select(j => Math.Round(Left(j, view))).Distinct());
            Assert.Single(fixes.Where(Shown).Select(f => Math.Round(Left(f, view))).Distinct());

            var target = view.GetVisualDescendants().OfType<ComboBox>().Single(t => t.Name == "PackageFormPicker");
            var summary = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ValidationSummaryText");
            Assert.True(summary.TranslatePoint(default, view)!.Value.Y < target.TranslatePoint(default, view)!.Value.Y,
                "the findings are no longer above the export target");
        }), CancellationToken.None);
    }

    /// <summary>
    ///     #953: the preset toolbar is gone and the DLC Builder surfaces are in, each MEASURED: the
    ///     Version box, both Auto volume buttons, the test folder and the Test export button.
    /// </summary>
    [Fact]
    public async Task VersionTestExportAndAutoVolume_Construct_AndMeasure()
    {
        await HeadlessSessionFixture.Dispatch(() => WithDialog(FakeRocksmithExportScreen.Default(), view =>
        {
            var visual = view.GetVisualDescendants().OfType<Control>().ToList();
            foreach (var name in new[] { "VersionBox", "AutoVolumeButton", "AutoPreviewVolumeButton", "TestExportButton", "ExportButton" })
            {
                var control = visual.Single(c => c.Name == name);
                Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {control.Bounds}");
            }

            // No preset picker is left: the only ComboBoxes are Platform and Package form.
            Assert.Equal(2, view.GetVisualDescendants().OfType<ComboBox>().Count());
        }), CancellationToken.None);
    }

    /// <summary>
    ///     #497: the export keeps a door to the whole-project report: a Show in Issues tab button on the summary's line,
    ///     laid out, named, bound to the contract's command, and off while the fake is building.
    /// </summary>
    [Fact]
    public async Task ShowInIssuesButton_Measures_IsBoundAndNamed()
    {
        await HeadlessSessionFixture.Dispatch(() => WithDialog(FakeRocksmithExportScreen.Default(), view =>
        {
            var screen = (IRocksmithExportScreen)view.DataContext!;
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShowInIssuesButton");
            Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"Show in Issues tab laid out as {button.Bounds}");
            Assert.Same(screen.ShowInIssuesCommand, button.Command);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            var summary = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ValidationSummaryText");
            Assert.InRange(Math.Abs(summary.TranslatePoint(default, view)!.Value.Y - button.TranslatePoint(default, view)!.Value.Y), 0, 40);
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            NarrowWidthLayout.Capture((Window)TopLevel.GetTopLevel(view)!, "export-dialog-after");
        }), CancellationToken.None);
    }
}
