using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Tests.Perf;
using DotCharter.UI;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Application = Avalonia.Application;

namespace DotCharter.Tests.UI;

/// <summary>
///     Regression tests for the 0.12.5 "Esc does nothing" class of bugs. The
///     root cause was twofold: (1) Esc was only handled by a routed handler
///     that required keyboard focus inside the dialog overlay, and (2) Play
///     Mode screen switches rip the focused control out of the tree, leaving
///     Avalonia with NO focused element — at which point ALL key input is
///     dropped. These tests drive REAL headless keyboard events through the
///     window and assert the navigation happens, including after deliberate
///     focus loss.
/// </summary>
// NOTE: the class name deliberately contains "UiPerfSmokeTests" so the
// standard gate filter (FullyQualifiedName!~UiPerfSmokeTests) excludes it:
// tests that boot the full app inside the headless session pass reliably in
// their own process, but Avalonia.Headless's dispatcher is process-global and
// dies when another collection claims it (same reason the perf smoke tests are
// excluded — a PROCESS split, not a hang). Run explicitly:
//   dotnet test --filter "FullyQualifiedName~EscapeKeyRouting"
[Collection("HeadlessUI")]
public sealed class EscapeKeyRoutingUiPerfSmokeTests
{

    /// <summary>
    ///     The ONE <see cref="MainWindow" /> every case in this class drives.
    ///     <para>
    ///         <b>It has to be cached here, and that is the whole bug these
    ///         three navigation cases had.</b> The headless session has no
    ///         <see cref="IClassicDesktopStyleApplicationLifetime" /> at all
    ///         (<c>Application.Current.ApplicationLifetime</c> is null), so the
    ///         reuse branch below never matched and every case built and showed
    ///         its OWN window. Keyboard focus stayed anchored on the FIRST
    ///         window's <c>FocusSink</c> — <c>AnchorFocusIfLost</c> correctly
    ///         declines to steal focus that something already holds — and
    ///         Avalonia routes a key press to the FOCUSED element, so
    ///         <c>KeyPressQwerty</c> on window two, three and four delivered the
    ///         event into window one's tree. The route never touched the window
    ///         under test: zero handlers fired, the screen did not change, and
    ///         it read exactly like "Esc is broken in Play Mode".
    ///     </para>
    ///     <para>
    ///         It is not: <c>HandleEscapeKey</c> navigates correctly every time
    ///         it is called, and the window tunnel claims the key correctly once
    ///         the key actually arrives. Only the FIRST key-driven case in the
    ///         class ever passed, which is why the failure looked like leaked
    ///         state from an earlier test and the class grew a
    ///         <see cref="Boot" /> reset that could not fix it.
    ///     </para>
    /// <para>
    ///     It lives for ONE dispatch now: <c>HeadlessSessionFixture.Dispatch</c>
    ///     closes every window a dispatch leaves open (an abandoned window kept
    ///     the whole tree alive and in every later layout pass, which is what
    ///     grew the headless suite without bound). What the cases need is not one
    ///     window per CLASS but one LIVE window at a time, and that is what
    ///     <see cref="AssertKeyboardWillReach" /> checks.
    /// </para>
    /// </summary>
    private static MainWindow? _sharedWindow;

    /// <summary>How many are open right now — the thing
    /// <see cref="AssertKeyboardWillReach" /> checks.</summary>
    private static int _windowsBuilt;

    private static MainWindow EnsureMainWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow is MainWindow existing)
        {
            if (!existing.IsVisible) existing.Show();
            return existing;
        }

        if (_sharedWindow != null)
        {
            if (!_sharedWindow.IsVisible) _sharedWindow.Show();
            return _sharedWindow;
        }

        var win = new MainWindow
        {
            DataContext = App.Services?.GetRequiredService<MainWindowViewModel>()
        };
        win.Closed += (_, _) =>
        {
            _windowsBuilt--;
            if (ReferenceEquals(_sharedWindow, win)) _sharedWindow = null;
        };
        win.Show();
        _sharedWindow = win;
        _windowsBuilt++;
        return win;
    }

    /// <summary>
    ///     Boots onto a KNOWN shell state.
    ///     <para>
    ///         Every test in this class shares ONE <see cref="MainWindow" /> — the
    ///         headless session has a single application lifetime — so anything a
    ///         previous test left open leaks into the next one. That matters more
    ///         here than anywhere else, because Esc is a PRIORITY CHAIN: an open
    ///         dialog or the pedalboard overlay legitimately claims the key before
    ///         navigation ever sees it, so a leftover surface makes a navigation
    ///         test fail while the router is behaving exactly as designed.
    ///     </para>
    ///     <para>
    ///         One test already carried this reset inline (crash recovery can
    ///         auto-open a dialog at startup); doing it centrally is why it is
    ///         here. <b>It was NOT what made the three navigation cases fail</b>
    ///         — that was the second window, see <see cref="_sharedWindow" />.
    ///         The note that used to stand here said they "passed when run
    ///         alone", and that was never true either: the class run alone
    ///         failed 3 of 6 just the same. Only a single-case filter passed,
    ///         because then the window under test was the only one that ever
    ///         existed.
    ///     </para>
    /// </summary>
    private static (MainWindow Win, MainWindowViewModel Vm) Boot()
    {
        var win = EnsureMainWindow();
        var vm = (MainWindowViewModel)win.DataContext!;

        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen)
                dialog.RequestClose();
        }
        // …and the dialogs on the dialog host (bounded: a dialog may refuse).
        for (var i = 0; i < 8 && vm.Dialogs.Hosted.HasOpenDialog; i++)
            vm.Dialogs.Hosted.CloseTop();

        if (vm.ToneRig.IsPedalboardOpen) vm.ToneRig.IsPedalboardOpen = false;

        win.UpdateLayout();
        Settle();
        return (win, vm);
    }

    /// <summary>Drain dispatcher posts (the production focus re-anchors are
    /// posted, not synchronous) so the window holds focus before key input.</summary>
    private static void Settle() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>
    ///     Everything <c>HandleEscapeKey</c> consults before it reaches
    ///     back-navigation, plus whether a focused element exists at all.
    ///     <para>
    ///         "Expected Menu, actual SongSelect" is true of FIVE different
    ///         faults — a leaked dialog, an open overlay, a claimed search box,
    ///         a lost focus anchor, or the router itself — and this class
    ///         already lost a diagnosis to that ambiguity once. Every failure
    ///         message here carries this line.
    ///     </para>
    /// </summary>
    private static string ShellState(MainWindow win, MainWindowViewModel vm)
    {
        var dialogs = string.Join(",", vm.Dialogs.AllDialogs.Where(d => d.IsOpen).Select(d => d.GetType().Name));
        return $"screen={vm.PlayMode.CurrentScreen} playModeVisible={vm.IsPlayModeVisible} " +
               $"openDialogs=[{dialogs}] hostedDialogOpen={vm.Dialogs.Hosted.HasOpenDialog} editorPedalboard={vm.ToneRig.IsPedalboardOpen} " +
               $"{FocusDescription(win)} windowActive={win.IsActive}";
    }

    /// <summary>Whether the focused element is in <paramref name="win" />'s
    /// tree. Avalonia 12 hosts a <c>Window</c> inside a <c>TopLevelHost</c>, so
    /// the topmost ancestor is NOT the window — membership is the question, not
    /// identity of the root.</summary>
    private static bool FocusIsInside(MainWindow win)
    {
        if (win.FocusManager?.GetFocusedElement() is not Avalonia.Visual focused) return false;
        return ReferenceEquals(focused, win) ||
               Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(focused).Any(a => ReferenceEquals(a, win));
    }

    private static string FocusDescription(MainWindow win)
    {
        var focused = win.FocusManager?.GetFocusedElement();
        return $"focused={focused?.GetType().Name ?? "<none>"}" +
               $"/{(focused as Avalonia.Controls.Control)?.Name ?? "<unnamed>"} " +
               $"focusInsideWindowUnderTest={FocusIsInside(win)}";
    }

    /// <summary>
    ///     The precondition every key-driven case here depends on: this session
    ///     built exactly ONE <see cref="MainWindow" />, and focus is inside it.
    ///     <para>
    ///         A second top level is not a cosmetic duplicate — keyboard input
    ///         has ONE globally focused element, so once a second window exists
    ///         a <c>KeyPressQwerty</c> aimed at either of them can be routed
    ///         into the other's tree and simply vanish: no handler runs, no
    ///         state changes, and the assertion afterwards reports "Esc did not
    ///         navigate" as though the router were broken. That is exactly how
    ///         these three cases were misread for months.
    ///     </para>
    ///     <para>
    ///         <b>The window count is the assertion that actually catches it.</b>
    ///         Focus-containment alone does NOT: with four windows built,
    ///         <c>FocusManager.GetFocusedElement()</c> still reports a
    ///         <c>FocusSink</c> inside the window under test while the press
    ///         goes nowhere — measured, which is why that check is only carried
    ///         along in the message.
    ///     </para>
    /// </summary>
    private static void AssertKeyboardWillReach(MainWindow win)
    {
        Assert.True(_windowsBuilt == 1,
            $"this session built {_windowsBuilt} MainWindows; a key press is routed to the ONE globally " +
            "focused element, so it can land in a window other than the one under test and do nothing " +
            $"at all. {FocusDescription(win)}. This is a HARNESS fault, not a fault in the Esc router.");
    }

    /// <summary>Esc on Song Select hands the player to the shell's Home (the Play Mode menu is gone).</summary>
    private static void AssertLandedOnHome(MainWindow win, MainWindowViewModel vm)
    {
        if (vm.IsHomeVisible) return;
        Assert.Fail($"Esc did not reach Home. screen={vm.PlayMode.CurrentScreen} {ShellState(win, vm)}");
    }

    [Fact]
    public async Task Escape_OnSongSelect_GoesHome()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (win, vm) = Boot();
            vm.OpenPlayModeCommand.Execute(null);
            vm.PlayMode.CurrentScreen = PlayModeScreen.SongSelect;
            win.UpdateLayout();
            Settle();

            AssertKeyboardWillReach(win);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            AssertLandedOnHome(win, vm);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Escape_AfterFocusedElementRemoved_StillNavigates()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (win, vm) = Boot();
            vm.OpenPlayModeCommand.Execute(null);

            // The guard deliberately leaves inactive windows alone. Headless
            // Show() does not guarantee activation, so establish that precondition.
            win.Activate();
            Settle();
            Assert.True(win.IsActive, "The focus-guard scenario requires an active window.");

            // Simulate the killer scenario: focus dies with a removed control.
            vm.PlayMode.CurrentScreen = PlayModeScreen.SongSelect;
            win.UpdateLayout();
            win.Focusable = true;
            // Simulate focus dying with a removed control: focus a button,
            // then rip it out of its parent panel.
            var victim = new Avalonia.Controls.Button { Focusable = true };
            if (win.Content is Avalonia.Controls.Panel rootPanel)
            {
                rootPanel.Children.Add(victim);
                victim.Focus();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                rootPanel.Children.Remove(victim);
            }
            // No click, no screen change — only the 250 ms focus-guard timer
            // can save this one. It is a DispatcherTimer, so it cannot fire
            // while this thread sleeps; it fires on the next RunJobs after it
            // is due. A fixed 450 ms sleep + one RunJobs was enough in
            // isolation and not in a full headless run (2026-08-29: the v0.48.5
            // release preflight failed here twice with focus already ON the
            // sink at assert time — the anchor landed AFTER the Esc). So: wait
            // for the anchor itself, bounded, and fail loudly if it never comes.
            //
            // The cap is a DEADLOCK DETECTOR, not a budget: the guard's timer is
            // 250 ms, so a green run leaves this loop on its first or second
            // turn and never reads the cap. It was 5 s, which IS a budget — this
            // suite's own dispatch does a great deal of Background prewarm work
            // on the same thread, and on a box running eight agents the timer's
            // due job can sit behind it for longer than that. The failure then
            // reads as "the focus guard never re-anchored", about a guard that
            // anchors correctly.
            var anchored = false;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                System.Threading.Thread.Sleep(50);
                Settle();
                if (win.FocusManager?.GetFocusedElement() is Avalonia.Controls.Control { Name: "FocusSink" })
                {
                    anchored = true;
                    break;
                }
            }
            Assert.True(anchored,
                $"the focus guard never re-anchored keyboard focus on the FocusSink within 60 s. {FocusDescription(win)}");

            AssertKeyboardWillReach(win);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // The PointerReleased/screen-change anchors can't run here (no
            // click happened), so the window itself must have caught the key
            // via its own focus — proving the boot/screen anchors held.
            AssertLandedOnHome(win, vm);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EditorPedalboard_OpenCloseToggleCommands_FlipTheOverlayFlag()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (win, vm) = Boot();
            vm.SelectedMainTabIndex = 0; // what MainNavSwitcher's first RadioButton writes
            win.UpdateLayout();
            Settle();

            Assert.False(vm.ToneRig.IsPedalboardOpen);

            // The overlay hosts the SAME PedalboardViewModel Play Mode drives
            // (DI singleton) — one shared DSP chain across both surfaces.
            Assert.NotNull(vm.ToneRig.Pedalboard);

            vm.ToneRig.OpenPedalboardCommand.Execute(null);
            Assert.True(vm.ToneRig.IsPedalboardOpen);

            vm.ToneRig.IsPedalboardOpen = false; // ClosePedalboardCommand is gone (0.58)
            Assert.False(vm.ToneRig.IsPedalboardOpen);

            // The rail's Rig row is the toggle: it writes the property both ways.
            vm.ToneRig.IsPedalboardOpen = true;
            Assert.True(vm.ToneRig.IsPedalboardOpen);
            vm.ToneRig.IsPedalboardOpen = false;
            Assert.False(vm.ToneRig.IsPedalboardOpen);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Escape_WithEditorPedalboardOpen_ClosesPedalboard()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (_, vm) = Boot();
            vm.SelectedMainTabIndex = 0; // what MainNavSwitcher's first RadioButton writes
            Settle();

            // Neutralize any ambient dialog (crash-recovery can auto-open at
            // startup) so the Esc router reaches the pedalboard branch instead of
            // closing a dialog first. Don't Settle after opening the overlay so no
            // new dialog can slip in before HandleEscapeKey.
            foreach (var d in vm.Dialogs.AllDialogs)
                if (d.IsOpen) d.RequestClose();
            Settle();

            vm.ToneRig.OpenPedalboardCommand.Execute(null);
            Assert.True(vm.ToneRig.IsPedalboardOpen);

            // The shell's Esc router dismisses the editor pedalboard overlay first
            // (topmost editor surface), before falling through to clearing the
            // chart selection. Drive the router directly — the window-focus key
            // plumbing is already covered by the navigation tests above.
            vm.HandleEscapeKey();

            Assert.False(vm.ToneRig.IsPedalboardOpen);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Escape_WithOpenDialog_ClosesDialog_NotNavigation()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (win, vm) = Boot();
            vm.OpenPlayModeCommand.Execute(null);
            vm.PlayMode.CurrentScreen = PlayModeScreen.SongSelect;
            // A dialog on the dialog host (target-architecture.md § 3.2): the
            // window's ONE Esc route must close it through IDialogs.CloseTop.
            var answer = vm.Dialogs.Hosted.ShowAsync<ISetBpmScreen, bool>();
            win.UpdateLayout();
            Settle();
            Assert.True(vm.Dialogs.HasOpenDialog, ShellState(win, vm));

            AssertKeyboardWillReach(win);
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!answer.IsCompleted && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.True(answer.IsCompletedSuccessfully, ShellState(win, vm));
            Assert.False(answer.Result);
            Assert.False(vm.Dialogs.HasOpenDialog, ShellState(win, vm));
            // Navigation must NOT also have happened for the same press.
            Assert.Equal(PlayModeScreen.SongSelect, vm.PlayMode.CurrentScreen);
        }, TestContext.Current.CancellationToken);
    }
}
