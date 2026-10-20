using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using DotCharter.Tests.Perf;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Application = Avalonia.Application;

namespace DotCharter.Tests.UI;

/// <summary>
///     Regression tests for the v0.32.x "ribbon-row toggle untoggles/retoggles on
///     pause/unpause" bug. Root cause: the chart editor claims the play/pause
///     Space at the window TUNNEL on KeyDown (so it beats a focused button), but
///     Avalonia's Button fires its click on the Space KEY-UP (default
///     ClickMode.Release). Suppressing only the KeyDown left the paired KeyUp live,
///     so a clicked (hence focused) ribbon-row ToggleButton flipped on every
///     play/pause. The fix swallows the paired KeyUp at the same tunnel
///     (<c>MainWindow.GlobalKeyUpPreview</c>). These drive REAL headless keyboard
///     events through the real MainWindow and assert the focused toggle survives.
/// </summary>
// NOTE: the class name deliberately contains "UiPerfSmokeTests" so the default
// gate filter (FullyQualifiedName!~UiPerfSmokeTests) excludes it — booting the
// full app inside the headless session passes reliably in its own process, but
// not when another collection has claimed the process-global dispatcher (same
// reason as the escape-routing tests). Run:
//   dotnet test --filter "FullyQualifiedName~PlayPauseSpaceKeyRouting"
[Collection("HeadlessUI")]
public sealed class PlayPauseSpaceKeyRoutingUiPerfSmokeTests
{
    private static (MainWindow Win, MainWindowViewModel Vm) Boot()
    {
        var lifetime = Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        MainWindow win;
        if (lifetime?.MainWindow is MainWindow existing)
        {
            if (!existing.IsVisible) existing.Show();
            win = existing;
        }
        else
        {
            win = new MainWindow { DataContext = App.Services?.GetRequiredService<MainWindowViewModel>() };
            win.Show();
        }

        var vm = (MainWindowViewModel)win.DataContext!;
        vm.SelectedMainTabIndex = 0; // what MainNavSwitcher's first RadioButton writes
        win.UpdateLayout();
        // The Esc/keybind routes stand down while a dialog is open; crash-recovery
        // can auto-open one at startup, which would block the play/pause KeyDown.
        foreach (var d in vm.Dialogs.AllDialogs)
            if (d.IsOpen) d.RequestClose();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (win, vm);
    }

    /// <summary>Positive control: a focused ToggleButton in a PLAIN window (no
    /// editor tunnel) DOES flip on a Space press+release — proving the headless
    /// keyboard injection actually exercises Button's KeyUp click path, so the
    /// editor test below is not a vacuous pass.</summary>
    [Fact]
    public async Task PlainWindow_FocusedToggleButton_FlipsOnSpaceKeyUp()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var toggle = new ToggleButton { IsChecked = false, Focusable = true };
            var win = new Window { Content = toggle, Width = 200, Height = 100 };
            win.Show();
            win.UpdateLayout();
            toggle.Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(toggle.IsFocused);

            win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            win.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(toggle.IsChecked);   // clicked on the Space KEY-UP
            win.Close();
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The bug + fix: with the chart editor visible, the SAME focused
    /// ToggleButton must NOT flip when the play/pause Space is pressed — the
    /// tunnel claims the KeyDown for play/pause and swallows the paired KeyUp.</summary>
    [Fact]
    public async Task ChartEditor_FocusedToggleButton_DoesNotFlipOnPlayPauseSpace()
    {
        // Via the fixture, so the app's Background prewarm chains are drained inside this
        // dispatch instead of inside Avalonia's 5 s teardown drain. See HeadlessSessionFixture.Dispatch.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (win, _) = Boot();

            // A focused ToggleButton stands in for the ribbon "Sections" chip:
            // any focused Button/ToggleButton hits the same Space-KeyUp click path.
            // Host it in the window's overlay layer (a Panel that is always part of
            // the window's visual + focus tree, so its KeyUp routes through the
            // MainWindow tunnel handlers under test).
            var chip = new ToggleButton { IsChecked = false, Focusable = true };
            var host = OverlayLayer.GetOverlayLayer(win);
            Assert.NotNull(host);
            host!.Children.Add(chip);
            win.UpdateLayout();
            chip.Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(chip.IsFocused);

            // Full Space press: KeyDown (claimed as play/pause) + KeyUp (must be swallowed).
            win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            win.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Repeat a few times: every play/pause must leave the chip untouched.
            for (var i = 0; i < 3; i++)
            {
                win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                win.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Assert.False(chip.IsChecked);   // never toggled by the play/pause Space
        }, TestContext.Current.CancellationToken);
    }
}
