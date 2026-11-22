using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #447: in the Tone designer, Delete on the project tone list removes only the definition
///     (no prompt), Ctrl+Z brings it back and Ctrl+Y removes it again while the dialog is open (the
///     main window's chord is blocked behind it); Delete in a text field edits text, not tones.
///     The designer still lays out non-zero after the delete.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignerDeleteKeysUiPerfSmokeTests
{
    [Fact]
    public async Task DeleteKeyOnTheList_RemovesTheDefinition_AndCtrlZAndCtrlYStepTheHistory()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var list = view.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "ProjectToneList");
                vm.SelectedTone = vm.ProjectTones[0];
                var focused = list.GetVisualDescendants().OfType<ListBoxItem>().First().Focus();
                Settle(window);
                var holder = TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement();
                Assert.True(holder is Visual v && (v == list || list.IsVisualAncestorOf(v)),
                    $"focus returned {focused}, the focused element is {holder?.GetType().Name} (list visible {list.IsEffectivelyVisible}, {list.Bounds})");

                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Settle(window);
                Assert.Equal(["tone1"], vm.ProjectTones.Select(t => t.Key));
                NarrowWidthLayout.Capture(window, "tone-designer-after-delete");
                Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1, $"the designer laid out {view.Bounds} after the delete");

                Assert.True(TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual after && view.IsVisualAncestorOf(after),
                    $"after the delete the focus is on {TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement()?.GetType().Name}");
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                Settle(window);
                Assert.Equal(["tone0", "tone1"], vm.ProjectTones.Select(t => t.Key).Order());

                window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
                Settle(window);
                Assert.Equal(["tone1"], vm.ProjectTones.Select(t => t.Key));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DeleteKeyInATextField_DoesNotDeleteTheTone()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                vm.SelectedTone = vm.ProjectTones[0];
                Settle(window);
                var box = view.GetVisualDescendants().OfType<TextBox>().First(b => b.IsEffectivelyVisible);
                box.Focus();
                Settle(window);

                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Settle(window);
                Assert.Equal(2, vm.ProjectTones.Count);
            });
        }, CancellationToken.None);
    }

    private static void WithDesigner(Action<Control, Window, ToneDesignerViewModel> act)
    {
        var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(1, 2);
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped<IToneDesignerScreen>(_ => SurfaceShowRewarmUiPerfSmokeTests.ToneDesigner(project));
        using var provider = services.BuildServiceProvider();
        SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
        {
            _ = host.ShowAsync<IToneDesignerScreen, bool>();
            Settle(window);
            var view = host.OpenViews[^1];
            act(view, window, Assert.IsType<ToneDesignerViewModel>(view.DataContext));
            host.CloseTop();
            Settle(window);
        });
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
