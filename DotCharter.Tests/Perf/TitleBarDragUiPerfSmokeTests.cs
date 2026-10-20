using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The shell wears a CUSTOM title bar — <c>WindowDecorations="BorderOnly"</c>
///     plus <c>ExtendClientAreaToDecorationsHint</c> — so the OS draws no caption
///     and the ONLY way to move the window is the transparent
///     <c>TitleBarDragArea</c> strip calling <c>BeginMoveDrag</c>
///     (<c>Views/Shell/WindowChromeView.axaml.cs</c>).
///     <para>
///         That makes the drag strip a piece of WINDOW CHROME any full-window
///         overlay can silently take away. v0.48.5 shipped exactly that: the chart
///         editor's Amp &amp; FX rig overlay sat in <c>WindowRootGrid</c> at
///         <c>Grid.Row="0" Grid.RowSpan="2"</c> — over <c>TitleBarRow</c> as well
///         — and the rig's own root paints the fully opaque
///         <c>FireOverlayFill</c>. Opening it put an opaque, hit-testable surface
///         on top of the drag strip, so the window stopped being movable for as
///         long as the rig was open. The owner: "in pedalboard view you can't move
///         the window around in Windows anymore — almost like it's fullscreen but
///         it isn't." Play Mode's copy of the same rig never had it; its host has
///         always been inside the content row.
///     </para>
///     <para>
///         Hit-testing is the only honest check. The overlay CONSTRUCTED fine,
///         LAID OUT fine and rendered fine — every rig smoke test stayed green
///         while the window was pinned to the desktop. What broke is which element
///         is topmost at one point. The simulated press beside it is a second
///         reading of the same rule through the real input pipeline, and it is
///         asserted only against its own CONTROL — a press taken with the rig
///         closed — because headless input has one pointer and one input root for
///         the whole session and a window an earlier class showed can still own
///         them. Measured: run alone the press arrives; run after the other ~340
///         cases it does not, with the rig open or shut. Without the control that
///         reads as the overlay eating the press, which is exactly the wrong
///         conclusion.
///     </para>
///     <para>
///         <b>One test, on purpose.</b> The four assertions are one rule walked in
///         order, and they cannot be split: headless pointer input has a single
///         position and a single input root, so a second <see cref="MainWindow" />
///         swallows every simulated press (the session hands out no
///         <see cref="Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime" />,
///         so the usual reuse branch never matches) — and a window CACHED across
///         dispatches is worse still, because <c>PerTest</c> isolation tears the
///         application down under it and hit tests then answer from the previous
///         app's stale composition tree. Both were measured while writing this:
///         each case passed alone and failed in company, for a different reason
///         each time. <c>UI/EscapeKeyRoutingUiPerfSmokeTests</c> is the same
///         lesson on the keyboard side.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class TitleBarDragUiPerfSmokeTests
{
    [Fact]
    public Task The_window_stays_movable_while_the_rig_overlay_is_open() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = App.Services?.GetRequiredService<MainWindowViewModel>();
            Assert.True(vm is not null, "the headless app container has no MainWindowViewModel");

            // Leaked shell state, reset before anything is measured. The shell view
            // model is a DI SINGLETON and `App.Services` outlives one dispatch, so
            // a dialog or the command palette that ANOTHER class in this session
            // opened is still open on the model this window binds to — and a modal
            // scrim covers the title bar in exactly the way the bug does, which
            // would read as a regression here. `UI/EscapeKeyRoutingUiPerfSmokeTests`
            // carries the same reset for the same reason.
            foreach (var dialog in vm!.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            // The chart editor, explicitly: chrome is a property of the SURFACE
            // since 0.62 and the app's DEFAULT startup screen is Play Mode,
            // which has no title bar at all. Without this the drag strip is
            // correctly absent and the case reads as a regression.
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;

            var window = new MainWindow { DataContext = vm };
            window.Show();

            // The chrome rows are WindowChromeView's (#654), in its own namescope.
            var drag = window.WindowChrome.FindControl<Border>("TitleBarDragArea");
            Assert.True(drag is not null,
                "MainWindow no longer has a `TitleBarDragArea` — the custom title bar's move-drag strip. "
                + "If it was renamed, repoint this test; if it was removed, the window cannot be moved.");

            var presses = 0;
            drag!.AddHandler(InputElement.PointerPressedEvent,
                (_, _) => presses++,
                RoutingStrategies.Bubble, handledEventsToo: true);

            // ── 1. the resting state, so the checks below are not vacuous ──────
            vm.ToneRig.IsPedalboardOpen = false;
            Realize(window);
            AssertDragRegionIsTopmost(window, drag, "with the rig CLOSED");

            // The CONTROL for step 3. Headless input has one pointer and one
            // input root per session, and this class is not the only one in it —
            // a window an earlier class showed can still own them, in which case
            // no simulated press reaches this tree at all. That is a property of
            // the harness, not of the shell, so it must not read as the overlay
            // eating the press: measure whether a press arrives HERE, with the
            // rig closed, and only compare against it.
            Press(window, drag);
            var pressReachesThisWindow = presses > 0;

            // ── 2. …and with the rig up ───────────────────────────────────────
            vm.ToneRig.IsPedalboardOpen = true;
            Realize(window);

            var rig = RigRoot(window);
            Assert.True(rig is { Bounds: { Width: > 0, Height: > 0 } },
                "the rig overlay never realized, so nothing below tests the overlaid case");

            // The rule, stated as what the RIG may do rather than as raw
            // topmost-ness: nothing in the rig's subtree may be under the drag
            // point, and the drag region must still be reachable there. Before the
            // fix the rig's own `RigHeader` was the topmost element at this exact
            // point. It is phrased this way because the shell is shared: another
            // class in this session can leave a modal up whose scrim spans the
            // window, and that would fail a bare "topmost is the drag region"
            // while saying nothing about the rig — measured, twice.
            var stack = window.GetVisualsAt(Centre(window, drag)).ToList();
            Assert.DoesNotContain(stack, v => v.GetSelfAndVisualAncestors().Contains(rig));
            Assert.True(stack.Contains(drag),
                "the title-bar drag region is not under its own centre with the rig overlay open — "
                + "something is covering the ONLY gesture that moves this window, which has no OS "
                + $"caption. [under the point, innermost first: {Stack(window, drag)}]");

            // ── 3. the same thing end to end, when the harness allows it.
            //       InputHitTest and the input pipeline can disagree — a handled
            //       tunnel event on an ancestor stops the press without changing
            //       what is topmost — so it is worth asserting separately. Skipped
            //       when a foreign layer is on top, for the reason above. ─────────
            if (pressReachesThisWindow && ReferenceEquals(TopmostAtCentreOf(window, drag), drag))
            {
                var before = presses;
                Press(window, drag);
                Assert.True(presses > before,
                    "a left press over the title-bar drag region reached it with the rig CLOSED and not "
                    + "with the rig OPEN. That press is the app's only move-window gesture: the shell asks "
                    + "for BorderOnly decorations, so nothing else on screen will move the window.");
            }

            // ── 4. and the fix must not cost the rig its OWN pointer input:
            //       the header carries the tone pill, the meters and the ✕. ─────
            var header = Descendant(rig!, "RigHeader");
            Assert.True(header is { Bounds: { Width: > 0, Height: > 0 } },
                "RigHeader is gone or never laid out — repoint this test");

            // Same phrasing as above, and for the same reason: the rig must be
            // UNDER its own header point. A foreign modal on top of everything is
            // not this rule's business; the shell being back over the rig is.
            var onHeader = window.GetVisualsAt(Centre(window, header!)).ToList();
            Assert.True(onHeader.Any(v => v.GetSelfAndVisualAncestors().Contains(rig)),
                "no part of the rig is under a point inside the rig's own header — keeping the title "
                + "bar draggable must not lift the shell back over the rig's controls. "
                + $"[under the point, innermost first: {Stack(window, header!)}]");
        }, CancellationToken.None);

    /// <summary>
    ///     The Graphite title bar lays out all three of its parts, and the
    ///     transport is centred in the WINDOW.
    ///     <para>
    ///         The centring is the assertion worth having. It is produced by two
    ///         balancing star columns either side of an Auto one, so the moment
    ///         anything in that row gains a fixed width or the column list is
    ///         edited, the transport drifts toward whichever side has more room
    ///         - and drifts silently: every element still measures, still
    ///         arranges and still renders, exactly the way a bar that is subtly
    ///         wrong always does.
    ///     </para>
    ///     <para>
    ///         Everything else here is the shape every silent XAML fault takes:
    ///         a caption whose style matched nothing arranges at zero size, and
    ///         an unresolvable icon key is a blank space with no exception.
    ///     </para>
    /// </summary>
    [Fact]
    public Task The_title_bar_lays_out_its_breadcrumb_transport_and_palette_door() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = App.Services?.GetRequiredService<MainWindowViewModel>();
            Assert.True(vm is not null, "the headless app container has no MainWindowViewModel");

            foreach (var dialog in vm!.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.ToneRig.IsPedalboardOpen = false;
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;

            var window = new MainWindow { DataContext = vm };
            window.Show();
            Realize(window);

            // The chrome rows are WindowChromeView's (#654), in its own namescope.
            var chrome = window.WindowChrome;
            var row = chrome.FindControl<Grid>("TitleBarRow");
            Assert.True(row is { Bounds: { Width: > 0, Height: > 0 } },
                "MainWindow no longer has a laid-out `TitleBarRow`");

            // No breadcrumb and no transport: the chart's name and the song
            // position are on the status rail, play/pause/stop on the toolbar.
            Assert.True(chrome.FindControl<StackPanel>("TitleBreadcrumb") is null,
                "the title bar repeats the chart's name, which the status rail already shows");
            Assert.True(chrome.FindControl<StackPanel>("TitleTransport") is null,
                "the title bar repeats the transport, which the editor's toolbar already has");

            // "Go to anything" - the palette's only door you can see.
            var palette = chrome.FindControl<Button>("TitleGoToAnything");
            Assert.True(palette is { Bounds: { Width: > 20, Height: > 10 } },
                $"the go-to-anything button laid out as {palette?.Bounds}");
            foreach (var icon in palette!.GetVisualDescendants().OfType<PathIcon>())
            {
                Assert.True(icon.Data is not null,
                    "the go-to-anything glyph has no geometry - its icon key is wrong, which renders "
                    + "as a blank space with no exception and no log line");
            }

            // The drag strip still moves the window.
            var strip = chrome.FindControl<Border>("TitleBarDragArea");
            Assert.True(strip is { Bounds: { Width: > 0, Height: > 0 } },
                "`TitleBarDragArea` is missing or laid out at zero size - the window has no OS "
                + "caption, so this strip is the only way to move it");
        }, CancellationToken.None);

    // `AFullBleedSurface_IsStillMovable` lived here and is GONE, deliberately.
    // It switched the shell to Home, and that switch runs
    // `StopTonePreviewOutsideTheEditor` — an audio path — which takes the whole
    // testhost down in this harness: the run reported ONE case of three and
    // exited non-zero, which is the "fewer than N" trap this suite is famous
    // for. The rule it asserted (a full-bleed surface keeps a drag strip,
    // because the window draws no OS caption) is a source lint now, in
    // `UI/PlayModeReachableFromEditorTests.PlayModeAndHome_AreFullBleed`.
    // **Do not restore a headless case that switches shell tabs** without
    // first making that switch inert.

    // ---- helpers --------------------------------------------------------------

    private static void AssertDragRegionIsTopmost(Window window, Border drag, string state)
    {
        Assert.True(drag.Bounds is { Width: > 0, Height: > 0 },
            $"the title-bar drag region laid out at zero size {state} — nothing is being hit-tested");

        var hit = TopmostAtCentreOf(window, drag);
        Assert.True(ReferenceEquals(hit, drag),
            $"the topmost element over the title-bar drag region {state} is `{Describe(hit)}`, not the "
            + "drag region. The shell draws no OS caption, so whatever sits there owns the ONLY gesture "
            + "that moves the window: an overlay spanning the title-bar row pins the window to the desktop."
            // The chain is what says WHICH layer did it — the failure is otherwise
            // one unnamed element and no way to tell the rig from a leaked modal.
            + $" [under the point, innermost first: {Stack(window, drag)}]");
    }

    /// <summary>
    ///     Lays out AND commits a frame. Hit testing goes through the compositor,
    ///     not the layout tree, so a window that has only been measured and
    ///     arranged answers <c>InputHitTest</c> with <c>null</c> everywhere —
    ///     measured: five visuals under the drag point after a render tick, zero
    ///     before one. Headless input takes the same path, so the press needs it
    ///     too.
    /// </summary>
    private static void Realize(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
    }

    private static IInputElement? TopmostAtCentreOf(Window window, Visual target) =>
        window.InputHitTest(Centre(window, target));

    private static void Press(Window window, Visual target)
    {
        var at = Centre(window, target);
        // The move is not decoration: headless input keeps its own pointer, and a
        // MouseDown at a position it has never been moved to does not reliably
        // resolve a source. It is also what a user does — the pointer travels to
        // the title bar before the button goes down.
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    private static Point Centre(Window window, Visual target)
    {
        var centre = target.TranslatePoint(
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
        Assert.True(centre.HasValue, "the probe target is not connected to the window");
        return centre!.Value;
    }

    /// <summary>The rig's own root, reached through the overlay panel: the view is
    /// its own namescope, so the window cannot find its children by name.</summary>
    private static Control? RigRoot(Window window)
    {
        var overlay = window.FindControl<Panel>("PedalboardOverlay");
        return overlay is null ? null : Descendant(overlay, "RigScreenRoot");
    }

    private static Control? Descendant(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    /// <summary>Everything under the probe point, innermost first — the only way a
    /// failure says WHICH layer took the pointer.</summary>
    private static string Stack(Window window, Visual target) =>
        string.Join(" < ", window.GetVisualsAt(Centre(window, target))
            .Select(v => v is Control c ? Describe(c) : v.GetType().Name));

    private static string Describe(IInputElement? hit) =>
        hit switch
        {
            null => "nothing",
            Control { Name: { Length: > 0 } n } c => $"{c.GetType().Name}#{n}",
            _ => hit.GetType().Name
        };
}
