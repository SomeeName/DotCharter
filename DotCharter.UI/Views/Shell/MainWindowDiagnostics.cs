// The shell's startup diagnostics logging and the env-gated UI autopilots
// (DOTCHARTER_UI_AUTOPILOT) used for scripted perf/verification runs.
//
// Extracted from the MainWindow.Diagnostics partial: it declared NO private
// field of its own and touched exactly ONE belonging to a sibling
// (`_timelineWindow`, in .Docking) — a collaborator in hiding rather than a
// page break. That single tie is now the window's `TimelinePopOutWindow`
// property. Owned by MainWindow, constructed in its ctor.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.Services.Session;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The shell's startup diagnostics and env-gated autopilots. Every service
///     arrives from the window as a <see cref="Func{TResult}" /> and is resolved at
///     the moment a diagnostic runs, so constructing this builds nothing (the
///     audio service in particular must not be constructed by the window).
/// </summary>
internal sealed class MainWindowDiagnostics(
    MainWindow window,
    Func<IAppLogger?> logger,
    Func<IProjectSessionService?> session,
    Func<IAudioPlaybackService?> audio,
    Func<IHighwayEditingService?> editing,
    Func<IHistoryService?> history,
    Func<IProjectDocumentService?> documents,
    Func<ProjectState?> projectState)
{
    private readonly MainWindow _window = window;
    private readonly Func<IAppLogger?> _logger = logger;
    private readonly Func<IProjectSessionService?> _session = session;
    private readonly Func<IAudioPlaybackService?> _audio = audio;
    private readonly Func<IHighwayEditingService?> _editing = editing;
    private readonly Func<IHistoryService?> _history = history;
    private readonly Func<IProjectDocumentService?> _documents = documents;
    private readonly Func<ProjectState?> _projectState = projectState;

    /// <summary>
    ///     Reports whether this window's platform host can present transparent
    ///     pixels, because that ALSO decides how every popup looks.
    ///     <para>
    ///         ComboBox dropdowns, flyouts, menus and tooltips each live in their own
    ///         Win32 popup HWND (<c>OverlayPopups</c> is off — see the airspace rule),
    ///         and they all inherit the process-wide composition mode. When that mode
    ///         cannot do per-pixel alpha, <c>ActualTransparencyLevel</c> resolves to
    ///         <see cref="WindowTransparencyLevel.None" /> and every pixel the theme
    ///         leaves transparent — the gutter outside a rounded
    ///         <c>FlyoutPresenter</c>, the ComboBox popup border's margin, a
    ///         <c>BoxShadow</c>'s feather — is presented as flat BLACK.
    ///     </para>
    ///     <para>
    ///         That shipped as a visible defect twice, both times diagnosed only from
    ///         screenshots, so the state is now written to the log at startup: an
    ///         opt-in <c>"Win32CompositionMode": "LowLatencyDxgiSwapChain"</c> and a
    ///         silent fallback to a redirection surface are otherwise indistinguishable
    ///         from a theme bug. Warning-level when opaque, Info when not.
    ///     </para>
    /// </summary>
    internal void LogPopupHostTransparency()
    {
        var logger = _logger();
        if (logger is null) return;

        if (_window.ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            logger.Warning(AppLogCategory.Rendering,
                "Window transparency is None — the Win32 composition mode cannot present " +
                "per-pixel alpha, so popup HWNDs (ComboBox dropdowns, flyouts, menus, " +
                "tooltips) will render their rounded-corner/shadow gutters as opaque black. " +
                "Check appconfig \"Win32CompositionMode\".");
        }
        else
        {
            logger.Info(AppLogCategory.Rendering,
                $"Window transparency level: {_window.ActualTransparencyLevel} (popup hosts can blend).");
        }
    }

    /// <summary>
    ///     Env-gated UI autopilot for scripted verification (same family as
    ///     the <c>DOTCHARTER_3D_STRESS</c>/<c>DOTCHARTER_3D_STATS_LOG</c>
    ///     benchmark hooks — inert unless the variable is set).
    ///     <c>DOTCHARTER_UI_AUTOPILOT=float-timeline</c>: pop the chart
    ///     timeline out 8 s after load and close the float window 6 s later —
    ///     drives the v0.33 composed-2D backend's detach/re-attach seam
    ///     end-to-end without focus-stealing scripted input.
    /// </summary>
    internal void ArmUiAutopilot()
    {
        switch (Environment.GetEnvironmentVariable("DOTCHARTER_UI_AUTOPILOT"))
        {
            case "float-timeline":
                DispatcherTimer.RunOnce(() =>
                {
                    _window.PopOutTimelineWindow();
                    DispatcherTimer.RunOnce(() => _window.TimelinePopOutWindow?.Close(), TimeSpan.FromSeconds(6));
                }, TimeSpan.FromSeconds(8));
                break;
            case "perf":
                ArmPerfAutopilot();
                break;
        }
    }

    /// <summary>
    ///     <c>DOTCHARTER_UI_AUTOPILOT=perf</c>: scripted perf-measurement run
    ///     (pairs with the <c>DOTCHARTER_2D_STATS_LOG</c> /
    ///     <c>DOTCHARTER_3D_STATS_LOG</c> hooks). Optionally positions the
    ///     window (<c>DOTCHARTER_AUTOPILOT_BOUNDS=x,y,w,h</c> — agents must
    ///     keep test windows off the owner's primary monitor), loads
    ///     <c>DOTCHARTER_AUTOPILOT_PROJECT</c>, plays it MUTED for
    ///     <c>DOTCHARTER_AUTOPILOT_RUN_S</c> seconds (default 30) and closes
    ///     the window (the normal terminate path). <c>DOTCHARTER_AUTOPILOT_SONG</c>
    ///     opens a library <c>.psarc</c> in the editor instead of a project, and
    ///     <c>DOTCHARTER_AUTOPILOT_HOLD=1</c> stops after the load: nothing plays and
    /// the window stays open (the memory measurement). Inert without the mode
    ///     variable.
    /// </summary>
    private void ArmPerfAutopilot()
    {
        var boundsSpec = Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_BOUNDS");
        if (!string.IsNullOrEmpty(boundsSpec))
        {
            var parts = boundsSpec.Split(',');
            if (parts.Length == 4 &&
                int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) &&
                int.TryParse(parts[2], out var w) && int.TryParse(parts[3], out var h))
            {
                _window.WindowState = WindowState.Normal;
                _window.Position = new PixelPoint(x, y);
                _window.Width = w;
                _window.Height = h;
            }
        }

        var projectPath = Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_PROJECT");
        var runSeconds = int.TryParse(Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_RUN_S"),
            out var s) && s > 0 ? s : 30;

        DispatcherTimer.RunOnce(async () =>
        {
            var logger = _logger();
            try
            {
                if (_window.DataContext is not MainWindowViewModel vm) return;
                // The game menu is the default front door (StartInPlayMode) —
                // a perf run measures the EDITOR's renderers, so switch there.
                vm.SelectedMainTabIndex = 0;
                // A perf run measures rendering, not listening — mute so a
                // scripted run never blasts audio at whoever owns the machine.
                var audio = _audio();
                audio?.Volume = 0;

                var session = _session();
                if (session != null && !string.IsNullOrEmpty(projectPath))
                    await session.LoadFile(projectPath);

                // DOTCHARTER_AUTOPILOT_SONG: a LIBRARY song (a .psarc under the
                // configured library), opened through the browser's own load path
                // as a user's "open in editor" does — the memory measurement's
                // "chart view with one song" state.
                var songPath = Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_SONG");
                if (!string.IsNullOrEmpty(songPath)) await LoadLibrarySong(vm, songPath, logger);
                audio?.Volume = 0;

                await Task.Delay(TimeSpan.FromSeconds(2));

                // Optional editing-GC probe: commits N real note edits through
                // the full editing pipeline (history + spacing enforcement +
                // damage-driven renders) with the transport STOPPED, logs the
                // allocation/GC cost per edit, then undoes them all. This is
                // the measurement seam for the "GC kicks in way too often
                // while editing" report.
                if (Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_EDIT") == "1")
                    await RunEditGcProbe(logger);

                // Optional shell-switch probe: every top-level switch a user makes,
                // timed to the drawn frame, after the startup prewarm has drained.
                // "early" starts while the drain still runs and notes each step as input, as a click does.
                var switches = Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_SWITCHES");
                if (switches is "1" or "early")
                {
                    if (switches == "1") await Task.Delay(TimeSpan.FromSeconds(8));
                    await ShellSwitchProbe.RunAsync(_window, vm, logger, early: switches == "early");
                    vm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                // DOTCHARTER_AUTOPILOT_STYLE_CENSUS=1: the style value frames every realized
                // control carries, per rule, once the prewarm has drained.
                if (Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_STYLE_CENSUS") == "1")
                {
                    await Task.WhenAny(_window.HiddenSurfacesDrained, Task.Delay(TimeSpan.FromSeconds(60)));
                    logger?.Info(AppLogCategory.Rendering,
                        "[StyleCensus] " + StyleFrameCensus.Format(StyleFrameCensus.Take(_window), 120));
                }

                // DOTCHARTER_AUTOPILOT_HOLD=1: stop after the load (and the switch probe,
                // when asked) — nothing plays and the window stays open until the
                // driver ends the process.
                if (Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_HOLD") == "1")
                {
                    logger?.Info(AppLogCategory.Rendering, "[PerfAutopilot] holding (loaded, idle).");
                    return;
                }

                // Optional popover-latency probe: every toolbar popover,
                // opened repeatedly, stopped and then playing.
                var popoverProbe = Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_POPOVERS") == "1"
                    ? _window.GetVisualDescendants().OfType<ToolbarView>().FirstOrDefault()
                    : null;
                if (popoverProbe != null) await PopoverLatencyProbe.RunAsync(popoverProbe, "stopped", logger);

                logger?.Info(AppLogCategory.Rendering,
                    $"[PerfAutopilot] playback starting ({runSeconds}s run).");
                vm.Transport.PlayCommand.Execute(null);

                if (popoverProbe != null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await PopoverLatencyProbe.RunAsync(popoverProbe, "playing", logger);
                }

                // Optional pop-out probe (composition-mode verification): float
                // the timeline mid-run (exercises the composed backend's
                // detach/re-attach against a NEW TopLevel), close it 6 s later.
                if (Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_FLOAT") == "1")
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    _window.PopOutTimelineWindow();
                    if (_window.TimelinePopOutWindow is { } floated)
                        floated.Position = new PixelPoint(_window.Position.X + 150, _window.Position.Y + 120);
                    await Task.Delay(TimeSpan.FromSeconds(6));
                    _window.TimelinePopOutWindow?.Close();
                }

                // Optional popup-surface probe (composition-mode verification):
                // opens the first interactable ComboBox dropdown mid-run so an
                // external capture can check popup HWND rendering, then closes it.
                if (Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_POPUP") == "1")
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    ComboBox? probe = null;
                    void FindCombo(Visual v)
                    {
                        if (probe != null) return;
                        if (v is ComboBox { ItemCount: > 0, IsEffectivelyVisible: true } cb &&
                            cb.Bounds.Width > 0)
                        {
                            probe = cb;
                            return;
                        }

                        foreach (var child in v.GetVisualChildren()) FindCombo(child);
                    }

                    FindCombo(_window);
                    if (probe != null)
                    {
                        logger?.Info(AppLogCategory.Rendering,
                            $"[PerfAutopilot] opening popup probe ({probe.ItemCount} items).");
                        probe.IsDropDownOpen = true;
                        await Task.Delay(TimeSpan.FromSeconds(4));
                        probe.IsDropDownOpen = false;
                    }
                    else
                    {
                        logger?.Info(AppLogCategory.Rendering, "[PerfAutopilot] no ComboBox found for popup probe.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(runSeconds));
                vm.Transport.PauseCommand.Execute(null);
                logger?.Info(AppLogCategory.Rendering, "[PerfAutopilot] run complete, closing.");
                await Task.Delay(TimeSpan.FromSeconds(1));
                _window.Close();
            }
            catch (Exception ex)
            {
                logger?.Error(AppLogCategory.Rendering, "[PerfAutopilot] run failed.", ex);
            }
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>Perf autopilot: scans the configured library, opens the song whose
    /// archive is <paramref name="psarcPath" /> in the editor and shows the chart.</summary>
    private static async Task LoadLibrarySong(MainWindowViewModel vm, string psarcPath, IAppLogger? logger)
    {
        await vm.SongBrowser.Activate();
        // Activate returns at once when the startup scan of the same folder is already
        // running (measured opens through here), so wait for the song to be listed.
        ViewModels.Panels.PsarcSongListItem? item = null;
        for (var waited = 0; item == null && waited < 60_000; waited += 250)
        {
            item = vm.SongBrowser.AllSongs.FirstOrDefault(s =>
                string.Equals(s.Song.PackagePath, psarcPath, StringComparison.OrdinalIgnoreCase));
            if (item == null) await Task.Delay(250);
        }

        if (item == null)
        {
            logger?.Warning(AppLogCategory.Rendering,
                $"[PerfAutopilot] '{psarcPath}' is not in the library ({vm.SongBrowser.AllSongs.Count} songs).");
            return;
        }

        // DOTCHARTER_AUTOPILOT_SONG_REPEAT=N opens it N times: the first open pays the
        // JIT and the cold caches, the rest are the repeat opens.
        var opens = int.TryParse(Environment.GetEnvironmentVariable("DOTCHARTER_AUTOPILOT_SONG_REPEAT"), out var n)
            ? Math.Clamp(n, 1, 20)
            : 1;
        for (var open = 0; open < opens; open++)
        {
            if (open > 0) await Task.Delay(TimeSpan.FromSeconds(1));
            var loaded = await vm.SongBrowser.LoadSongItem(item);
            vm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
            logger?.Info(AppLogCategory.Rendering,
                $"[PerfAutopilot] library song '{item.Title}' loaded={loaded} ({vm.SongBrowser.AllSongs.Count} songs indexed).");
        }
    }

    /// <summary>Perf-autopilot editing-GC probe: 100 committed note edits
    /// through the REAL pipeline (undo history, spacing enforcement, wake-
    /// driven 2D/3D renders), 10 ms apart (a fast human editing pace), then
    /// 100 undos. Logs process-wide + UI-thread allocations and GC counts
    /// per phase as a [EditGC] line.</summary>
    private async Task RunEditGcProbe(IAppLogger? logger)
    {
        const int editCount = 100;
        var editing = _editing();
        var history = _history();
        if (editing == null || history == null) return;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        var uiAlloc0 = GC.GetAllocatedBytesForCurrentThread();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < editCount; i++)
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(40 + i / 4, new Fraction(i % 4, 4)),
                StringIndex = i % 6,
                Fret = 5 + i % 7
            };
            editing.CommitHandShapeDragAndDrop(
                [], [note],
                [], [],
                [], [],
                [], []);
            await Task.Delay(10);
        }

        sw.Stop();
        var editMs = sw.ElapsedMilliseconds;
        var editAllocMb = (GC.GetTotalAllocatedBytes(true) - alloc0) / 1_000_000.0;
        var editUiAllocMb = (GC.GetAllocatedBytesForCurrentThread() - uiAlloc0) / 1_000_000.0;
        int e0 = GC.CollectionCount(0) - g0, e1 = GC.CollectionCount(1) - g1, e2 = GC.CollectionCount(2) - g2;

        var undoSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < editCount; i++) history.Undo();
        undoSw.Stop();

        logger?.Info(AppLogCategory.Rendering,
            $"[EditGC] {editCount} commits in {editMs} ms · alloc {editAllocMb:F1} MB total" +
            $" ({editAllocMb * 1000.0 / editCount:F0} KB/edit; UI thread {editUiAllocMb * 1000.0 / editCount:F0} KB/edit)" +
            $" · GC +{e0}/+{e1}/+{e2} (gen0/1/2) · undo x{editCount} {undoSw.ElapsedMilliseconds} ms");

        // Cost of ONE crash-recovery-style whole-project snapshot (assemble +
        // deep clone, both on the UI thread — exactly what the 60 s recovery
        // timer does while a session is dirty). This is the "giant clone
        // landing mid-frame" suspect for the 2D max-frame spikes.
        var docService = _documents();
        var projectState = _projectState();
        if (docService != null && projectState?.ActiveProject is { } project &&
            _window.DataContext is MainWindowViewModel shell)
        {
            var cloneAlloc0 = GC.GetAllocatedBytesForCurrentThread();
            var cloneSw = System.Diagnostics.Stopwatch.StartNew();
            var assembled = docService.AssembleProject(project, shell.Transport.Bpm,
                projectState.CurrentAudioFileName, shell.ChartEditor.LyricPhrases);
            var assembleMs = cloneSw.Elapsed.TotalMilliseconds;
            var snapshot = Core.Utilities.ChartProjectCloner.Clone(assembled);
            cloneSw.Stop();
            var cloneAllocMb = (GC.GetAllocatedBytesForCurrentThread() - cloneAlloc0) / 1_000_000.0;
            logger?.Info(AppLogCategory.Rendering,
                $"[CloneStats] assemble {assembleMs:F1} ms · assemble+clone {cloneSw.Elapsed.TotalMilliseconds:F1} ms" +
                $" · alloc {cloneAllocMb:F1} MB · arrangements {snapshot.Arrangements.Count}");
        }
    }
}
