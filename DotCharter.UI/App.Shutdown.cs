// The exit path: the IsExiting flag, the hard TerminateNow shutdown, the clean
// out-of-process-VST shutdown and its pinned teardown order, and the two kernel32
// imports they terminate the process with.

using DotCharter.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Services.Infrastructure.Recovery;

namespace DotCharter.UI;

public partial class App
{
    // Set as the very first thing on the X-close / Exit path. Used by
    // MainWindow and other shutdown sites to short-circuit destructive
    // teardown (VST plugin unload in particular is a frequent crash source
    // — AGPF / BIAS / Helix all access-violate during their unload path
    // on some Windows builds). With this flag set we just flush logs +
    // TerminateProcess; the OS reclaims memory and handles.
    public static bool IsExiting;

    /// <summary>
    /// Final shutdown: persist config, flush the log, then
    /// <c>TerminateProcess</c> so we never enter the CRT exit /
    /// DLL-detach path (which on Windows fast-fails inside ucrtbase or
    /// inside a misbehaving VST3 plugin's static destructors).
    /// Idempotent and safe to call from multiple shutdown sites
    /// (MainWindow.OnClosing, desktop.Exit, …).
    /// </summary>
    public static void TerminateNow()
    {
        if (Interlocked.Exchange(ref _terminateOnce, 1) != 0) return;
        IsExiting = true;
        // Same reason as the usage flush below: this path TERMINATES the
        // process, so ProjectSessionService.Dispose — the only other caller of
        // NotifyCleanShutdown — never runs, and every ordinary close therefore
        // left its snapshot behind as false crash evidence. Reaching here means
        // the unsaved-changes gate in MainWindow.OnClosing already let the close
        // proceed (saved, or explicitly discarded), so the snapshot describes
        // nothing the user still wants. Runs BEFORE the log flush so its own
        // line reaches the file.
        try { (Services?.GetService<ICrashRecoveryService>())?.NotifyCleanShutdown(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Recovery clear failed: {ex.Message}"); }
        // Config saves are COALESCED (JsonConfigurationService arms a debounced
        // background write instead of paying fsync on the UI thread), and this
        // path TerminateProcess-es — so without this line every setting changed
        // inside the last debounce window dies with the process. Same reason as
        // the usage flush below: Dispose never runs here.
        try { (Services?.GetService<IConfigurationService>())?.Flush(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Config flush failed: {ex.Message}"); }
        // The chart synth's instrument picks save on their own queue; a pick
        // made just before closing would die with the process.
        FlushChartSynthInstruments(_chartSynthInstruments, Services?.GetService<IAppLogger>());
        try { (Services?.GetService<IAppLogger>())?.Flush(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Flush failed: {ex.Message}"); }
        // This path TERMINATES the process, so it is the only place the session's
        // usage counts can still reach disk — Dispose never runs.
        try { (Services?.GetService<IUsageTracker>())?.Flush(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Usage flush failed: {ex.Message}"); }
        try { TerminateProcess(GetCurrentProcess(), 0); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] TerminateProcess failed: {ex.Message}"); }
        // If TerminateProcess somehow fails (e.g. when we run inside the
        // test harness with elevated security), fall back to Environment.Exit.
        try { Environment.Exit(0); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Environment.Exit failed: {ex.Message}"); }
    }

    private static int _terminateOnce;

    /// <summary>The chart synth's instrument coordinator once it has STARTED: the
    /// exit paths flush its queued saves and never construct it just to
    /// flush, so this is null when it never ran.</summary>
    private static DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator? _chartSynthInstruments;

    /// <summary>How long an exit path waits for queued instrument saves: a bound,
    /// because a stuck disk must not hold the closing window open.</summary>
    private static readonly TimeSpan ShutdownSaveFlushCap = TimeSpan.FromSeconds(3);

    /// <summary>Flushes the instrument picks' save queue with <see cref="ShutdownSaveFlushCap" />,
    /// and SAYS so in the log when it times out or throws (a pick may be lost); both
    /// exit paths flush the logger after this.</summary>
    private static void FlushChartSynthInstruments(
        DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator? instruments, IAppLogger? logger)
    {
        if (instruments == null) return;
        try
        {
            if (!instruments.Flush(ShutdownSaveFlushCap))
            {
                logger?.Warning(AppLogCategory.Audio,
                    $"Shutdown: chart synth instrument saves did not finish within {ShutdownSaveFlushCap.TotalSeconds:0} s; the last pick may be lost.");
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, "Shutdown: flushing the chart synth instrument saves failed.", ex);
        }
    }
    private static int _cleanShutdown;

    /// <summary>
    /// Decide the shutdown strategy at window-close time. Returns true when
    /// the clean path is safe: the audio service guarantees no third-party
    /// VST code runs in-process (0.12.0 out-of-process hosting), so ordinary
    /// teardown cannot hit the plugin access-violation class TerminateNow
    /// exists to dodge. The caller then lets the normal Avalonia close
    /// proceed and <see cref="ShutdownServices"/> disposes the audio stack.
    /// </summary>
    public static bool TryBeginCleanShutdown()
    {
        try
        {
            var audio = Services?.GetService<IAudioPlaybackService>();
            if (audio is not { SupportsCleanShutdown: true }) return false;
            Interlocked.Exchange(ref _cleanShutdown, 1);
            IsExiting = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Shutdown] Clean-shutdown probe failed: {ex.Message}");
            return false;
        }
    }

    private void ShutdownServices()
    {
        if (Interlocked.CompareExchange(ref _cleanShutdown, 0, 0) == 1)
        {
            // Clean path (out-of-process VST active): dispose the audio
            // service — which quits the sandbox child, disarms the bridge
            // and destroys the JUCE engine with zero third-party plugin
            // code in-process — then flush and exit through the normal CRT
            // path. Proving this path stays crash-free is the exit
            // criterion for deleting the TerminateNow hack entirely.
            if (Interlocked.Exchange(ref _terminateOnce, 1) != 0) return;
            IsExiting = true;
            // Resolve everything BEFORE the container goes away — GetService on
            // a disposed ServiceProvider throws ObjectDisposedException.
            var logger = Services?.GetService<IAppLogger>();
            var config = Services?.GetService<IConfigurationService>();
            var instruments = _chartSynthInstruments;
            RunCleanShutdown(
                () =>
                {
                    config?.Flush();
                    FlushChartSynthInstruments(instruments, logger);
                },
                Services?.GetService<IAudioPlaybackService>(),
                () => logger?.Flush(),
                _serviceProvider);
            return;
        }

        // Legacy path: in-proc plugins may be live. Avalonia fires
        // desktop.Exit after the main window finishes closing; by now
        // MainWindow.OnClosing has already requested termination — this is
        // just a defence in depth.
        TerminateNow();
    }

    /// <summary>
    /// The clean-shutdown teardown sequence, in the one order that is correct.
    /// Extracted from <see cref="ShutdownServices" /> so the ORDER can be
    /// pinned by a test (<c>CleanShutdownSequenceTests</c>) — the branch itself
    /// only runs on a real app exit under out-of-process VST, which no headless
    /// test can drive.
    ///
    /// **This sequence is for the clean path ONLY.** The legacy path
    /// <c>TerminateProcess</c>es while in-proc VST3 plugins are still loaded,
    /// and running managed teardown there is precisely the access-violation
    /// class <see cref="TerminateNow" /> exists to dodge
    /// (<c>MainWindow.OnClosing</c> calls it deliberately). On the clean path
    /// there is no third-party plugin code in-process and the process leaves
    /// through the normal CRT path — where, until now, the container was NEVER
    /// disposed: <c>_serviceProvider</c> was assigned in
    /// <c>OnFrameworkInitializationCompleted</c> and read nowhere. What
    /// silently never ran: <c>SqliteLibraryIndex</c>'s connection (leaving
    /// <c>-wal</c>/<c>-shm</c> files and an uncheckpointed last transaction
    /// beside the index DB), the LAN update/scoreboard sockets (closed by the
    /// OS rather than by a graceful shutdown packet), the ONNX / native stem
    /// separators' GPU handles, and a fully-implemented
    /// <c>StatusBarViewModel.Dispose()</c> that stops a 1 Hz timer, unhooks the
    /// 250 Hz <c>PositionChanged</c> and frees the cached avatar bitmaps.
    ///
    /// Why this order:
    /// 0. **Config flush first.** Config saves are COALESCED — a
    ///    <c>SaveAppConfig</c> only arms a debounced background write — so
    ///    anything the user changed in the last quarter second is still in
    ///    memory. It runs ahead of the audio teardown because that is the step
    ///    most likely to be slow or to throw, and a lost setting is a bug the
    ///    user sees on the next launch.
    /// 1. **Audio second.** Disposing it quits the sandbox child, disarms the
    ///    bridge and destroys the JUCE engine. The container disposes its
    ///    singletons in REVERSE registration order, and the audio service's
    ///    ordering against <c>IDspChainService</c> is load-bearing — it should
    ///    not depend on where the two happen to sit in
    ///    <c>ConfigureServices</c>.
    /// 2. **Flush second.** Everything logged up to here reaches disk even if
    ///    a service throws on the way out.
    /// 3. **Container last.** <c>AppLogger</c> is one of its singletons and its
    ///    <c>Dispose</c> drains whatever the teardown itself logs.
    ///
    /// Every step is independently guarded: one service throwing must not cost
    /// the others their teardown.
    /// </summary>
    internal static void RunCleanShutdown(
        Action? flushConfig, IDisposable? audio, Action? flushLog, IDisposable? container)
    {
        try { flushConfig?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Config flush failed: {ex.Message}"); }

        try { audio?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Audio dispose failed: {ex.Message}"); }

        try { flushLog?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Flush failed: {ex.Message}"); }

        try { container?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Shutdown] Container dispose failed: {ex.Message}"); }
    }

    /// <summary>
    /// What <see cref="OnFrameworkInitializationCompleted"/> hooked into
    /// PROCESS-wide state (AppDomain / TaskScheduler events, the console
    /// writers, <c>ChartTheme.ThemeChanged</c>), as the actions that undo it.
    /// </summary>
    private readonly List<Action> _processWideUnhooks = [];

    /// <summary>
    /// Releases this App instance for a host that builds MORE THAN ONE App per
    /// process: undoes its process-wide hooks (newest first) and disposes its
    /// container. The product never calls it — it builds one App per process and
    /// leaves through <see cref="ShutdownServices"/>.
    /// <para>
    /// The headless test session is that host: it is
    /// <c>AvaloniaTestIsolationLevel.PerTest</c>, so every dispatch boots a whole
    /// App and abandons it, and nothing ever disposed the container. Each
    /// abandoned App kept its <c>AppLogger</c> alive — two Serilog async pump
    /// THREADS and a one-second flush TIMER, which the process-wide timer queue
    /// roots — and through the logger's subscribers the entire app graph, its
    /// window and compositor included. The headless suite grew ~1 GB a minute,
    /// ~8 threads per test, until the run crawled (26 GB observed) and never
    /// finished. <c>HeadlessSessionFixture.Dispatch</c> calls this after every
    /// dispatch; <c>UI/IsolatedAppTeardownTests</c> pins it.
    /// </para>
    /// </summary>
    internal void ReleaseForIsolatedTeardown()
    {
        var provider = Interlocked.Exchange(ref _serviceProvider, null);
        if (provider != null && ReferenceEquals(Services, provider)) Services = null;
        ReleaseProcessWideState(_processWideUnhooks, provider);
        // The UI's own static events: every subscriber left is a control or a
        // window of THIS app (the caller has already closed the windows it knew).
        global::DotCharter.UI.Services.UiScaleController.DropSubscribersForIsolatedTeardown();
        global::DotCharter.UI.Controls.Themes.ChartTheme.DropSubscribersForIsolatedTeardown();
    }

    /// <summary>
    /// The release itself, static so the default gate can drive it without an
    /// Avalonia application (<c>UI/IsolatedAppTeardownTests</c>): undo the hooks
    /// newest first, then dispose the container — which disposes the logger and
    /// so stops its pump threads and flush timer.
    /// </summary>
    internal static void ReleaseProcessWideState(List<Action> unhooks, IDisposable? container)
    {
        for (var i = unhooks.Count - 1; i >= 0; i--) unhooks[i]();
        unhooks.Clear();
        container?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
