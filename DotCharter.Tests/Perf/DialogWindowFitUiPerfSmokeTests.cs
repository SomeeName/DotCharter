using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #210: a hosted dialog keeps its TITLE and its FOOTER inside its card, with the body between
///     them, in a window as small as 1280×720 — the smallest the app supports.
///     <para>
///         The Tone designer did not. Its card is a <c>DockPanel</c> (head docked top, footer docked
///         bottom) whose last child, the three-column body, was a fixed <c>Height="560"</c>. The
///         dialog grammar clamps the card to the window (<c>DialogCardClampConverter</c>), so at
///         1280×720 the body was handed ~430 px; a fixed-height last child in a DockPanel does not
///         shrink, it CENTRES in the slot it is given and spills over both neighbours — "Project
///         tones" printed over the subtitle and the slot column painted over Done. Every
///         measurement here would have passed on its own (each part arranged where its panel put
///         it); only the RELATION of body to head and footer, and a hit test on Done, see it.
///     </para>
///     <para>
///         The dialog is opened the way the app opens it — a real <see cref="DialogHost" /> inside
///         the real <see cref="DialogContainerView" /> — in a window whose bottom
///         <c>FireStatusBarHeight</c> is the editor's status rail, because the dialog container
///         sits above that row in <c>MainWindow.axaml</c>. Text wraps differently in de-DE and
///         ja-JP, so this class is in the release's culture re-run (<c>$HeadlessLayoutFilter</c>).
///     </para>
///     <para>
///         #725: the body's own bounds are not enough. A body <c>StackPanel</c> handed less height
///         than it asked for keeps the bounds it was given and paints its children on, under the
///         footer — Batch lint, Showlights and Guitar Pro import in <c>Full</c>, the Tuner and
///         Songsterr import did, and every bounds check here passed. So every unclipped visual
///         in the body is graded by the rectangle it PAINTS (<see cref="PaintedUnclipped" />).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class DialogWindowFitUiPerfSmokeTests
{
    /// <summary>The issue's window, a 16:10 laptop and the owner's screen.</summary>
    private static readonly (double W, double H)[] Windows = [(1280, 720), (1280, 800), (1920, 1080)];

    /// <summary>The Tone designer's body asks for this much when the window has the room
    /// (<c>ToneDesignerDialogView.axaml</c>, <c>ToneDesignerBody</c>'s <c>PreferredHeight</c>).</summary>
    private const double ToneBodyHeight = 560;

    /// <summary>A pixel of rounding is not an overlap.</summary>
    private const double Tolerance = 0.5;

    /// <summary>
    ///     Every variant of the Tone designer at every window: the title and Done are inside the
    ///     card and the window, the body sits between the head and the footer, Done is the top-most
    ///     thing under its own centre — and on the owner's 1920×1080 the body is still the full
    ///     560 px it was before, so the look did not change where it already fitted.
    /// </summary>
    [Fact]
    public async Task TheToneDesigner_KeepsItsTitleAndDone_ClearOfItsBody_FromA1280x720WindowUp()
    {
        var offenders = new List<string>();
        var log = new List<string>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = ScreenCatalog.Default.Entries.Single(e => e.Contract == typeof(IToneDesignerScreen));
            var variants = FakeScreens.VariantsOf(entry.Contract).Select(v => v.Name).ToList();
            Assert.Contains(FakeVariantNames.Full, variants);
            foreach (var (w, h, variant) in from size in Windows from v in variants select (size.W, size.H, v))
            {
                WithDialogInWindow(entry, variant, w, h, (view, root) =>
                {
                    var fit = Measure(entry, variant, w, h, view, root, offenders);
                    var body = view.GetLogicalDescendants().OfType<Control>().SingleOrDefault(c => c.Name == "ToneDesignerBody");
                    Assert.True(body is not null, "the Tone designer has no ToneDesignerBody — this case would grade a body that moved");
                    Assert.Contains(body, fit.Body);
                    log.Add($"[tone designer] {variant,-8} {w:0}x{h:0}: card {fit.Card.Height:0} px, body {body!.Bounds.Height:0} px");
                    if (h >= 1080 && Math.Abs(body.Bounds.Height - ToneBodyHeight) > Tolerance)
                    {
                        offenders.Add($"{variant} {w:0}x{h:0}: the body is {body.Bounds.Height:0.#} px tall, not the " +
                                      $"{ToneBodyHeight:0} px it had before #210 — a window with the room must keep the look");
                    }

                    Capture(root, $"tone-designer-{variant}-{w:0}x{h:0}.png");
                });
            }
        }, CancellationToken.None);

        foreach (var line in log) Console.WriteLine(line);
        Assert.True(offenders.Count == 0, "The Tone designer does not fit its window (#210):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The same three relations for every hosted dialog in every fake variant at 1280×720 — the
    ///     issue asked for the other cards to be checked for the same fixed-height trap, and this is
    ///     the check that keeps the next one from shipping.
    /// </summary>
    [Fact]
    public async Task EveryHostedDialog_KeepsItsTitleAndFooter_ClearOfItsBody_InA1280x720Window()
    {
        var offenders = new List<string>();
        var graded = 0;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var hosted = ScreenCatalog.Default.Entries
                .Where(e => e.Kind == ScreenKind.Dialog && e.ViewType.Namespace == typeof(SetBpmDialogView).Namespace)
                .ToList();
            Assert.True(hosted.Count >= 29, $"only {hosted.Count} hosted dialogs in the catalog");
            foreach (var (entry, variant) in from e in hosted from v in FakeScreens.VariantsOf(e.Contract) select (e, v.Name))
            {
                WithDialogInWindow(entry, variant, 1280, 720, (view, root) =>
                {
                    _ = Measure(entry, variant, 1280, 720, view, root, offenders);
                    graded++;
                    Capture(root, $"{entry.ViewType.Name}-{variant}-1280x720.png");
                });
            }
        }, CancellationToken.None);

        Assert.True(graded >= 60, $"only {graded} dialog variants were graded");
        Assert.True(offenders.Count == 0, "A hosted dialog does not fit a 1280x720 window:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The painted-bounds reading the dialogs are graded by, on a fixture where the answer is
    ///     known: a 100 px StackPanel holding 160 px of children paints to y 160 and is seen, and a
    ///     100 px Viewbox (not clipping) scaling a 1000 px child paints to y 100 and is not — though
    ///     the child's LAYOUT height is 1000, which is what made a translated origin cry wolf (#725).
    /// </summary>
    [Fact]
    public async Task ThePaintedBoundsReading_SeesAStackSpillingPastItsSlot_AndReadsAViewboxAtItsDrawnScale()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var stack = new StackPanel { Height = 100, VerticalAlignment = VerticalAlignment.Top };
            stack.Children.Add(new Border { Height = 80 });
            stack.Children.Add(new Border { Height = 80 });
            var scaled = new Border { Width = 1000, Height = 1000 };
            var viewbox = new Viewbox { Height = 100, ClipToBounds = false, Child = scaled, VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            root.Children.Add(stack);
            Grid.SetColumn(viewbox, 1);
            root.Children.Add(viewbox);
            var window = new Window { Width = 400, Height = 400, Content = root };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var stackBottom = PaintedUnclipped(stack, root).Max(p => p.Rect.Bottom);
                Assert.True(Math.Abs(stackBottom - 160) <= Tolerance, $"the overflowing stack reads as ending at {stackBottom:0.#}, not 160");
                var viewboxBottom = PaintedUnclipped(viewbox, root).Max(p => p.Rect.Bottom);
                Assert.True(viewboxBottom <= 100 + Tolerance, $"the Viewbox's scaled child reads as ending at {viewboxBottom:0.#}, past the 100 px it paints");
                Assert.True(scaled.Bounds.Height > 100, $"the fixture's child laid out {scaled.Bounds.Height:0.#} px tall — it no longer tells the two readings apart");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed record Fit(Rect Card, IReadOnlyList<Control> Body);

    /// <summary>Asserts the relations and returns the card's rectangle and the body's controls.</summary>
    private static Fit Measure(ScreenEntry entry, string variant, double w, double h, Control view, Control root, List<string> offenders)
    {
        var where = $"{entry.ViewType.Name} {variant} {w:0}x{h:0}";
        var card = view.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogCard"));
        var title = view.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("DialogTitle") && t.IsEffectivelyVisible);
        var head = title.GetLogicalAncestors().OfType<Control>().First(c => c.Classes.Contains("DialogHead"));
        var footer = view.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogFooter") && b.IsEffectivelyVisible);
        Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{where}: the card laid out as {card.Bounds}");
        Assert.True(title.Bounds is { Width: > 1, Height: > 1 }, $"{where}: the title laid out as {title.Bounds}");
        Assert.True(footer.Bounds is { Width: > 1, Height: > 1 }, $"{where}: the footer laid out as {footer.Bounds}");

        var cardRect = RectIn(card, root);
        var window = new Rect(0, 0, w, h);
        Inside(where, "the card", cardRect, window, "the window", offenders);
        Inside(where, "the title", RectIn(title, root), cardRect, "the card", offenders);
        Inside(where, "the footer", RectIn(footer, root), cardRect, "the card", offenders);

        // The body is every other visible child of the panel that holds the footer: whatever the
        // card stacks between its head and its footer must stay between them.
        var panel = Assert.IsAssignableFrom<Panel>(footer.Parent);
        var headInPanel = head.GetVisualAncestors().Prepend(head).OfType<Control>().FirstOrDefault(c => c.Parent == panel);
        var body = panel.Children.Where(c => c != footer && c != headInPanel && c.IsVisible).ToList();
        Assert.True(body.Count > 0, $"{where}: the card holds nothing but its head and footer");
        var headBottom = RectIn(headInPanel ?? head, root).Bottom;
        var footerTop = RectIn(footer, root).Top;
        foreach (var part in body)
        {
            var rect = RectIn(part, root);
            if (rect.Top < headBottom - Tolerance || rect.Bottom > footerTop + Tolerance)
            {
                offenders.Add($"{where}: the body ({part.GetType().Name} {part.Name}) spans y {rect.Top:0.#}..{rect.Bottom:0.#} " +
                              $"but the head ends at {headBottom:0.#} and the footer starts at {footerTop:0.#} — it paints over them");
            }

            // #725: a part keeps the bounds its panel arranged it to, but a StackPanel handed
            // less height than it asked for lays its children out at their full height and
            // paints them on, under the footer. Everything the part PAINTS must end above it.
            var spill = PaintedUnclipped(part, root).FirstOrDefault(p => p.Rect.Bottom > footerTop + Tolerance);
            if (spill.Visual is not null)
            {
                offenders.Add($"{where}: {spill.Visual.GetType().Name} {spill.Visual.Name} in the body paints y " +
                              $"{spill.Rect.Top:0.#}..{spill.Rect.Bottom:0.#}, under the footer at {footerTop:0.#} — the body must scroll or reflow");
            }
        }

        // Every footer action is inside the card and the window, and none is covered: the top-most
        // visual under its centre is the button or something inside it.
        foreach (var button in footer.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            var rect = RectIn(button, root);
            var label = button.Content as string ?? button.Name ?? button.GetType().Name;
            Inside(where, $"the footer action '{label}'", rect, cardRect, "the card", offenders);
            Inside(where, $"the footer action '{label}'", rect, window, "the window", offenders);
            var hit = root.InputHitTest(rect.Center, enabledElementsOnly: false) as Visual;
            if (hit is null || (hit != button && !button.IsVisualAncestorOf(hit)))
            {
                offenders.Add($"{where}: the footer action '{label}' is covered — the top-most visual at its centre " +
                              $"{rect.Center} is {hit?.GetType().Name ?? "nothing"} {(hit as Control)?.Name}");
            }
        }

        return new Fit(cardRect, body);
    }

    private static void Inside(string where, string what, Rect rect, Rect outer, string outerName, List<string> offenders)
    {
        if (rect.Left < outer.Left - Tolerance || rect.Top < outer.Top - Tolerance ||
            rect.Right > outer.Right + Tolerance || rect.Bottom > outer.Bottom + Tolerance)
        {
            offenders.Add($"{where}: {what} spans {Format(rect)}, outside {outerName} {Format(outer)}");
        }
    }

    private static string Format(Rect r) => $"x {r.Left:0.#}..{r.Right:0.#} y {r.Top:0.#}..{r.Bottom:0.#}";

    /// <summary>
    ///     Every visible visual under <paramref name="part" /> that nothing clips, with the rectangle
    ///     it PAINTS in <paramref name="root" />'s coordinates: its bounds carried through every
    ///     transform above it (<c>TransformToVisual</c>), so a <c>Viewbox</c>'s child
    ///     is read at the scale it is drawn at, not at the design size its layout keeps (#725 — the
    ///     translated-origin reading flagged every scaled glyph as spilling). A visual that clips
    ///     to its bounds (a <c>ScrollViewer</c>'s presenter) is read, its descendants are not.
    /// </summary>
    internal static IEnumerable<(Control Visual, Rect Rect)> PaintedUnclipped(Control part, Visual root)
    {
        foreach (var child in part.GetVisualChildren().OfType<Control>().Where(c => c.IsVisible))
        {
            if (child.Bounds is { Width: > 0, Height: > 0 } && child.TransformToVisual(root) is { } toRoot)
                yield return (child, new Rect(child.Bounds.Size).TransformToAABB(toRoot));
            if (child.ClipToBounds || child.Clip is not null) continue;
            foreach (var inner in PaintedUnclipped(child, root)) yield return inner;
        }
    }

    private static Rect RectIn(Visual visual, Visual root)
    {
        var origin = visual.TranslatePoint(default, root);
        Assert.True(origin is not null, $"{visual.GetType().Name} is not under the window's root");
        return new Rect(origin!.Value, visual.Bounds.Size);
    }

    /// <summary>
    ///     Opens <paramref name="entry" />'s <paramref name="variant" /> on a real dialog host in a
    ///     <paramref name="width" />×<paramref name="height" /> window whose last
    ///     <c>FireStatusBarHeight</c> is the status rail, lays it out and hands over the open view
    ///     and the window's root (window coordinates).
    /// </summary>
    private static void WithDialogInWindow(ScreenEntry entry, string variant, double width, double height, Action<Control, Grid> assert)
    {
        var fake = FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == variant);
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(entry.Contract, _ => fake.Create());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());

        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var statusRail = new Border
        {
            Height = Assert.IsType<double>(Application.Current!.FindResource("FireStatusBarHeight")),
            Background = Application.Current!.FindResource("AppBackground") as IBrush
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(container);
        Grid.SetRow(statusRail, 1);
        root.Children.Add(statusRail);
        var window = new Window { Width = width, Height = height, Content = root };
        window.Show();
        try
        {
            Open(host, entry);
            Dispatcher.UIThread.RunJobs();
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            // Hit testing reads the last RENDERED scene, not the layout just run: commit a frame,
            // or the covered-footer check asks a scene that may not hold the dialog yet.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Assert.True(statusRail.Bounds.Height > 1, $"the status rail stand-in is {statusRail.Bounds.Height} px — FireStatusBarHeight did not resolve");
            assert(Assert.Single(host.OpenViews), root);
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }

    /// <summary><c>ShowAsync&lt;TContract, TResult&gt;</c> for a catalog entry, whose result type the contract names.</summary>
    private static void Open(IDialogs host, ScreenEntry entry)
    {
        var result = entry.Contract.GetInterfaces().Append(entry.Contract)
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDialogScreen<>))
            .GetGenericArguments()[0];
        var show = typeof(IDialogs).GetMethod(nameof(IDialogs.ShowAsync))!.MakeGenericMethod(entry.Contract, result);
        _ = show.Invoke(host, [null]);
    }

    private static void Capture(Visual root, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir) || TopLevel.GetTopLevel(root) is not Window window) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, Path.ChangeExtension(file, $"{HeadlessSessionFixture.SweptCulture}.png")),
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
