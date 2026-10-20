using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.UI.Platform;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <see cref="ToolTipHoverPolicy" /> against the REAL editor chrome — the
///     rails the owner sweeps. <c>UI/ToolTipHoverPolicyTests</c> proves the
///     guard works on a control built in a test; this proves it reaches markup,
///     which is the half that can silently stop working (the guard subscribes
///     once, so a view constructed before <c>App.Initialize</c> would be missed
///     entirely and nothing else would notice).
/// </summary>
/// <remarks>
///     <para>
///         <b>Measured here, asserted structurally.</b> Headless, Debug,
///         <c>r2r=no</c>. One tooltip open/close cycle on a real toolbar button:
///         <b>4.0-8.7 ms per open</b> (p95 5.6-15.5, worst 34-110) and
///         <b>391 KB allocated per cycle</b> — and that is on the CHEAP
///         <c>OverlayPopupHost</c> the headless platform substitutes. The app
///         runs with <c>OverlayPopups</c> off (the airspace rule in
///         <c>DotCharter.UI/AGENTS.md</c>), so its host is a <c>PopupRoot</c>:
///         a native HWND with its own ANGLE/D3D render target.
///     </para>
///     <para>
///         <b>What headless CANNOT show, and why there is no sweep test here.</b>
///         The headless windowing platform has no popup impl, so every popup —
///         tooltips included — falls back to the in-window
///         <c>OverlayPopupHost</c>. Avalonia's <c>ToolTipService.Update</c> then
///         early-returns on every pointer move ("don't update while the pointer
///         is over a tooltip", and with an overlay host the tooltip's visual
///         root IS the window's), so the chained-open storm cannot be
///         reproduced headlessly at all. Driving 180 real
///         <c>window.MouseMove</c>s over the toolbar yields 2 opens headless
///         where the windowed path would yield one per tipped control crossed.
///         What IS assertable — and is what the fix consists of — is that
///         Avalonia's <c>betweenShowDelay &gt;= 0</c> branch can never be taken
///         on these rails.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class ToolTipChurnUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 900;

    /// <summary>
    ///     Every tipped control in the editor chrome is opted out of the instant
    ///     chained open. One un-guarded control is enough to re-arm the storm:
    ///     the delay is read from whichever control the pointer moved ONTO.
    /// </summary>
    [Theory]
    [InlineData("toolbar")]
    [InlineData("activity rail")]
    [InlineData("status rail")]
    public Task EveryTippedControlInTheEditorChrome_CannotOpenAChainedTooltipInstantly(string rail) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (view, window) = Show(rail);
            try
            {
                var tipped = TippedControlsIn(view);
                Assert.True(tipped.Count > 0, $"the {rail} has no tooltips at all — this test found nothing to check");

                var unguarded = tipped
                    .Where(c => ToolTipHoverPolicy.AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(c)))
                    .Select(c => $"{c.GetType().Name}({ToolTip.GetTip(c)})")
                    .ToList();

                Assert.True(unguarded.Count == 0,
                    $"{unguarded.Count} of {tipped.Count} tipped controls on the {rail} still let Avalonia " +
                    $"re-open a tooltip with NO delay while the pointer sweeps: {string.Join(", ", unguarded.Take(5))}");

                Debug.WriteLine($"[tooltip-churn] {rail}: {tipped.Count} tipped controls, all guarded");
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The premise, on the real markup: a tooltip open is expensive and its
    ///     popup host does not survive the close, so N chained opens cost N
    ///     host builds. If Avalonia ever starts caching the host this guard
    ///     becomes unnecessary rather than wrong — and we want to be told.
    ///     Timings are LOGGED, never asserted.
    /// </summary>
    [Fact]
    public Task AToolTipOpen_RebuildsItsPopupHostAndIsWorthAvoiding() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (view, window) = Show("toolbar");
            try
            {
                var target = TippedControlsIn(view).First();

                ToolTip.SetIsOpen(target, true);
                var whileOpen = PopupHostOf(target);
                ToolTip.SetIsOpen(target, false);
                var afterClose = PopupHostOf(target);
                ToolTip.SetIsOpen(target, true);
                var secondOpen = PopupHostOf(target);
                ToolTip.SetIsOpen(target, false);

                Assert.NotNull(whileOpen);
                Assert.Null(afterClose);
                Assert.False(ReferenceEquals(whileOpen, secondOpen),
                    "a tooltip's popup host is now REUSED across opens — the churn this policy avoids is gone, " +
                    "and ToolTipHoverPolicy should be re-justified or removed");

                // Warm, then measure. Not asserted: a wall-clock budget flakes
                // the moment suites run in parallel.
                for (var i = 0; i < 20; i++)
                {
                    ToolTip.SetIsOpen(target, true);
                    ToolTip.SetIsOpen(target, false);
                }

                const int cycles = 100;
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < cycles; i++)
                {
                    ToolTip.SetIsOpen(target, true);
                    ToolTip.SetIsOpen(target, false);
                }

                var ms = sw.Elapsed.TotalMilliseconds / cycles;
                var kb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / cycles;
                Debug.WriteLine($"[tooltip-churn] one open+close cycle on {target.GetType().Name}: " +
                                $"{ms:0.00} ms, {kb:0} KB (headless OverlayPopupHost, r2r=no)");
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (Control View, Window Window) Show(string rail)
    {
        Control view = rail switch
        {
            "toolbar" => new ToolbarView(),
            // The menu bar was this case until 0.62. Its own tooltips live on
            // MenuItems inside CLOSED popups, so the only ones a layout pass
            // ever reached were the nav switcher's — and the switcher is the
            // activity rail now, which is where the chrome's tipped controls
            // went with it.
            "activity rail" => new ActivityRailView(),
            "status rail" => new StatusBar(),
            _ => throw new ArgumentOutOfRangeException(nameof(rail), rail, "unknown rail")
        };
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        view.Measure(new Size(W, H));
        view.Arrange(new Rect(0, 0, W, H));
        window.UpdateLayout();
        return (view, window);
    }

    private static List<Control> TippedControlsIn(Control view) =>
        view.GetVisualDescendants().OfType<Control>()
            .Concat([view])
            .Where(c => ToolTip.GetTip(c) != null)
            .ToList();

    /// <summary>
    ///     The live popup host behind an open tooltip. Avalonia keeps it on the
    ///     internal <c>ToolTip.ToolTipProperty</c> and exposes it through
    ///     <c>Avalonia.Controls.Diagnostics.IPopupHostProvider</c>, which is not
    ///     public in 12.1.1 — hence reflection, and hence this being the only
    ///     place that does it.
    /// </summary>
    private static object? PopupHostOf(Control adorned)
    {
        var tipProperty = (AvaloniaProperty)typeof(ToolTip)
            .GetField("ToolTipProperty",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)!
            .GetValue(null)!;

        var tip = adorned.GetValue(tipProperty);
        var popup = tip?.GetType()
            .GetField("_popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(tip);
        return popup?.GetType()
            .GetProperty("Host",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(popup);
    }
}
