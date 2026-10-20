using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Rocksmith export dialog at a laptop and a desktop window (#214), on every gallery
///     variant: each reason Export is greyed is printed ONCE, in the strip above the footer; the
///     findings scroll with the body (no list scroller nested in it), so the
///     <see cref="FakeRocksmithExportScreen.Full" /> variant (24 findings, a
///     blocked reason and a build result) shows a real number of whole rows without scrolling;
///     the footer is inside the window and nothing is drawn over it; Review report is an
///     outlined Fire button on the summary's line, with its name. Width-shaped, so it is in the
///     release's <c>$HeadlessLayoutFilter</c> (de-DE and ja-JP).
/// </summary>
[Collection("HeadlessUI")]
public sealed class RocksmithExportLayoutUiPerfSmokeTests(ITestOutputHelper output)
{
    public static TheoryData<int, int> Sizes => new() { { 1280, 720 }, { 1920, 1080 } };

    /// <summary>
    ///     Whole finding rows the Full variant must show before any scroll. It is the worst case:
    ///     the reason strip and a three-line build result sit in the fixed band above the footer.
    ///     Measured in en-US / de-DE / ja-JP: at 1280×720 the body viewport is 299–303 px, the
    ///     Validation label and the summary line with its button take 91–92 px and a row 29–31 px, so
    ///     6 rows are whole in all three; at 1920×1080 (an 820 px card, 463–467 px of body) 11–12.
    ///     The floors are those measurements less a row's slack at the larger size. Before #214
    ///     the list was a 150 px scroller under the five-line report and showed 3.
    /// </summary>
    public static TheoryData<int, int, int> SizesWithRows => new() { { 1280, 720, 6 }, { 1920, 1080, 10 } };

    private static void WithDialog(IRocksmithExportScreen screen, int width, int height, Action<RocksmithExportDialogView, Window> assert)
    {
        var view = new RocksmithExportDialogView { DataContext = screen };
        var window = new Window { Width = width, Height = height, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            assert(view, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect RectIn(Control control, Visual root) =>
        new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);

    private static ScrollViewer Body(Control view) =>
        view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Classes.Contains("DialogBody"));

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task EachBlockingReason_IsPrintedExactlyOnce_AboveTheFooter(int width, int height)
    {
        var problems = new List<string>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var variant in FakeRocksmithExportScreen.Variants)
            {
                WithDialog(variant.Create(), width, height, (view, window) =>
                {
                    Capture(window, $"rocksmith-export-{variant.Name.ToLowerInvariant()}-{width}x{height}.png");
                    var screen = (IRocksmithExportScreen)view.DataContext!;
                    var at = $"{variant.Name} {width}x{height}";
                    var texts = view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
                    var reasons = screen.ExportBlockedReason.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (screen.IsExportBlocked != reasons.Length > 0) problems.Add($"{at}: IsExportBlocked disagrees with the reason");
                    foreach (var reason in reasons)
                    {
                        var copies = texts.Where(t => t.Text!.Contains(reason, StringComparison.Ordinal)).ToList();
                        if (copies.Count != 1 || copies[0].Name != "ExportBlockedText")
                        {
                            problems.Add($"{at}: \"{reason}\" is printed {copies.Count} times " +
                                         $"({string.Join(", ", copies.Select(c => c.Name ?? "an unnamed TextBlock in a " + c.Parent?.GetType().Name))})");
                        }
                    }

                    var export = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExportButton");
                    if (screen.ExportBlockedReason != (ToolTip.GetTip(export) as string ?? "")) problems.Add($"{at}: Export's tooltip is not the reason");
                    if (!screen.IsExportBlocked) return;
                    var strip = RectIn(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ExportBlockedStrip"), view);
                    var footer = RectIn(view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("DialogFooter")), view);
                    if (strip.Bottom > footer.Top + 0.5) problems.Add($"{at}: the reason strip {strip} runs into the footer {footer}");
                });
            }
        }, CancellationToken.None);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(SizesWithRows))]
    public async Task TheFindings_ScrollWithTheBody_AndTheFooterStaysClear(int width, int height, int minWholeRows)
    {
        await HeadlessSessionFixture.Dispatch(() => WithDialog(FakeRocksmithExportScreen.Full(), width, height, (view, window) =>
        {
            var screen = (IRocksmithExportScreen)view.DataContext!;
            var body = Body(view);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, screen.ValidationItems));

            // ONE scroll region: the list never scrolls on its own inside the body.
            Assert.True(double.IsPositiveInfinity(list.MaxHeight), $"the findings list is capped at {list.MaxHeight} px");
            foreach (var inner in list.GetVisualDescendants().OfType<ScrollViewer>())
            {
                Assert.True(inner.Extent.Height <= inner.Viewport.Height + 0.5,
                    $"the findings list scrolls by itself: extent {inner.Extent.Height:0} in a {inner.Viewport.Height:0} px viewport");
            }

            var viewport = RectIn(body, view);
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible).Select(r => RectIn(r, view)).ToList();
            var whole = rows.Count(r => r.Top >= viewport.Top - 0.5 && r.Bottom <= viewport.Bottom + 0.5);
            output.WriteLine($"{width}x{height}: body viewport {viewport.Height:0} px, {whole} of {screen.ValidationItems.Count} findings whole, " +
                             $"row height {(rows.Count > 0 ? rows[0].Height : 0):0} px, first row at {(rows.Count > 0 ? rows[0].Top - viewport.Top : 0):0} px");
            Assert.True(whole >= minWholeRows, $"{width}x{height}: only {whole} whole finding rows in a {viewport.Height:0} px body");
            Assert.True(screen.ValidationItems.Count > whole,
                $"the Full variant shows all {whole} findings at {width}x{height}: it no longer tests a list longer than the window");

            // The footer: inside the window, below everything else, both actions laid out.
            var footer = RectIn(view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("DialogFooter")), view);
            var footerInWindow = new Rect(view.TranslatePoint(footer.Position, window)!.Value, footer.Size);
            Assert.True(new Rect(0, 0, width, height).Contains(footerInWindow), $"the footer {footerInWindow} leaves the {width}x{height} window");
            Assert.True(viewport.Bottom <= footer.Top + 0.5, $"the body {viewport} runs into the footer {footer}");
            // The strips between the body and the footer; one inside the body is clipped by its viewport.
            foreach (var strip in view.GetVisualDescendants().OfType<Border>()
                         .Where(b => b.Classes.Contains("DlgStrip") && b.IsEffectivelyVisible && !b.GetVisualAncestors().Contains(body)))
            {
                var r = RectIn(strip, view);
                Assert.True(r.Bottom <= footer.Top + 0.5, $"a strip {r} is drawn over the footer {footer}");
            }

            var actions = view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ExportButton" || b.IsCancel).ToList();
            Assert.Equal(2, actions.Count);
            foreach (var action in actions)
            {
                var r = RectIn(action, view);
                Assert.True(r is { Width: > 1, Height: > 1 } && footer.Contains(r), $"{action.Content} laid out at {r}, footer {footer}");
            }
        }), CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task ReviewReport_IsAnOutlinedFireButton_OnTheSummaryLine(int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() => WithDialog(FakeRocksmithExportScreen.Default(), width, height, (view, _) =>
        {
            // #952: Prepare for Rocksmith is gone; the report button keeps the summary's line.
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "PrepareButton");
            var review = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReviewReportButton");
            Assert.Contains("FireGhost", review.Classes);
            Assert.True(review.Bounds is { Width: > 1, Height: >= 30 }, $"Review laid out as {review.Bounds}");
            Assert.True(review.BorderThickness.Top >= 1 && review.BorderBrush is not null, "Review draws no outline: it reads as a caption");
            Assert.Equal(LocalizationService.Instance["Dialog_Export_ReviewReport"], ControlAutomationPeer.CreatePeerForElement(review).GetName());

            var summary = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ValidationSummaryText");
            var r = RectIn(review, view);
            var s = RectIn(summary, view);
            Assert.True(r.Top < s.Bottom && s.Top < r.Bottom, $"Review {r} is not on the summary's line {s}");
        }), CancellationToken.None);
    }

    /// <summary>#743: the split-chords row shows for a 7-/8-string guitar (the LongText
    /// variant), lays out with a size and names its box; every other project hides it.</summary>
    [Fact]
    public async Task SplitExtendedChords_ShowsOnlyForASevenOrEightStringGuitar()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDialog(FakeRocksmithExportScreen.LongText(), 1280, 720, (view, _) =>
            {
                var row = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "SplitExtendedChordsRow");
                Assert.True(row.IsEffectivelyVisible);
                Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"split row laid out as {row.Bounds}");
                var box = row.GetVisualDescendants().OfType<CheckBox>().Single();
                Assert.True(box.Bounds is { Width: > 1, Height: > 1 }, $"split box laid out as {box.Bounds}");
                Assert.Equal(LocalizationService.Instance["Dialog_Export_SplitExtendedChords"],
                    ControlAutomationPeer.CreatePeerForElement(box).GetName());
            });
            WithDialog(FakeRocksmithExportScreen.Default(), 1280, 720, (view, _) =>
                Assert.False(view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "SplitExtendedChordsRow").IsEffectivelyVisible));
        }, CancellationToken.None);
    }

    /// <summary>#756: without the user's key file every package is written unencrypted,
    /// and the Package section says so (the Full variant); with keys it says nothing.</summary>
    [Fact]
    public async Task UnencryptedHint_ShowsOnlyWhenPackagesAreWrittenUnencrypted()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDialog(FakeRocksmithExportScreen.Full(), 1280, 720, (view, _) =>
            {
                var hint = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "UnencryptedPackageHint");
                Assert.True(hint.IsEffectivelyVisible);
                Assert.True(hint.Bounds is { Width: > 1, Height: > 1 }, $"unencrypted hint laid out as {hint.Bounds}");
                Assert.Equal(LocalizationService.Instance["Dialog_Export_WrittenUnencrypted"], hint.Text);
            });
            WithDialog(FakeRocksmithExportScreen.Default(), 1280, 720, (view, _) =>
                Assert.False(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "UnencryptedPackageHint").IsEffectivelyVisible));
        }, CancellationToken.None);
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
