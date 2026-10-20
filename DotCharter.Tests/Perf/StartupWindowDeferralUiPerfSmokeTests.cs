using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>The behavioural half of <c>UI/StartupWindowDeferralTests</c> (#194): the
///     rig, the menu bar, the editor toolbar and the status rail are absent from
///     the boot tree, and they ARRIVE — through the prewarm drain after first paint,
///     or through the user's own first click.</b>
///     <para>
///         Each is the <c>ContentTemplate</c> body of a host, which Avalonia builds
///         only when a layout pass reaches the presenter. That is a framework
///         behaviour an Avalonia upgrade could change silently, putting ~1,100
///         elements back in front of the first frame with every lint green — hence a
///         real shell, booted the way the default config boots it (Play Mode).
///         Counted in instances and measured sizes, never milliseconds.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class StartupWindowDeferralUiPerfSmokeTests(ITestOutputHelper output)
{
    private const int PlayModeTabIndex = 3;

    /// <summary>The deferred views and how many the shell holds once warm: the rig
    /// twice (the shell's overlay and Play Mode's), every rail once.</summary>
    private static readonly (Type View, int Warm)[] Deferred =
    [
        (typeof(AmpFxRigView), 2),
        (typeof(MainMenuView), 1),
        (typeof(ToolbarView), 1),
        (typeof(StatusBar), 1)
    ];

    [Fact]
    public Task A_play_mode_boot_builds_none_of_them_and_the_prewarm_builds_them_all() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = PlayModeShell();

            var window = new MainWindow();
            AssertNoneBuilt(window, "inside new MainWindow() (the windowCtor= phase)");

            window.DataContext = vm;
            Assert.True(window.PlayModeHost.Content is PlayModeView,
                "the Play Mode boot surface was not filled in the bind — the first frame would be empty, " +
                "and the 'nothing built' check below would be vacuous");
            AssertNoneBuilt(window, "in the DataContext bind (the bind= phase)");

            window.Width = 1440;
            window.Height = 900;
            window.Show();
            AssertNoneBuilt(window, "by the first layout pass, i.e. for the FIRST FRAME");

            // The prewarm: Loaded posts it at Background, one surface per turn.
            for (var i = 0; i < 600; i++) Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            foreach (var (type, warm) in Deferred)
            {
                var built = Instances(window, type);
                Assert.True(built.Count == warm,
                    $"after the prewarm drain the shell holds {built.Count} {type.Name}(s), expected {warm}. " +
                    "A body the drain does not build is built — and laid out — by the user's first click instead.");
                foreach (var view in built)
                {
                    Assert.True(view.DesiredSize is { Width: > 0, Height: > 0 },
                        $"{type.Name} was built by the prewarm but measured {view.DesiredSize} — it was never " +
                        "laid out, so the first switch still pays its whole first layout.");
                }
            }

            // LOGGED, never asserted (Debug, headless, r2r=no — read the shape): the
            // first switches the deferred views sit on, now that the drain has paid
            // for them. The budget is 150 ms, the target 20.
            var toEditor = Stopwatch.StartNew();
            vm.SelectedMainTabIndex = 0;
            window.UpdateLayout();
            toEditor.Stop();
            var rigOpen = Stopwatch.StartNew();
            vm.ToneRig.IsPedalboardOpen = true;
            window.UpdateLayout();
            rigOpen.Stop();
            vm.ToneRig.IsPedalboardOpen = false;
            window.UpdateLayout();
            // The SECOND open is the control: what an open costs once nothing is cold.
            var rigReopen = Stopwatch.StartNew();
            vm.ToneRig.IsPedalboardOpen = true;
            window.UpdateLayout();
            rigReopen.Stop();
            vm.ToneRig.IsPedalboardOpen = false;
            output.WriteLine($"after the prewarm drain: first switch to the editor (menu bar, toolbar, status rail) " +
                             $"{toEditor.Elapsed.TotalMilliseconds:0.0} ms; first rig open {rigOpen.Elapsed.TotalMilliseconds:0.0} ms, " +
                             $"second {rigReopen.Elapsed.TotalMilliseconds:0.0} ms " +
                             "(Debug, headless, r2r=no).");
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     A user who opens the rig before the drain reaches it needs no code path
    ///     of ours: the overlay turning visible measures the host, which builds the
    ///     body. If that ever stopped being true the rig would open EMPTY.
    /// </summary>
    [Fact]
    public Task Opening_the_rig_before_any_prewarm_builds_and_lays_it_out() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = PlayModeShell();
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            Assert.Empty(Instances(window.ShellRigScreenHost, typeof(AmpFxRigView)));

            vm.ToneRig.IsPedalboardOpen = true;
            window.UpdateLayout();

            var rig = Assert.Single(Instances(window.ShellRigScreenHost, typeof(AmpFxRigView)));
            Assert.True(rig.Bounds is { Width: > 1, Height: > 1 },
                $"opening the rig built it but laid it out as {rig.Bounds} — it rendered nothing.");
            vm.ToneRig.IsPedalboardOpen = false;
        }, TestContext.Current.CancellationToken);

    private static MainWindowViewModel PlayModeShell()
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
        vm.SelectedMainTabIndex = PlayModeTabIndex;
        Assert.True(vm.IsPlayModeVisible && !vm.IsShellChromeVisible,
            "the shell did not land on full-bleed Play Mode, the default boot this test is about");
        return vm;
    }

    private static void AssertNoneBuilt(Control root, string when)
    {
        foreach (var (type, _) in Deferred)
        {
            var built = Instances(root, type);
            Assert.True(built.Count == 0,
                $"{built.Count} {type.Name}(s) were built {when}. Each is a ContentTemplate body so that a " +
                "hidden host never builds it before first paint; something reached the presenter.");
        }
    }

    private static List<Control> Instances(Control root, Type type) =>
        [.. root.GetSelfAndLogicalDescendants().OfType<Control>().Where(type.IsInstanceOfType)];
}
