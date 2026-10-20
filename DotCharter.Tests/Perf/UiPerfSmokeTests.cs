using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public class UiPerfSmokeTests(ITestOutputHelper o)
{
    private readonly ITestOutputHelper _out = o;
    private static readonly int[] Action = [0, 1, 2, 3, 4, 5, 0];

    private record Measurement(string Label, double Ms, long AllocBytes);

    /// <summary>
    ///     Settles the heap before a RUN of samples. It used to sit inside
    ///     <see cref="Measure" />, i.e. once per sample, and that — not layout —
    ///     is where <c>Scenario_06</c>'s runtime went: two blocking gen-2
    ///     collections plus a finalizer drain over the whole booted app heap,
    ///     180 times. Measured on this suite: 180 samples whose STOPWATCHED work
    ///     summed to <b>176.7 ms</b> (avg 0.98, worst 23.3) took ~110 s of wall
    ///     clock, i.e. ~610 ms per sample of quiesce outside the stopwatch.
    ///     <para>
    ///         Hoisting it does not weaken the samples: each loop still starts
    ///         from a settled heap, and a loop's own ~1.5 MB of allocation does
    ///         not fill a gen-0 budget, so a sample absorbs at most a sub-ms
    ///         gen-0 — which is a cost the real UI thread pays too.
    ///     </para>
    /// </summary>
    private static void Quiesce()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>One sample. <see cref="Quiesce" /> is the caller's job, once per
    /// run — never per sample. <c>precise: true</c> stays: its EE suspension is
    /// tens of microseconds (it was never the cost) and the samples here allocate
    /// only single-digit KB, which an imprecise read cannot resolve.
    /// <para>
    ///     <b>Do not settle the dispatcher here — it was tried and it breaks
    ///     <see cref="Scenario_06_Stress_UserInteractions" />.</b> Every switch
    ///     measured here re-arms the app's Background prewarm chains and a
    ///     180-iteration loop that never yields stacks them up unrun: measured
    ///     at <b>62 s</b> of backlog handed to the dispatch teardown, which is
    ///     the bulk of what <c>HeadlessSessionFixture.Dispatch</c>'s drain now
    ///     pays. Running them per sample (outside the stopwatch) does cut the
    ///     class from ~7 min to ~4 — but it also REALIZES every hidden surface
    ///     inside the stress loop, and Scenario_06's leak guard counts visuals:
    ///     its warm-up baseline is taken with that work still queued, so the
    ///     tree "grew" 1,166 → 16,022 against a tolerance of 400 and the guard
    ///     failed on legitimate prewarm. Settling BOTH counts might fix that,
    ///     but it changes what the only real assertion in this class means —
    ///     so it needs its own measured pass, not a drive-by.
    /// </para></summary>
    private static Measurement Measure(string label, Action action)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        var after = GC.GetTotalAllocatedBytes(precise: true);
        return new Measurement(label, sw.Elapsed.TotalMilliseconds, after - before);
    }

    private void Report(string scenario, IEnumerable<Measurement> ms)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {scenario} ==");
        sb.AppendLine($"  {"label",-55} {"time",10}    {"alloc",10}");
        foreach (var m in ms)
            sb.AppendLine($"  {m.Label,-55} {m.Ms,8:0.00} ms {m.AllocBytes / 1024.0,9:0.0} KB");
        var text = sb.ToString();
        _out.WriteLine(text);
        // Mirror to a file the user can open after the run — ITestOutputHelper
        // is sometimes hidden behind verbosity flags.
        var dir = Path.Combine(AppContext.BaseDirectory, "perf-reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{scenario}.txt");
        File.WriteAllText(path, text);
    }

    private static bool IsInteractableComboBox(ComboBox cb) =>
        cb.ItemCount > 0 &&
        cb.IsEffectivelyVisible &&
        cb.Bounds.Width > 0 &&
        cb.Bounds.Height > 0;

    private static void EnsureMultiArrangementProject(ChartEditorViewModel editor)
    {
        if (editor.Arrangements.Count > 1) return;

        var project = new ChartProject();
        foreach (var name in new[] { "Lead", "Bass", "Combo" })
        {
            project.Arrangements.Add(new Arrangement
            {
                Name = name,
                ArrangementType = name == "Bass" ? ArrangementType.Bass : ArrangementType.Lead,
                Levels = { new ChartLevel { DifficultyIndex = 0 } }
            });
        }

        editor.ApplyProject(project);
    }

    private static Window? _window;
    private static Application? _windowApp;

    /// <summary>
    ///     The shell every scenario measures against — ONE per dispatch.
    ///     <para>
    ///         <b>The lifetime branch below never matches here.</b> The headless
    ///         session has no <see cref="IClassicDesktopStyleApplicationLifetime" />
    ///         at all, so before the cache every call built a whole new
    ///         <c>MainWindow</c> — three of them inside
    ///         <see cref="Scenario_01_MainWindow_Construction" />, whose second
    ///         line is literally labelled "(warm)" and was therefore reporting a
    ///         COLD construction. <c>UI/EscapeKeyRoutingUiPerfSmokeTests</c> hit
    ///         the same thing and fixed it the same way; there it broke keyboard
    ///         routing, here it multiplied the prewarm work left on the
    ///         dispatcher queue by the number of windows.
    ///     </para>
    ///     <para>
    ///         Keyed on <see cref="Application.Current" /> and NOT merely cached:
    ///         the session is <c>AvaloniaTestIsolationLevel.PerTest</c>, so each
    ///         dispatch gets a NEW application and a window from the previous one
    ///         belongs to a torn-down style scope. Reusing across dispatches
    ///         would resolve every <c>DynamicResource</c> against nothing.
    ///     </para>
    /// </summary>
    private static Window EnsureMainWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            if (!lifetime.MainWindow.IsVisible) lifetime.MainWindow.Show();
            return lifetime.MainWindow;
        }

        if (_window != null && ReferenceEquals(_windowApp, Application.Current))
        {
            if (!_window.IsVisible) _window.Show();
            return _window;
        }

        var win = new MainWindow
        {
            DataContext = App.Services?.GetRequiredService<MainWindowViewModel>()
        };
        // Forget it when the dispatch's teardown closes it. Kept past its own
        // dispatch, this static was the root of #250: the released window, its
        // App and container stayed alive for the rest of the run, and Avalonia's
        // Grid shares ONE TempDefinitions array per thread, so a live Grid of
        // this dead window also pinned whichever LATER app laid a Grid out last.
        // HeadlessSessionFixture's StaticVisualLeakGuard fails the dispatch now.
        win.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, win)) return;
            _window = null;
            _windowApp = null;
        };
        win.Show();
        _window = win;
        _windowApp = Application.Current;
        return win;
    }

    [Fact]
    public async Task Scenario_01_MainWindow_Construction()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var ms = new List<Measurement>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Quiesce();
            ms.Add(Measure("EnsureMainWindow + first layout", () =>
            {
                var win = EnsureMainWindow();
                win.UpdateLayout();
            }));
            ms.Add(Measure("EnsureMainWindow (warm)", () => EnsureMainWindow()));
            var win = EnsureMainWindow();
            ms.Add(Measure("UpdateLayout (warm)", () => win.UpdateLayout()));
        }, TestContext.Current.CancellationToken);
        Report("01-mainwindow", ms);
    }

    [Fact]
    public async Task Scenario_02_Inspector_TabSwitch()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var ms = new List<Measurement>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var win = EnsureMainWindow();
            win.UpdateLayout();
            var inspector = App.Services?.GetRequiredService<InspectorViewModel>();
            if (inspector == null) { _out.WriteLine("InspectorViewModel not registered."); return; }
            // Initial layout pass to make sure all inspector tabs are realized.
            for (var i = 0; i < 6; i++)
            {
                inspector.SelectedTabIndex = i;
                win.UpdateLayout();
            }
            // Now measure warm-tab switches: this is the user's "tab switch lag".
            var pattern = Action;
            Quiesce();
            foreach (var i in pattern)
            {
                ms.Add(Measure($"Inspector → tab {i}", () =>
                {
                    inspector.SelectedTabIndex = i;
                    win.UpdateLayout();
                }));
            }
        }, TestContext.Current.CancellationToken);
        Report("02-inspector-tab-switch", ms);
    }

    [Fact]
    public async Task Scenario_03_Settings_Open_TabSwitch_Save()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var ms = new List<Measurement>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var win = EnsureMainWindow();
            win.UpdateLayout();
            var session = App.Services?.GetService<DotCharter.Presentation.Shared.SettingsSession>();
            if (session == null) { _out.WriteLine("SettingsSession not registered."); return; }
            Quiesce();
            // The settings shell loads every tab on the next Background turn.
            ms.Add(Measure("Settings.Open (cold)", () =>
            {
                session.IsOpen = true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Background);
                win.UpdateLayout();
            }));

            // The Settings tab is a ScreenHost showing the settings shell; its rail is the tab switch.
            var host = win.FindControl<ContentControl>("SettingsHost")?.Content as DotCharter.UI.Views.Shell.ScreenHost;
            if (host?.ViewOf(typeof(DotCharter.Presentation.Contracts.Settings.ISettingsShellScreen))?.DataContext
                is not DotCharter.Presentation.Contracts.Settings.ISettingsShellScreen shell)
            {
                _out.WriteLine("The settings shell is not hosted yet.");
                return;
            }

            var rail = shell.Sections.SelectMany(s => s.Items).ToList();
            // Warm cycle so jit/atlas/etc don't pollute switch measurements.
            for (var i = 0; i < 4; i++) { rail[i].SelectCommand.Execute(null); win.UpdateLayout(); }

            Quiesce();
            foreach (var i in new[] { 0, 1, 2, 3, 0 })
            {
                ms.Add(Measure($"Settings → tab {i}", () =>
                {
                    rail[i].SelectCommand.Execute(null);
                    win.UpdateLayout();
                }));
            }

            ms.Add(Measure("Settings.Save (no changes: a no-op)", () =>
            {
                shell.SaveCommand.Execute(null);
                win.UpdateLayout();
            }));
        }, TestContext.Current.CancellationToken);
        Report("03-settings", ms);
    }

    [Fact]
    public async Task Scenario_04_AudioConfig_ApplyTwice()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var ms = new List<Measurement>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            EnsureMainWindow();
            var audioSvc = App.Services?.GetRequiredService<IAudioPlaybackService>();
            var configSvc = App.Services?.GetRequiredService<IConfigurationService>();
            if (audioSvc == null || configSvc == null) { _out.WriteLine("Audio services not registered."); return; }
            var cfg = configSvc.LoadAppConfig();

            Quiesce();
            ms.Add(Measure("ApplyAudioConfiguration (first - cold)", () => audioSvc.ApplyAudioConfiguration(cfg)));
            ms.Add(Measure("ApplyAudioConfiguration (same cfg - should be no-op)", () => audioSvc.ApplyAudioConfiguration(cfg)));
            // Mutate the *delay* (an inert field) and re-apply — should still no-op the engine rebuild.
            cfg.AudioDelayMs += 1;
            ms.Add(Measure("ApplyAudioConfiguration (delay-only change)", () => audioSvc.ApplyAudioConfiguration(cfg)));
            cfg.AudioDelayMs -= 1;
            ms.Add(Measure("ApplyAudioConfiguration (revert delay)", () => audioSvc.ApplyAudioConfiguration(cfg)));
        }, TestContext.Current.CancellationToken);
        Report("04-audio-apply", ms);
    }

    [Fact]
    public async Task Scenario_05_ComboBox_OpenDropdown()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var ms = new List<Measurement>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var win = EnsureMainWindow();
            // Recursively find every ComboBox in the live visual tree.
            var combos = new List<ComboBox>();
            void Walk(Visual v)
            {
                if (v is ComboBox cb && IsInteractableComboBox(cb)) combos.Add(cb);
                foreach (var c in v.GetVisualChildren()) Walk(c);
            }
            Walk(win);
            _out.WriteLine($"Found {combos.Count} visible ComboBoxes in main window.");

            // NOT `IsDropDownOpen = true`. A ComboBox popup is a real popup, and
            // docs/agents/ui/rendering-perf-detail.md states the rule: never open one under
            // Avalonia.Headless. Doing so leaves a dispatcher frame behind, so
            // the session's per-dispatch teardown throws
            // "InvalidProgramException: You've caused dispatcher loop" instead of
            // resetting — and because all nine [Collection("HeadlessUI")] classes
            // SHARE one static session, every test scheduled afterwards then
            // failed or hung. Two profilers that assert nothing were taking ~80
            // real XAML guards down with them, which is most of the reason this
            // whole suite sits outside the default gate.
            //
            // The realizable cost is measured without the popup: laying the
            // ComboBox itself out is what the virtualization rules care about.
            Quiesce();
            for (var i = 0; i < Math.Min(8, combos.Count); i++)
            {
                var cb = combos[i];
                ms.Add(Measure($"ComboBox[{i}] (items={cb.ItemCount}) layout",
                    () => { cb.InvalidateMeasure(); win.UpdateLayout(); }));
            }
        }, TestContext.Current.CancellationToken);
        Report("05-combobox-open", ms);
    }

    [Fact]
    public async Task Scenario_06_Stress_UserInteractions()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var inspectorMs = new List<Measurement>();
        var visualsAfterWarmup = 0;
        var visualsAtEnd = 0;
        var arrangementMs = new List<Measurement>();
        var comboMs = new List<Measurement>();

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var win = EnsureMainWindow();
            win.UpdateLayout();
            var shell = App.Services?.GetRequiredService<MainWindowViewModel>();
            var inspector = App.Services?.GetRequiredService<InspectorViewModel>();
            if (shell == null || inspector == null)
            {
                _out.WriteLine("Shell or inspector VM not registered.");
                return;
            }

            EnsureMultiArrangementProject(shell.ChartEditor);
            win.UpdateLayout();

            for (var i = 0; i < 6; i++)
            {
                inspector.SelectedTabIndex = i;
                win.UpdateLayout();
            }

            // Counted AFTER the warm-up so one-time realization of the six tabs
            // is not mistaken for growth; the stress loop below must not add more.
            visualsAfterWarmup = win.GetVisualDescendants().Count();

            Quiesce();
            for (var i = 0; i < 120; i++)
            {
                var tab = i % 6;
                inspectorMs.Add(Measure($"Inspector stress tab {tab}", () =>
                {
                    inspector.SelectedTabIndex = tab;
                    win.UpdateLayout();
                }));
            }

            var arrangements = shell.ChartEditor.Arrangements;
            if (arrangements.Count > 1)
            {
                Quiesce();
                for (var i = 0; i < 60; i++)
                {
                    var arr = arrangements[i % arrangements.Count];
                    arrangementMs.Add(Measure($"Arrangement stress {arr.Name}", () =>
                    {
                        shell.ChartEditor.ActiveArrangement = arr;
                        win.UpdateLayout();
                    }));
                }
            }

            var combos = new List<ComboBox>();
            void Walk(Visual v)
            {
                if (v is ComboBox cb && IsInteractableComboBox(cb)) combos.Add(cb);
                foreach (var c in v.GetVisualChildren()) Walk(c);
            }
            Walk(win);

            // No real popups — see the note in Scenario_05. This one opened 66 of
            // them, and it is the specific test that poisoned the shared session.
            foreach (var cb in combos.Take(6))
            {
                cb.InvalidateMeasure();
                win.UpdateLayout();
            }

            Quiesce();
            for (var i = 0; i < 60 && combos.Count > 0; i++)
            {
                var cb = combos[i % Math.Min(6, combos.Count)];
                comboMs.Add(Measure($"Combo stress items={cb.ItemCount}", () =>
                {
                    cb.InvalidateMeasure();
                    win.UpdateLayout();
                }));
            }

            visualsAtEnd = win.GetVisualDescendants().Count();
        }, TestContext.Current.CancellationToken);

        var all = inspectorMs.Concat(arrangementMs).Concat(comboMs).ToList();
        Report("06-stress-user-interactions", all);

        // NOT a wall-clock budget. The root AGENTS.md rule is explicit — "never a
        // wall-clock assertion; assert STRUCTURE and LOG the timing instead" —
        // and this test is why. It used to assert an 8 ms average, and once the
        // session-poisoning bug was fixed and it actually ran its 180 iterations
        // to completion for the first time, it failed and took NINE MINUTES doing
        // it. A budget that has never once been evaluated is not a guard.
        //
        // The timings are reported above for a human to read. What is asserted
        // here is what a STRESS test is uniquely able to prove and a stopwatch is
        // not: that repeating an interaction does not GROW anything. A leaked
        // visual per tab switch is invisible in an average and obvious here.
        Log("Inspector stress", inspectorMs);
        Log("Arrangement stress", arrangementMs);
        Log("ComboBox stress", comboMs);

        Assert.NotEmpty(all);
        Assert.True(visualsAfterWarmup > 0, "the shell produced no visual tree to stress");
        Assert.True(visualsAtEnd <= visualsAfterWarmup + VisualGrowthTolerance,
            $"the visual tree GREW across the stress run: {visualsAfterWarmup} → {visualsAtEnd} " +
            $"(tolerance {VisualGrowthTolerance}). Something is leaking one or more visuals per " +
            "interaction — that is unbounded, and an averaged millisecond budget cannot see it.");
    }

    /// <summary>
    ///     Slack for surfaces that legitimately realize late (virtualized rows
    ///     materializing, a deferred host attaching). A LEAK is per-iteration, so
    ///     it clears any constant like this by two orders of magnitude.
    /// </summary>
    private const int VisualGrowthTolerance = 400;

    /// <summary>Reports timings without asserting on them.</summary>
    private void Log(string scenario, IReadOnlyList<Measurement> ms)
    {
        if (ms.Count == 0) return;
        _out.WriteLine($"{scenario}: n={ms.Count} avg={ms.Average(m => m.Ms):0.00} ms " +
                       $"worst={ms.Max(m => m.Ms):0.00} ms");
    }
}
