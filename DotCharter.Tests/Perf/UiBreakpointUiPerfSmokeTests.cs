using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DotCharter.UI.Services;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The breakpoint mechanism against a REAL window and the REAL theme: the
///     class toggle, the ladder swap, and the runtime line-height values.
///     <para>
///         The policy itself is pure and lives in the default gate
///         (<c>UI/UiBreakpointTests</c>). What needs a window is everything the
///         policy cannot see — that a class really lands on the root, that
///         merging <c>Ladders.Wide.axaml</c> really re-resolves a
///         <c>DynamicResource</c> in an already-attached tree, and that the
///         base ladder comes back when the shell closes.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class UiBreakpointUiPerfSmokeTests
{
    /// <summary>
    ///     A window whose width crosses the thresholds wears the right class,
    ///     and the design band wears none. Driven through the real
    ///     <c>SizeChanged</c> wiring rather than by calling <c>Apply</c>, so a
    ///     controller that attached nothing would fail here.
    /// </summary>
    [Fact]
    public Task TheWindowRoot_WearsTheClassForItsWidth() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var window = new Window { Width = 1920, Height = 1080 };
            UiBreakpointController.Attach(window);
            window.Show();
            try
            {
                // The design band: NO class, which is what makes every existing
                // selector in the theme the default shape.
                Resize(window, 1920);
                Assert.DoesNotContain("Wide", window.Classes);
                Assert.DoesNotContain("Compact", window.Classes);

                Resize(window, 3840);
                Assert.Contains("Wide", window.Classes);
                Assert.DoesNotContain("Compact", window.Classes);

                Resize(window, 1280);
                Assert.Contains("Compact", window.Classes);
                // The previous shape's class must be gone: the two are
                // mutually exclusive and a leftover would style twice.
                Assert.DoesNotContain("Wide", window.Classes);

                Resize(window, 2000);
                Assert.DoesNotContain("Compact", window.Classes);
                Assert.DoesNotContain("Wide", window.Classes);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>The swap, seen from a control.</b> A <c>Border</c> whose
    ///     <c>MaxWidth</c> is a <c>DynamicResource</c> on a breakpointed token
    ///     must follow the ladder while it is ATTACHED — that is the whole
    ///     premise of "no per-view edits", and a merged dictionary that did not
    ///     propagate would leave every consumer silently on the base value.
    /// </summary>
    [Fact]
    public Task TheWideLadder_RetargetsAnAttachedControl() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var probe = new Border { HorizontalAlignment = HorizontalAlignment.Center };
            probe[!Layoutable.MaxWidthProperty] =
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FireColumnMaxWidthLg");

            var window = new Window { Width = 2000, Height = 1000, Content = probe };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal(1040, probe.MaxWidth);

                Assert.True(UiBreakpointController.Apply(window, UiBreakpoint.Wide),
                    "applying a new shape reported no change");
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                Assert.Equal(1400, probe.MaxWidth);

                // …and every other breakpointed constant moved with it.
                Assert.Equal(320d, Token(window, "FireSidebarWidth"));
                Assert.Equal(520d, Token(window, "FireContextRailWidth"));
                Assert.Equal(720d, Token(window, "FireHeroMinWidth"));
                Assert.Equal(520d, Token(window, "FirePopoverMaxWidth"));
                Assert.Equal(760d, Token(window, "FireColumnMaxWidth"));

                // The TYPE ladder must NOT have moved: that is the transform's
                // job, and ramping it here would compound with RootScaler.
                Assert.Equal(58d, Token(window, "FireSizeHero"));
                Assert.Equal(34d, Token(window, "LineHeightHero"));
                Assert.Equal(480d, Token(window, "FireDialogWidthSm"));
                Assert.Equal(1120d, Token(window, "FireDialogWidthXl"));

                Assert.True(UiBreakpointController.Apply(window, UiBreakpoint.Default));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                Assert.Equal(1040, probe.MaxWidth);
            }
            finally
            {
                UiBreakpointController.Apply(window, UiBreakpoint.Default);
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     Closing the shell must put the base ladder back. The dictionary is
    ///     merged at APPLICATION scope (a popup is its own top level and has to
    ///     see it), so a shell that left it behind would hand the wide tokens
    ///     to whatever opened next in the same process.
    /// </summary>
    [Fact]
    public Task ClosingTheShell_RestoresTheBaseLadder() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var window = new Window { Width = 3840, Height = 2160 };
            UiBreakpointController.Attach(window);
            window.Show();
            Resize(window, 3840);
            Assert.Contains("Wide", window.Classes);
            Assert.Equal(1400d, Token(window, "FireColumnMaxWidthLg"));

            window.Close();

            var after = new Window { Width = 1920, Height = 1080 };
            after.Show();
            try { Assert.Equal(1040d, Token(after, "FireColumnMaxWidthLg")); }
            finally { after.Close(); }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>The line-height tokenisation, proved at RUNTIME.</b>
    ///     <c>UI/LineHeightTokenTests</c> proves the SOURCE still resolves to
    ///     the same 33 numbers; this proves the theme actually hands them to a
    ///     TextBlock and that the text still measures the same box. The expected
    ///     values are the literals these styles carried before 2026-09-12 — one
    ///     line of diff away from the old markup.
    ///     <para>
    ///         One case over six classes rather than a <c>[Theory]</c>, and that
    ///         is deliberate: each <c>Dispatch</c> boots and tears down an
    ///         ISOLATED application (<c>AvaloniaTestIsolationLevel.PerTest</c>),
    ///         and six back-to-back boots intermittently raced Avalonia's own
    ///         <c>ServerCompositor</c> construction onto a thread that did not
    ///         own the render loop ("The calling thread cannot access this
    ///         object because a different thread owns it"). Six line boxes are
    ///         not six different questions; one window answers all of them.
    ///     </para>
    /// </summary>
    [Fact]
    public Task EveryTokenisedTypeStep_ResolvesToItsOldLiteral() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            // The six app-scope type classes and the literal each carried.
            (string Class, double FontSize, double LineHeight)[] steps =
            [
                ("Display", 28, 34),
                ("Title", 20, 26),
                ("Subtitle", 14, 20),
                ("Body", 13, 18),
                ("Caption", 11, 14),
                ("Mono", 12, 16)
            ];

            var stack = new StackPanel();
            var window = new Window { Width = 800, Height = 900, Content = stack };
            window.Show();
            try
            {
                foreach (var (styleClass, _, _) in steps)
                {
                    // Top-aligned inside a vertical StackPanel: a TextBlock used
                    // as a Window's Content STRETCHES, and a stretched height
                    // measures the window rather than the line box.
                    var text = new TextBlock
                    {
                        Text = "Hjgpqy",
                        VerticalAlignment = VerticalAlignment.Top,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    text.Classes.Add(styleClass);
                    text.Themed(); // the type classes are ControlThemes since #240
                    stack.Children.Add(text);
                }

                window.UpdateLayout();

                for (var i = 0; i < steps.Length; i++)
                {
                    var (styleClass, fontSize, lineHeight) = steps[i];
                    var text = (TextBlock)stack.Children[i];

                    Console.WriteLine(
                        $"[line-height] .{styleClass} font={text.FontSize} lineHeight={text.LineHeight} " +
                        $"measured={text.Bounds.Height} desired={text.DesiredSize.Height}");

                    Assert.Equal(fontSize, text.FontSize);
                    Assert.Equal(lineHeight, text.LineHeight);
                    // A single line's arranged height IS the line box.
                    Assert.Equal(lineHeight, text.Bounds.Height, 1);
                    Assert.Equal(lineHeight, text.DesiredSize.Height, 1);
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    /// <summary>The value a control at the given window would see for a token.
    /// Resolved through the real lookup chain (window -> application), not by
    /// reading the dictionary we merged — that would only prove we merged it.
    /// </summary>
    private static double Token(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, out var value), $"token {key} does not resolve");
        return Assert.IsType<double>(value);
    }

    /// <summary>Delivers a width the way a user's drag does, so the controller's
    /// own <c>SizeChanged</c> subscription is what runs.</summary>
    private static void Resize(Window window, double width)
    {
        window.Width = width;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        window.UpdateLayout();
    }
}
