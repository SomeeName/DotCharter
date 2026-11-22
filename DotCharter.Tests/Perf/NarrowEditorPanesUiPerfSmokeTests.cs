using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Catalog;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Shell;
using static DotCharter.Tests.Perf.NarrowWidthLayout;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Editor panes docked NARROW or SHORT (#591), laid out for real in the swept culture (the
///     release re-runs this class in de-DE and ja-JP, where captions are widest):
///     <list type="bullet">
///         <item>the 3D preview's practice strip wraps inside a narrow preview instead of pushing
///         its calibration, notation and stats controls off the right edge;</item>
///         <item>the Riff Library shares a narrow pane by ratio, so the name box and verbs stay
///         inside it (a fixed 240 px list left the details a few dozen pixels);</item>
///         <item>the stacked tracks scroll once their rows no longer fit, instead of squeezing every
///         row header until its buttons are cut off.</item>
///     </list>
/// </summary>
[Collection("HeadlessUI")]
public sealed class NarrowEditorPanesUiPerfSmokeTests
{
    private const double Tolerance = 0.5;

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public Task ThePracticeStrip_WrapsInsideANarrowPreview(string culture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const double width = 360;
            var services = DotCharter.UI.App.Services!;
            var seeder = UiSweepDataContexts.Build(services);
            var shell = Assert.IsType<MainWindowViewModel>(seeder.For(typeof(MainWindowViewModel)).Value);
            var localization = LocalizationService.Instance;
            var previousCulture = localization.ConfiguredCulture;
            var messages = new List<string>();
            foreach (var language in new[] { "en-US", "de-DE", "ja-JP" })
            {
                localization.SetConfiguredCulture(language);
                messages.Add(localization["LivePlay_CalibrateFailed"]);
            }
            localization.SetConfiguredCulture(culture);
            var previousStatus = shell.LivePlay.CalibrationStatus;
            var previousSummary = shell.LivePlay.PassSummary;
            shell.LivePlay.PassSummary = localization.Format("LivePlay_PassSummary", 123, 99.9);
            shell.LivePlay.CalibrationStatus = messages.MaxBy(message => message.Length)!;
            var strip = new Preview3DPracticeStripView { DataContext = shell };
            var window = NewWindow(width, 400, strip);
            window.Show();
            try
            {
                Settle(window);
                Assert.True(strip.Bounds is { Width: > 1, Height: > 1 },
                    $"the practice strip laid out as {strip.Bounds}: it rendered nothing");

                var controls = strip.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c is not Popup { IsOpen: false })
                    .ToList();
                Assert.True(controls.Count >= 8,
                    $"only {controls.Count} strip controls found; the strip lost its verbs — repoint this test");

                var offenders = new List<string>();
                foreach (var control in controls)
                {
                    // Template icons have Viewbox transforms; compare both transformed corners.
                    var first = control.TranslatePoint(default, strip)!.Value;
                    var last = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), strip)!.Value;
                    var left = Math.Min(first.X, last.X);
                    var right = Math.Max(first.X, last.X);
                    var top = Math.Min(first.Y, last.Y);
                    var bottom = Math.Max(first.Y, last.Y);
                    Report($"practice strip {control.GetType().Name} {left:0.#}..{right:0.#} of {strip.Bounds.Width:0.#}");
                    if (left < -Tolerance || right > strip.Bounds.Width + Tolerance ||
                        top < -Tolerance || bottom > strip.Bounds.Height + Tolerance)
                    {
                        offenders.Add($"{control.GetType().Name} '{control.Name}' at {left:0.#},{top:0.#} ends at {right:0.#}");
                    }
                }

                Assert.True(offenders.Count == 0,
                    $"controls past the right edge of a {width} px practice strip:\n  " +
                    string.Join("\n  ", offenders));

                var status = strip.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == shell.LivePlay.CalibrationStatus);
                Assert.True(status.TextLayout.TextLines.Count > 1,
                    "the real calibration failure text must wrap at 360 px");
                Assert.True(status.TextLayout.WidthIncludingTrailingWhitespace <= status.Bounds.Width + Tolerance,
                    $"calibration text paints {status.TextLayout.WidthIncludingTrailingWhitespace} px in a {status.Bounds.Width} px slot");
                var panel = Named<WrapPanel>(strip, "PracticeStripPanel");
                Assert.True(panel.Bounds.Height > panel.Children.Max(c => c.Bounds.Height) + 1,
                    "the strip did not wrap to a second line at a narrow width");
                Capture(window, $"practice-strip-narrow-{culture}");
            }
            finally
            {
                shell.LivePlay.CalibrationStatus = previousStatus;
                shell.LivePlay.PassSummary = previousSummary;
                localization.SetConfiguredCulture(previousCulture);
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(160)]
    [InlineData(480)]
    public Task TheRiffLibrary_KeepsItsNameBoxInsideANarrowPane(double height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const double width = 320;
            var seeder = UiSweepDataContexts.Build(DotCharter.UI.App.Services!);
            var model = Assert.IsType<PhraseCatalogViewModel>(seeder.For(typeof(PhraseCatalogViewModel)).Value);
            var previousStatus = model.StatusMessage;
            var previousEntry = model.SelectedEntry;
            var selectedEntry = new PhraseCatalogEntry
            {
                Id = "narrow-layout", Name = "UI layout fixture", Source = new PhraseSourceContext(),
                Payload = new ClipboardPayload()
            };
            var message = LocalizationService.Instance["PhraseCatalog_CaptureEmpty"];
            model.StatusMessage = message;
            var view = new PhraseCatalogView { DataContext = model };
            var window = NewWindow(width, height, view);
            window.Show();
            try
            {
                Settle(window);
                // Attach reloads the catalog; select the in-memory fixture after that reload.
                model.SelectedEntry = selectedEntry;
                Settle(window);
                Assert.NotEmpty(model.CompatibilityMessage);
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 },
                    $"the Riff Library laid out as {view.Bounds}: it rendered nothing");

                // Design D (#751): at this Tiny width every card is a page. The details card holds the
                // verdict, the capture card the name box; the status is the pinned footer's.
                var cards = view.Cards;
                var details = Named<ColumnFlowCard>(view, "RiffDetailsCard");
                cards.ShowPage(details);
                Settle(window);
                Assert.Contains(details, cards.ShownCards);
                var verdicts = details.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.Text == model.CompatibilityMessage && text.IsEffectivelyVisible).ToList();
                Assert.NotEmpty(verdicts);
                foreach (var text in verdicts)
                    Assert.True(text.TextLayout.Width <= text.Bounds.Width + Tolerance);
                var status = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == message && t.IsEffectivelyVisible);
                Assert.True(status.Bounds.Width > 1 && Span(status, view).Right <= view.Bounds.Width + Tolerance,
                    $"the status line laid out {status.Bounds}");

                var capture = Named<ColumnFlowCard>(view, "RiffCaptureCard");
                cards.ShowPage(capture);
                Settle(window);
                var nameBox = Named<TextBox>(view, "NewPhraseNameBox");
                var (cardLeft, cardRight) = Span(capture, view);
                var (boxLeft, boxRight) = Span(nameBox, view);
                Report($"riff library capture card {cardLeft:0.#}..{cardRight:0.#}, name box {boxLeft:0.#}..{boxRight:0.#}, view {view.Bounds.Width:0.#}");

                Assert.True(nameBox.Bounds.Width > 1, "the name box has no width");
                Assert.True(boxRight <= view.Bounds.Width + Tolerance,
                    $"the name box ends at {boxRight:0.#}, past the {view.Bounds.Width:0.#} px pane");
                Assert.True(boxRight <= cardRight + Tolerance,
                    $"the name box ends at {boxRight:0.#}, past its card ({cardRight:0.#})");
                Capture(window, $"riff-library-narrow-{height}");
            }
            finally
            {
                model.StatusMessage = previousStatus;
                model.SelectedEntry = previousEntry;
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public Task TheStackedTracks_ScrollInsteadOfCuttingTheirHeaders(int rowCount) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new StackedTracksView();
            var host = view.FindControl<ItemsControl>("StackedItemsHost")!;
            var rowTemplate = host.ItemTemplate!;
            var rows = new List<Control>();
            // The real row markup with no entry behind it (as StackedTrackRow_MarqueeBindsToItsOwnRenderer
            // builds it): what is under test is the row's layout, not its data.
            host.ItemTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var row = Assert.IsAssignableFrom<Control>(rowTemplate.Build(null));
                row.DataContext = null;
                rows.Add(row);
                return row;
            });

            var outer = new ScrollViewer
            {
                Height = 300,
                Content = new StackPanel
                {
                    Children = { new Border { Height = 100 }, view, new Border { Height = 600 } }
                }
            };
            view.Height = 300;
            var window = NewWindow(320, 300, outer);
            window.Show();
            try
            {
                host.ItemsSource = Enumerable.Range(0, rowCount).Cast<object>().ToList();
                Settle(window);
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 },
                    $"the stacked tracks laid out as {view.Bounds}: they rendered nothing");
                Assert.Equal(rowCount, rows.Count);

                var offenders = new List<string>();
                foreach (var row in rows)
                {
                    var actions = row.GetVisualDescendants().OfType<Control>()
                        .Where(c => c.Classes.Contains("TrackAction") && c.IsEffectivelyVisible)
                        .ToList();
                    Assert.NotEmpty(actions);
                    foreach (var action in actions)
                    {
                        var (left, right) = Span(action, row);
                        Assert.True(left >= -Tolerance && right <= row.Bounds.Width + Tolerance);
                    }
                    var last = actions.MaxBy(c => TopIn(c, row) + c.Bounds.Height)!;
                    var bottom = TopIn(last, row) + last.Bounds.Height;
                    Report($"stacked row {rows.IndexOf(row)}: last action bottom {bottom:0.#} of {row.Bounds.Height:0.#}");
                    if (bottom > row.Bounds.Height + Tolerance)
                        offenders.Add($"row {rows.IndexOf(row)}: '{last.GetType().Name}' ends at {bottom:0.#} of {row.Bounds.Height:0.#}");
                }

                Assert.True(offenders.Count == 0,
                    $"{rowCount} stacked rows in a 300 px pane cut their header buttons off:\n  " +
                    string.Join("\n  ", offenders));

                var scroll = Named<ScrollViewer>(view, "StackedTracksScroll");
                Report($"stacked tracks extent {scroll.Extent.Height:0.#}, viewport {scroll.Viewport.Height:0.#}");
                Assert.Equal(rowCount > 1, scroll.Extent.Height > scroll.Viewport.Height + 1);
                var point = scroll.TranslatePoint(new Point(60, 40), window)!.Value;
                window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
                Settle(window);
                if (rowCount > 1)
                {
                    Assert.True(scroll.Offset.Y > 0, "an overflowing stacked pane did not scroll");
                    Assert.Equal(0, outer.Offset.Y);
                }
                else
                {
                    Assert.Equal(0, scroll.Offset.Y);
                    Assert.True(outer.Offset.Y > 0, "a non-overflowing pane consumed the outer scroller's wheel");
                }
                Capture(window, $"stacked-tracks-short-{rowCount}");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
