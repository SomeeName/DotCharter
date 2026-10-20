using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Core;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Docking;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Regression tests for UI audit 2026-07-25 finding 4.1:
///     <c>DockLayoutController.Rebuild()</c> orphaned float windows. Repro:
///     float the Inspector out, then View → Reset Layout
///     (<c>MainWindow.ResetLayoutToDefaults</c> → <c>Rebuild()</c>). The old
///     floating <c>HostWindow</c> stayed on screen, still presenting a
///     <c>DockableView</c> over the SAME view model the freshly-rebuilt docked
///     pane now presented — two live views over one VM. The orphan was no
///     longer reachable from <c>_layout</c>, so <c>CaptureState</c> recorded
///     the pane as "Docked" and the window was silently lost on restart; and
///     closing it later raised the OLD factory's still-attached
///     <c>WindowClosed</c> → <c>OnLayoutMutated()</c>, which reads the NEW
///     layout and could flip the pane's visibility flag off with the docked
///     copy still there.
///
///     Nothing under <c>Docking/</c> had a test before this. The layout model
///     is exercisable without an Avalonia platform (a real float would need a
///     <c>HostWindow</c>, so the float window is fabricated at the model level
///     — which is precisely the shape <c>Rebuild()</c> has to clean up).
/// </summary>
public sealed class DockLayoutControllerTests
{
    /// <summary>Records whether the float window was told to close.
    /// <c>DockWindow.Exit()</c> is not virtual — it forwards to
    /// <c>Host.Exit()</c>, which in a live app is the <c>HostWindow</c> that
    /// needs an Avalonia platform. Standing in for the host is the smallest
    /// seam that still runs the real model code.</summary>
    private sealed class SpyHostWindow : IHostWindow
    {
        public bool Exited { get; private set; }

        /// <summary>Where the window ACTUALLY is on screen. A real
        /// <c>HostWindow</c> answers these from the platform, and the model's
        /// <c>X/Y/Width/Height</c> only learn about a drag or a resize when
        /// <c>IDockWindow.Save()</c> copies them across.</summary>
        public double HostX { get; set; }
        public double HostY { get; set; }
        public double HostWidth { get; set; }
        public double HostHeight { get; set; }

        public IDockWindow? Window { get; set; }
        public IHostWindowState? HostWindowState { get; set; }
        public bool IsTracked { get; set; }

        public void Present(bool isDialog) { }
        public void Exit() => Exited = true;
        public void SetPosition(double x, double y) { HostX = x; HostY = y; }
        public void GetPosition(out double x, out double y) { x = HostX; y = HostY; }
        public void SetSize(double width, double height) { HostWidth = width; HostHeight = height; }
        public void GetSize(out double width, out double height) { width = HostWidth; height = HostHeight; }
        public void SetTitle(string? title) { }
        public void SetLayout(IDock layout) { }
        public void SetWindowState(DockWindowState windowState) { }
        public DockWindowState GetWindowState() => DockWindowState.Normal;
        public void SetActive() { }
    }

    private sealed class PaneFlag
    {
        private bool _visible = true;

        /// <summary>How many times the controller WROTE the flag. The read side
        /// alone cannot tell "left at its default" apart from "set to the value
        /// it already had", and that is exactly the distinction the
        /// unknown-mode guard turns on.</summary>
        public int Writes { get; private set; }

        public bool Visible
        {
            get => _visible;
            set
            {
                Writes++;
                _visible = value;
            }
        }

        /// <summary>Seeds the flag without counting as a controller write.</summary>
        public void Preset(bool value)
        {
            _visible = value;
            Writes = 0;
        }
    }

    /// <summary>A pane Context is a VIEW MODEL by the type system now
    /// (<c>DockPaneDefinition.Context</c> is <see cref="ViewModelBase" />, not
    /// <c>object</c>) — so the test double has to be one too. That is the
    /// point: a control here no longer compiles.</summary>
    private sealed class FakePaneViewModel : ViewModelBase;

    private static (DockLayoutController Controller, PaneFlag Flag) NewController()
    {
        var host = new DockControl { Name = "Bottom" };
        var flag = new PaneFlag();
        var panes = new List<DockPaneDefinition>
        {
            new()
            {
                Id = "Inspector",
                Title = "Inspector",
                Context = new FakePaneViewModel(),
                IsVisible = () => flag.Visible,
                SetVisible = v => flag.Visible = v
            }
        };
        return (new DockLayoutController(host, panes), flag);
    }

    private static SpyHostWindow FloatAPane(DockLayoutController controller)
    {
        var root = controller.Layout!;
        var host = new SpyHostWindow();
        var window = new DockWindow { Owner = root, Host = host };
        host.Window = window;
        root.Windows ??= [];
        root.Windows.Add(window);
        return host;
    }

    [Fact]
    public void Rebuild_ClosesFloatWindowsFromThePreviousLayout()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        var floated = FloatAPane(controller);

        controller.Rebuild();

        Assert.True(floated.Exited, "the orphaned float window was never asked to close");
    }

    [Fact]
    public void Rebuild_LeavesNoWindowsBehindOnTheOldRoot()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        FloatAPane(controller);
        var oldRoot = controller.Layout!;

        controller.Rebuild();

        Assert.NotSame(oldRoot, controller.Layout);
        Assert.Empty(oldRoot.Windows!);
    }

    /// <summary>The rebuilt layout is a clean slate: the pane comes home docked
    /// and nothing is floating.</summary>
    [Fact]
    public void Rebuild_BringsTheFloatedPaneHomeDocked()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        FloatAPane(controller);

        controller.Rebuild();

        Assert.NotNull(controller.FindPane("Inspector"));
        Assert.Null(controller.GetFloatWindow("Inspector"));
        Assert.True(controller.Layout!.Windows is null or { Count: 0 });
    }

    /// <summary>Repeated rebuilds must stay stable — Reset Layout is not a
    /// once-per-session operation.</summary>
    [Fact]
    public void Rebuild_IsRepeatable()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();

        for (var i = 0; i < 3; i++)
        {
            FloatAPane(controller);
            controller.Rebuild();
        }

        Assert.True(flag.Visible);
        Assert.NotNull(controller.FindPane("Inspector"));
    }

    /// <summary>A hidden pane stays out of the rebuilt layout — the teardown
    /// must not resurrect panes the user closed.</summary>
    [Fact]
    public void Rebuild_OmitsPanesWhoseVisibilityFlagIsFalse()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();
        FloatAPane(controller);
        flag.Visible = false;

        controller.Rebuild();

        Assert.Null(controller.FindPane("Inspector"));
    }

    // ---------------------------------------------------------------------
    // RestoreState against a HOSTILE snapshot.
    //
    // DockLayoutState round-trips through JSON inside AppConfig, so it can come
    // back in shapes CaptureState never writes, and RestoreState runs inside
    // MainWindow's Loaded handler — where an escaping exception silently costs
    // the rest of first-paint wiring (PrewarmTopLevelViews, the focus anchor,
    // the autopilot arm). Every one of these used to be a way to lose that.
    // ---------------------------------------------------------------------

    private static DockLayoutState SnapshotWith(params (string Id, DockPaneState? State)[] entries)
    {
        var state = new DockLayoutState();
        foreach (var (id, pane) in entries) state.Panes[id] = pane!;
        return state;
    }

    /// <summary>A <c>"Inspector": null</c> in the config file. The dictionary
    /// value type is non-nullable in C#, which is exactly why nobody checked —
    /// System.Text.Json writes the null in anyway and the old code went
    /// straight to <c>s.Mode</c>.</summary>
    [Fact]
    public void RestoreState_SurvivesANullPaneEntry()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();
        flag.Preset(true);

        controller.RestoreState(SnapshotWith(("Inspector", null)));

        Assert.Equal(0, flag.Writes);
        Assert.NotNull(controller.FindPane("Inspector"));
    }

    /// <summary>
    ///     A mode string from another build (or a hand-edited config) is not an
    ///     instruction — the pane keeps the DEFAULT layout instead of being
    ///     interpreted as "not Closed, therefore visible".
    ///
    ///     <para>It asserts the flag is never WRITTEN, not merely that its value
    ///     is unchanged. Asserting the value proves nothing here: the old code
    ///     read any unrecognized string as `!= "Closed"` and pushed
    ///     <c>SetVisible(true)</c>, which from the open default is
    ///     indistinguishable from doing nothing. The write count is what fails
    ///     against that code — which is the whole point of the test.</para>
    /// </summary>
    [Theory]
    [InlineData("Maximized")]
    [InlineData("")]
    [InlineData("docked")]
    public void RestoreState_IgnoresAModeThisBuildNeverWrites(string mode)
    {
        var (controller, flag) = NewController();
        controller.Rebuild();
        flag.Preset(true);

        controller.RestoreState(SnapshotWith(("Inspector", new DockPaneState { Mode = mode })));

        Assert.True(flag.Writes == 0,
            $"mode '{mode}' is not one this build writes, so it must not drive the pane's " +
            $"visibility at all — the controller wrote the flag {flag.Writes} time(s).");
        Assert.NotNull(controller.FindPane("Inspector"));
    }

    /// <summary>A snapshot naming a pane this build no longer registers (the
    /// Stem Mixer and Riff Library were panes until 0.42.4) is ignored, not a
    /// lookup failure.</summary>
    [Fact]
    public void RestoreState_IgnoresPanesThatNoLongerExist()
    {
        var (controller, _) = NewController();
        controller.Rebuild();

        controller.RestoreState(SnapshotWith(
            ("StemMixer", new DockPaneState { Mode = "Floating" }),
            ("PhraseCatalog", new DockPaneState { Mode = "Pinned" })));

        Assert.NotNull(controller.FindPane("Inspector"));
    }

    /// <summary>An entirely empty snapshot leaves every pane at its default.</summary>
    [Fact]
    public void RestoreState_SurvivesAnEmptySnapshot()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();

        controller.RestoreState(new DockLayoutState());

        Assert.True(flag.Visible);
        Assert.NotNull(controller.FindPane("Inspector"));
    }

    /// <summary>"Closed" still closes — the hardening above must not have made
    /// RestoreState inert. Without this the four tests around it would all pass
    /// against a RestoreState whose body was deleted.</summary>
    [Fact]
    public void RestoreState_StillAppliesTheModesItDoesRecognize()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();
        flag.Preset(true);

        controller.RestoreState(SnapshotWith(("Inspector", new DockPaneState { Mode = "Closed" })));

        Assert.True(flag.Writes >= 1, "a recognized mode must still drive the visibility flag");
        Assert.False(flag.Visible);
        Assert.Null(controller.FindPane("Inspector"));
    }

    /// <summary>Float bounds are sanitized before they reach a window. A NaN
    /// origin puts the window nowhere and the pane reads as lost; a 1e12 origin
    /// puts it off every monitor, which looks identical to the user.</summary>
    [Theory]
    [InlineData(double.NaN, 200)]
    [InlineData(double.PositiveInfinity, 200)]
    [InlineData(double.NegativeInfinity, 200)]
    [InlineData(1e12, 20000)]
    [InlineData(-1e12, -20000)]
    [InlineData(640, 640)]
    public void SanitizeBound_ClampsAnOriginIntoAUsableRange(double stored, double expected)
        => Assert.Equal(expected, DockLayoutController.SanitizeBound(stored, 200, -20000, 20000));

    /// <summary>A missing bound (a snapshot older than the field) takes the
    /// default, and a sub-minimum extent is floored — a 3 px float window is
    /// unreachable with a mouse.</summary>
    [Theory]
    [InlineData(null, 480)]
    [InlineData(3d, 320)]
    [InlineData(900d, 900)]
    public void SanitizeBound_FloorsAnExtent(double? stored, double expected)
        => Assert.Equal(expected, DockLayoutController.SanitizeBound(stored, 480, 320, 20000));

    /// <summary>
    ///     A float window's bounds are the PLATFORM's, not the model's:
    ///     <c>IDockWindow.X/Y/Width/Height</c> are plain fields that only learn
    ///     where the window went when <c>Save()</c> copies them off the host.
    ///     <c>CaptureState</c> read them raw, so "float the Inspector, drag it to
    ///     the second monitor, resize it, quit" persisted the bounds the window
    ///     was CREATED at, and it came back next launch in the middle of the
    ///     first screen at its opening size. Nothing else in the app refreshes
    ///     them first: Dock.Avalonia calls <c>Save()</c> at a drag-END and from
    ///     the host window's own close, and the app snapshots in
    ///     <c>MainWindow.OnClosing</c> — before any host window closes, and after
    ///     any number of resizes, which raise no drag at all.
    /// </summary>
    [Fact]
    public void CaptureState_ReadsTheFloatWindowsLiveBounds()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        var host = FloatThePaneInto(controller, x: 1440, y: 260, width: 720, height: 540);

        var state = new DockLayoutState();
        controller.CaptureState(state);

        var pane = state.Panes["Inspector"];
        Assert.Equal("Floating", pane.Mode);
        Assert.Equal(1440, pane.FloatX);
        Assert.Equal(260, pane.FloatY);
        Assert.Equal(720, pane.FloatWidth);
        Assert.Equal(540, pane.FloatHeight);
        // ...and the model really was stale: the capture is what refreshed it.
        Assert.Equal(1440, host.Window!.X);
    }

    /// <summary>
    ///     A floated pane can go home in one step: back into the tool dock it came from, with its
    ///     float window closed and gone from the model. Until 2026-09-24 the only way back was to
    ///     drag the window onto the dock, and the owner found no way at all ("you can't redock it
    ///     again where it was").
    /// </summary>
    [Fact]
    public void DockPaneHome_PutsAFloatedPaneBackInItsDock_AndClosesTheWindow()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        var host = FloatThePaneInto(controller, x: 300, y: 200, width: 720, height: 540);
        Assert.NotNull(controller.GetFloatWindow("Inspector"));

        Assert.True(controller.DockPaneHome("Inspector"));

        Assert.Null(controller.GetFloatWindow("Inspector"));
        Assert.True(host.Exited, "the emptied float window was left on screen");
        Assert.True(controller.Layout!.Windows is null or { Count: 0 });
        var pane = controller.FindPane("Inspector")!;
        Assert.Equal("BottomTools", ((IDock)pane.Owner!).Id);
        Assert.Same(pane, ((IDock)pane.Owner!).ActiveDockable);
    }

    [Fact]
    public void DockPaneHome_IsANoOp_ForAPaneThatIsAlreadyDocked()
    {
        var (controller, _) = NewController();
        controller.Rebuild();

        Assert.False(controller.DockPaneHome("Inspector"));
        Assert.Equal("BottomTools", ((IDock)controller.FindPane("Inspector")!.Owner!).Id);
    }

    /// <summary>
    ///     The floated pane's own menu. Dock.Avalonia enables its "Dock" item only for a tool with an
    ///     OriginalOwner and runs PinDockable; it never set one on a float, so the item was always greyed
    ///     out. A float now records its home and a PinDockable on a floated tool takes it there — and the
    ///     window carries the pane's name, not "IToolDock".
    /// </summary>
    [Fact]
    public void ThePaneMenusDock_TakesAFloatedPaneHome_AndLeavesAutoHideUsable()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        var home = (IDock)controller.FindPane("Inspector")!.Owner!;
        var host = FloatThePaneInto(controller, x: 300, y: 200, width: 720, height: 540);
        var dockable = controller.FindPane("Inspector")!;
        var window = controller.GetFloatWindow("Inspector")!;

        controller.Factory!.RememberHome(dockable, home);
        Assert.Same(home, dockable.OriginalOwner); // what enables the menu's "Dock"
        Assert.Equal("Inspector", window.Title);

        controller.Factory.PinDockable(dockable);

        Assert.Null(controller.GetFloatWindow("Inspector"));
        Assert.True(host.Exited);
        Assert.Same(home, dockable.Owner);
        Assert.Null(dockable.OriginalOwner); // docked again, so "Auto Hide" is enabled again
    }

    /// <summary>
    ///     Dock's pin leaves the tool dock's ActiveDockable on the pane it just removed ("TODO: Handle
    ///     ActiveDockable state", still there in 12.1.0.8): the dock keeps drawing the pinned pane and its
    ///     close button later becomes the CloseDockable(null) crash. The factory hooks PinDockableCore,
    ///     which Dock 12.1.0.8 runs for every pin toggle.
    /// </summary>
    [Fact]
    public void PinningAPane_MovesItsDocksActiveTabOffIt()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        var pane = controller.FindPane("Inspector")!;
        var dock = (IToolDock)pane.Owner!;
        dock.ActiveDockable = pane;

        controller.Factory!.PinDockable(pane);

        Assert.DoesNotContain(pane, dock.VisibleDockables!);
        Assert.NotSame(pane, dock.ActiveDockable);
    }

    [Fact]
    public void IsPaneFloating_FollowsTheFloatWindow()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        Assert.False(controller.IsPaneFloating("Inspector"));

        FloatThePaneInto(controller, x: 300, y: 200, width: 720, height: 540);
        Assert.True(controller.IsPaneFloating("Inspector"));

        controller.DockPaneHome("Inspector");
        Assert.False(controller.IsPaneFloating("Inspector"));
    }

    /// <summary>
    ///     #285: a gameplay run hides the panes through their visibility flags, and a flag
    ///     turned back on adds its pane to the tool dock, so an Inspector the user had floated
    ///     came back DOCKED after every run. The snapshot taken before the run floats it again.
    /// </summary>
    [Fact]
    public void AGameplayRun_LeavesAFloatedPaneFloatingWhereItWas()
    {
        var (controller, flag) = NewController();
        controller.Rebuild();
        FloatThePaneInto(controller, x: 300, y: 200, width: 720, height: 540);
        var snapshot = new GameplayDockSnapshot();

        snapshot.BeforeGameplay(controller, null);
        flag.Visible = false;
        controller.SetPaneVisible("Inspector", false);
        flag.Visible = true;
        controller.SetPaneVisible("Inspector", true);
        Assert.False(controller.IsPaneFloating("Inspector"), "the flag alone is expected to dock it");
        snapshot.AfterGameplay(controller, null);

        // Floating again, through FloatPane: the path the start-up restore takes, which hands
        // the bounds to the platform host (this test has none, so it does not read them back).
        Assert.True(controller.IsPaneFloating("Inspector"), "the run left the floated Inspector docked");
        Assert.True(flag.Visible);
    }

    /// <summary>The snapshot is spent by the run it was taken for: a second end does not
    /// drag the layout back to a stale state the user has since changed.</summary>
    [Fact]
    public void AGameplaySnapshot_IsRestoredOnce()
    {
        var (controller, _) = NewController();
        controller.Rebuild();
        FloatThePaneInto(controller, x: 300, y: 200, width: 720, height: 540);
        var snapshot = new GameplayDockSnapshot();
        snapshot.BeforeGameplay(controller);
        snapshot.AfterGameplay(controller);

        controller.DockPaneHome("Inspector");
        snapshot.AfterGameplay(controller);

        Assert.False(controller.IsPaneFloating("Inspector"));
    }

    /// <summary>Moves the built layout's pane into a float window sitting at the
    /// given screen bounds, with the model's own X/Y/W/H left at zero — the
    /// state a dragged or resized window is really in until <c>Save()</c> runs.</summary>
    private static SpyHostWindow FloatThePaneInto(
        DockLayoutController controller, double x, double y, double width, double height)
    {
        var root = controller.Layout!;
        var dockable = controller.FindPane("Inspector")!;
        if (dockable.Owner is IDock previousOwner) previousOwner.VisibleDockables!.Remove(dockable);

        var factory = new DotCharterDockFactory();
        var tools = factory.CreateToolDock();
        tools.Id = "FloatTools";
        tools.VisibleDockables = factory.CreateList<IDockable>(dockable);
        tools.ActiveDockable = dockable;
        dockable.Owner = tools;

        var layout = factory.CreateRootDock();
        layout.VisibleDockables = factory.CreateList<IDockable>(tools);
        layout.ActiveDockable = tools;
        tools.Owner = layout;

        var host = new SpyHostWindow { HostX = x, HostY = y, HostWidth = width, HostHeight = height };
        var window = new DockWindow { Owner = root, Host = host, Layout = layout };
        layout.Window = window;
        host.Window = window;
        root.Windows ??= [];
        root.Windows.Add(window);
        return host;
    }
}
