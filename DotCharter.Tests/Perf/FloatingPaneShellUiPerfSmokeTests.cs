using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Floating editor windows carry the shell with them: the timeline pop-out
///     takes the inline hand-position / section editor overlay along, every
///     floating window (pop-outs and Dock float windows) is scaled by the
///     interface scale and lays out non-zero inside it, and a floated Dock pane
///     keeps the shell's shortcuts while it holds focus.
/// </summary>
[Collection("HeadlessUI")]
public sealed class FloatingPaneShellUiPerfSmokeTests
{
    private const double Scale = 1.5;

    [Fact]
    public Task TheTimelinePopOut_TakesTheInlineEditorOverlay_AndBringsItHome() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, _) =>
        {
            var overlay = Named<Control>(window, "InlineEditOverlay");
            var timeline = Named<Control>(window, "TimelineGrid");
            Assert.Same(window, TopLevel.GetTopLevel(overlay));

            window.PopOutTimelineWindow();
            var pop = window.TimelinePopOutWindow;
            Assert.NotNull(pop);
            Settle(pop!);
            Assert.Same(pop, TopLevel.GetTopLevel(timeline));
            Assert.Same(pop, TopLevel.GetTopLevel(overlay));
            Assert.True(overlay.Bounds is { Width: > 0, Height: > 0 }, $"the floated overlay laid out as {overlay.Bounds}");

            pop!.Close();
            Settle(window);
            Assert.Same(window, TopLevel.GetTopLevel(timeline));
            Assert.Same(window, TopLevel.GetTopLevel(overlay));
        }), CancellationToken.None);

    [Fact]
    public Task TheTimelinePopOut_IsScaledLikeTheShell_AndLaysOutNonZero() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, _) =>
        {
            window.PopOutTimelineWindow();
            var pop = window.TimelinePopOutWindow!;
            Settle(pop);

            AssertScaled(pop);
            var timeline = Named<Control>(window, "TimelineGrid");
            Assert.True(timeline.Bounds is { Width: > 0, Height: > 0 }, $"the floated timeline laid out as {timeline.Bounds}");
            pop.Close();
            Settle(window);
        }), CancellationToken.None);

    [Fact]
    public Task AFloatedDockPane_IsScaled_AndKeepsTheShellsShortcuts() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsAiChatVisible = true;
            Settle(window);
            var side = window.SideDockController!;
            side.FloatPane(MainWindow.AiChatPaneId, 100, 100, 480, 600);
            Settle(window);
            var host = Assert.IsType<HostWindow>(side.GetFloatWindow(MainWindow.AiChatPaneId)?.Host);
            try
            {
                Settle(host);
                AssertScaled(host);

                // The key must start INSIDE the float window: with the focus left in the shell the
                // keyboard routes it through MainWindow, whose own handlers answer it.
                var target = host.GetVisualDescendants().OfType<InputElement>()
                    .First(c => c is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
                Assert.True(target.Focus(), $"{target.GetType().Name} in the float window took no focus");
                Settle(host);
                Assert.Same(host, TopLevel.GetTopLevel(host.FocusManager?.GetFocusedElement() as Visual));
                Assert.False(vm.CommandPalette.IsOpen);
                host.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, null);
                Settle(host);
                Assert.True(vm.CommandPalette.IsOpen, "Ctrl+Shift+P in the floated pane's window did not reach the shell");
            }
            finally
            {
                vm.CommandPalette.Close();
                side.DockPaneHome(MainWindow.AiChatPaneId);
                Settle(window);
            }
        }), CancellationToken.None);

    /// <summary>The wrapper on a plain templated window: the scaler goes around the content presenter, the content is measured inside it, and the window takes the shell root's flow direction.</summary>
    [Fact]
    public Task TheTemplatedWrapper_ScalesAPlainWindowsContent() =>
        HeadlessSessionFixture.Dispatch(() => WithScale(() =>
        {
            var content = new Border { Width = 200, Height = 100 };
            var shellRoot = new Border { FlowDirection = FlowDirection.RightToLeft };
            var window = new Window { Width = 640, Height = 480, Content = content };
            FloatingWindowShell.HostTemplated(window, shellRoot);
            window.Show();
            try
            {
                Settle(window);
                AssertScaled(window);
                Assert.True(content.Bounds is { Width: > 0, Height: > 0 }, $"the content laid out as {content.Bounds}");
                Assert.Contains(content.GetVisualAncestors(), a => a is LayoutTransformControl { Name: WindowUiScale.ScalerName });

                // The direction follows the shell's root while the window is open: a language switch flips both.
                Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
                shellRoot.FlowDirection = FlowDirection.LeftToRight;
                Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
            }
            finally
            {
                window.Close();
            }
        }), CancellationToken.None);

    /// <summary>A window templated again while open (a theme or style change) keeps its scaler around the new template's content presenter.</summary>
    [Fact]
    public Task TheTemplatedWrapper_FollowsATemplateAppliedAgain() =>
        HeadlessSessionFixture.Dispatch(() => WithScale(() =>
        {
            var content = new Border { Width = 200, Height = 100 };
            var window = new Window { Width = 640, Height = 480, Content = content };
            FloatingWindowShell.HostTemplated(window, new Border());
            window.Show();
            try
            {
                Settle(window);
                AssertScaled(window);
                var template = window.Template;
                window.Template = null;
                Settle(window);
                window.Template = template;
                Settle(window);

                AssertScaled(window);
                Assert.Contains(content.GetVisualAncestors(), a => a is LayoutTransformControl { Name: WindowUiScale.ScalerName });
            }
            finally
            {
                window.Close();
            }
        }), CancellationToken.None);

    private static void AssertScaled(Window window)
    {
        var scalers = window.GetVisualDescendants().OfType<LayoutTransformControl>()
            .Where(l => l.Name == WindowUiScale.ScalerName).ToList();
        var scaler = Assert.Single(scalers);
        var transform = Assert.IsType<ScaleTransform>(scaler.LayoutTransform);
        Assert.Equal(Scale, transform.ScaleX, 3);
        Assert.Equal(Scale, transform.ScaleY, 3);
        Assert.True(scaler.Bounds is { Width: > 0, Height: > 0 }, $"the scaler laid out as {scaler.Bounds}");
    }

    private static T Named<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} is not in the shell");

    private static void Settle(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
    }

    /// <summary>Manual 150 % for the body, put back to manual 100 % inside the same dispatch (see <c>InterfaceScaleUiPerfSmokeTests</c>).</summary>
    private static void WithScale(Action body)
    {
        UiScaleController.SetAutoMode(false);
        UiScaleController.Set(Scale);
        try
        {
            body();
        }
        finally
        {
            UiScaleController.Set(1.0);
        }
    }

    private static void WithShell(Action<MainWindow, MainWindowViewModel> body) => WithScale(() =>
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen) dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsAiChatVisible = false;
        var previous = (state.ActiveProject, state.ActiveArrangement);
        NarrowWidthLayout.SeedSong(state);
        services.GetRequiredService<ISelectionService>().ClearSelections();

        // No window.Close(): closing the SHELL runs the app's shutdown path (see EditorShortWindowUiPerfSmokeTests).
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        try
        {
            Settle(window);
            body(window, vm);
        }
        finally
        {
            window.ClosePopOutWindows();
            vm.IsAiChatVisible = false;
            state.ActiveProject = previous.ActiveProject;
            state.ActiveArrangement = previous.ActiveArrangement;
        }
    });
}
