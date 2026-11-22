using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #514: a middle-mouse pan over the REAL shell timeline ends with the button (or with a lost
///     capture / Escape): the SizeAll cursor is gone, the continuous-render pin is balanced and the
///     view no longer follows the pointer. Real pointer events through the window, over the ribbon
///     rows and the note lanes, with and without the in-place editor open.
/// </summary>
[Collection("HeadlessUI")]
public sealed class TimelineMiddlePanUiPerfSmokeTests
{
    internal static void WithShell(Action<MainWindow, ChartRenderer, MainWindowViewModel> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<DotCharter.Services.State.ProjectState>();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen)
                dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        NarrowWidthLayout.SeedSong(state);
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            NarrowWidthLayout.Settle(window);
            body(window, NarrowWidthLayout.Named<ChartRenderer>(window, "MainChartRenderer"), vm);
        }
        finally
        {
            vm.ShellDialogs.InlineEdit.Fhp.Cancel();
            vm.ShellDialogs.InlineEdit.Section.Cancel();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }

    private static Point At(ChartRenderer renderer, double x, double y) => renderer.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(renderer)!)!.Value;

    [Fact]
    public Task ToggleKeys_InTheRealShell_RequireAReleaseBetweenActions() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            var original = vm.ProjectState.Keybinds.ToggleWaveform;
            var shown = vm.ProjectState.ShowWaveform;
            try
            {
                vm.ProjectState.Keybinds.ToggleWaveform = new KeyChord(Key.F24);
                vm.KeybindHandler.RefreshBinds();
                Assert.True(renderer.Focus());
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.Equal(!shown, vm.ProjectState.ShowWaveform);
                window.KeyRelease(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyPress(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.Equal(shown, vm.ProjectState.ShowWaveform);
                window.KeyRelease(Key.F24, RawInputModifiers.None, PhysicalKey.None, null);
            }
            finally
            {
                vm.ProjectState.Keybinds.ToggleWaveform = original;
                vm.ProjectState.ShowWaveform = shown;
                vm.KeybindHandler.ResetHeldKeys();
                vm.KeybindHandler.RefreshBinds();
            }
        }), CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InlineEditor_InTheRealShell_CancelsOnEscape(bool section) =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            var inline = vm.ShellDialogs.InlineEdit;
            if (section) inline.ShowSection(2000, null);
            else inline.ShowFhp(2000, null);
            NarrowWidthLayout.Settle(window);
            var overlay = window.GetVisualDescendants().OfType<DotCharter.UI.Views.Editor.InlineEditOverlayView>()
                .First(v => v.IsEffectivelyVisible);
            var box = overlay.FindControl<TextBox>(section ? "SectionNameBox" : "FhpFretBox")!;
            Assert.True(box.Bounds is { Width: > 1, Height: > 1 });
            Assert.True(box.Focus());
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(section ? inline.Section.IsOpen : inline.Fhp.IsOpen);
        }), CancellationToken.None);

    [Theory]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public Task Minimap_RejectsNonLeftPressesWithoutPinning(MouseButton button) =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            vm.IsMinimapVisible = true;
            NarrowWidthLayout.Settle(window);
            var map = window.FindControl<MinimapOverviewControl>("MinimapOverview")!;
            Assert.True(map.Bounds is { Width: > 1, Height: > 1 });
            var point = map.TranslatePoint(new Point(100, 10), window)!.Value;
            window.MouseDown(point, button);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
            window.MouseUp(point, button);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
        }), CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Minimap_ReleaseOrCaptureLossDropsExactlyOnePin(bool loseCapture) =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            vm.IsMinimapVisible = true;
            NarrowWidthLayout.Settle(window);
            var map = window.FindControl<MinimapOverviewControl>("MinimapOverview")!;
            var host = (Control)map.Parent!;
            IPointer? pointer = null;
            void Grab(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
            host.AddHandler(InputElement.PointerPressedEvent, Grab, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            var point = map.TranslatePoint(new Point(100, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            host.RemoveHandler(InputElement.PointerPressedEvent, Grab);
            Assert.NotNull(pointer);
            Assert.Equal(1, renderer.ContinuousRenderRequests);
            if (loseCapture) pointer.Capture(null);
            else window.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
        }), CancellationToken.None);

    [Fact]
    public Task RulerRelease_DeliversTheThrottledMove() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = typeof(MainWindow).GetField("_interactionManager", flags)!.GetValue(window)!;
            var coordinator = manager.GetType().GetField("_coordinator", flags)!.GetValue(manager)!;
            vm.ProjectState.TimelineFollowsPlayhead = false;
            vm.ProjectState.TimelineViewAnchorMs = 0;
            var point = At(renderer, 600, renderer.Bounds.Height - 8);
            window.MouseDown(point, MouseButton.Left);
            vm.ProjectState.TimelineFollowsPlayhead = false;
            vm.ProjectState.TimelineViewAnchorMs = 0;
            typeof(DotCharter.UI.Input.Tools.ToolCoordinator).GetField("_lastMoveTicks", flags)!.SetValue(coordinator, long.MaxValue);
            var expected = ChartHitTester.GetViewportBounds(vm.ProjectState.TimelineViewAnchorEffectiveMs,
                vm.ProjectState.PixelsPerMs, renderer.Bounds.Width).StartMs + 800 / vm.ProjectState.PixelsPerMs;
            Assert.True(expected > vm.ProjectState.CurrentTimeMs);
            window.MouseMove(At(renderer, 800, renderer.Bounds.Height - 8), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(renderer, 800, renderer.Bounds.Height - 8), MouseButton.Left);
            Assert.Equal(expected, vm.ProjectState.CurrentTimeMs, 0.01);
        }), CancellationToken.None);

    private static IEnumerable<double> Rows(ChartRenderer renderer)
    {
        var ribbon = renderer.TopRibbon;
        yield return ribbon.ToneY + 4;
        yield return ribbon.SectionY + 4;
        yield return ribbon.PhraseY + 4;
        yield return ribbon.MetadataY + 4;
        for (var y = renderer.TopMargin + 6; y < renderer.Bounds.Height - 6; y += 40)
            yield return y;
    }

    private static void AssertPanOver(MainWindow window, ChartRenderer renderer, double y, bool move, string label)
    {
        var press = At(renderer, 400, y);
        window.MouseDown(press, MouseButton.Middle);
        Assert.Equal(1, renderer.ContinuousRenderRequests);
        if (move) window.MouseMove(press + new Vector(-60, 0));
        window.MouseUp(press + new Vector(move ? -60 : 0, 0), MouseButton.Middle);
        Dispatcher.UIThread.RunJobs();

        Assert.True(renderer.ContinuousRenderRequests == 0, $"{label} y={y}: continuous render still pinned");
        Assert.True(!ReferenceEquals(renderer.Cursor, TimelineCursor(StandardCursorType.SizeAll)), $"{label} y={y}: cursor still SizeAll");

        // No button held: moving must not pan any further.
        var anchor = ((MainWindowViewModel)window.DataContext!).ProjectState.TimelineViewAnchorMs;
        window.MouseMove(press + new Vector(-200, 0));
        Assert.Equal(anchor, ((MainWindowViewModel)window.DataContext!).ProjectState.TimelineViewAnchorMs);
    }

    private static Cursor TimelineCursor(StandardCursorType type) => DotCharter.UI.Input.TimelineCursorPolicy.Cached(type);

    [Fact]
    public Task MiddlePress_OverEveryRow_EndsWithTheRelease() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            foreach (var y in Rows(renderer))
            {
                AssertPanOver(window, renderer, y, move: false, "click");
                AssertPanOver(window, renderer, y, move: true, "drag");
            }
        }), CancellationToken.None);

    [Fact]
    public Task MiddlePress_WithTheInlineEditorOpen_EndsWithTheRelease() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            foreach (var section in new[] { false, true })
            {
                if (section) vm.ShellDialogs.InlineEdit.ShowSection(2000, null);
                else vm.ShellDialogs.InlineEdit.ShowFhp(2000, null);
                NarrowWidthLayout.Settle(window);
                foreach (var y in Rows(renderer))
                {
                    AssertPanOver(window, renderer, y, move: false, "click");
                    AssertPanOver(window, renderer, y, move: true, "drag");
                }
            }
        }), CancellationToken.None);

    private static void AssertPanEnds(MainWindow window, ChartRenderer renderer, Action<IPointer> end, string label)
    {
        IPointer? pointer = null;
        void Grab(object? _, PointerPressedEventArgs e) => pointer = e.Pointer;
        renderer.AddHandler(InputElement.PointerPressedEvent, Grab, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        var press = At(renderer, 400, renderer.TopMargin + 30);
        window.MouseDown(press, MouseButton.Middle);
        renderer.RemoveHandler(InputElement.PointerPressedEvent, Grab);
        Assert.Equal(1, renderer.ContinuousRenderRequests);
        Assert.Same(renderer, pointer!.Captured);
        Assert.Same(DotCharter.UI.Input.TimelineCursorPolicy.Cached(StandardCursorType.SizeAll), renderer.Cursor);

        // A PointerExited while the capture is still held does not end the pan.
        window.MouseMove(press + new Vector(-30, 0));
        Assert.Equal(1, renderer.ContinuousRenderRequests);

        end(pointer);
        Dispatcher.UIThread.RunJobs();
        Assert.True(renderer.ContinuousRenderRequests == 0, $"{label}: the continuous render pin was not released exactly once ({renderer.ContinuousRenderRequests})");
        Assert.False(ReferenceEquals(DotCharter.UI.Input.TimelineCursorPolicy.Cached(StandardCursorType.SizeAll), renderer.Cursor), $"{label}: cursor still SizeAll");

        // The late release changes nothing.
        window.MouseUp(press, MouseButton.Middle);
        Assert.Equal(0, renderer.ContinuousRenderRequests);
    }

    [Fact]
    public Task ALostCapture_EndsThePan() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, _) =>
            AssertPanEnds(window, renderer, pointer => pointer.Capture(null), "capture lost")), CancellationToken.None);

    [Fact]
    public Task A_tool_gesture_captures_until_release_outside_the_timeline() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, _) =>
        {
            IPointer? pointer = null;
            void Grab(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
            renderer.AddHandler(InputElement.PointerPressedEvent, Grab, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            var press = At(renderer, 600, renderer.TopMargin + 30);
            window.MouseDown(press, MouseButton.Left);
            renderer.RemoveHandler(InputElement.PointerPressedEvent, Grab);
            Assert.Same(renderer, pointer!.Captured);
            window.MouseMove(new Point(5, 5));
            Assert.Equal(1, renderer.ContinuousRenderRequests);
            window.MouseUp(new Point(5, 5), MouseButton.Left);
            Assert.Null(pointer.Captured);
            Assert.Null(renderer.MarqueeRect);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
        }), CancellationToken.None);

    [Fact]
    public Task A_tool_capture_loss_cancels_previews_and_the_next_press_routes_afresh() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, vm) =>
        {
            vm.ChartEditor.Notes.Clear();
            vm.ChartEditor.Chords.Clear();
            IPointer? pointer = null;
            void Grab(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
            renderer.AddHandler(InputElement.PointerPressedEvent, Grab, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            var press = At(renderer, 600, renderer.TopMargin + 30);
            window.MouseDown(press, MouseButton.Left);
            renderer.RemoveHandler(InputElement.PointerPressedEvent, Grab);
            window.MouseMove(press + new Vector(100, 40), RawInputModifiers.LeftMouseButton);
            Assert.NotNull(renderer.MarqueeRect);
            pointer!.Capture(null);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
            Assert.Null(renderer.MarqueeRect);
            Assert.Empty(vm.ChartEditor.Selection.PreviewNotes);
            Assert.Empty(vm.ChartEditor.Selection.PreviewChords);
            window.MouseUp(new Point(5, 5), MouseButton.Left);
            var ribbon = At(renderer, 400, 4);
            window.MouseDown(ribbon, MouseButton.Left);
            Assert.Equal(DotCharter.Services.Editing.Tracks.EditorFocusArea.TimelineEvents, vm.ChartEditor.FocusArea);
            window.MouseUp(ribbon, MouseButton.Left);
            Assert.Equal(0, renderer.ContinuousRenderRequests);
        }), CancellationToken.None);

    [Fact]
    public Task Escape_EndsThePan() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, renderer, _) =>
            AssertPanEnds(window, renderer, _ => window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None), "escape")), CancellationToken.None);
}
