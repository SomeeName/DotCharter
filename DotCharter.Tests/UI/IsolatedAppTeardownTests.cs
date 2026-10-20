using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The headless-UI suite boots a whole <see cref="App" /> per dispatch
///     (<c>AvaloniaTestIsolationLevel.PerTest</c>) and nothing released it. Each
///     abandoned app kept its <c>AppLogger</c> — two Serilog async pump threads and
///     a one-second file-flush timer, which the process-wide timer queue roots —
///     and, through the logger's subscribers, the whole app graph. The suite grew
///     ~1 GB a minute (~8 threads a test), reached 26 GB and never finished.
///     <para>
///         <c>App.ReleaseForIsolatedTeardown</c> now undoes the app's process-wide
///         hooks and disposes its container, and
///         <c>HeadlessSessionFixture.Dispatch</c> calls it after every dispatch.
///         These cases pin both halves in the DEFAULT gate: the release really
///         frees an app across many lifetimes (reachability, not timing), and the
///         fixture and the app still wire it.
///     </para>
/// </summary>
[Collection("SerialDiskIo")] // installs the process-wide console router while it runs
public sealed class IsolatedAppTeardownTests : IDisposable
{
    private const int Lifetimes = 5;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "dotcharter-isolated-teardown-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ReleasingEachAppLifetime_FreesItsLogger_AndRestoresTheConsole()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var loggers = new List<WeakReference>();

        for (var i = 0; i < Lifetimes; i++) loggers.Add(BootAndReleaseOneLifetime(i));

        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Same(originalOut, Console.Out);
        Assert.Same(originalError, Console.Error);
        var alive = loggers.Count(w => w.IsAlive);
        Assert.True(alive == 0,
            $"{alive} of {Lifetimes} released app lifetimes still hold their AppLogger — something process-wide "
            + "(an undisposed container's flush timer / pump thread, or a static event) still roots it. In the "
            + "headless suite every dispatch leaks one whole app this way.");
    }

    /// <summary>
    ///     One app lifetime, shaped like <c>App.OnFrameworkInitializationCompleted</c>:
    ///     the container owns a FILE-logging <see cref="AppLogger" /> (the sink with
    ///     the pump thread and flush timer that rooted the leaked apps), and the
    ///     process-wide hooks are recorded as unhooks exactly as the app records them.
    ///     Separate and not inlined so no local outlives it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference BootAndReleaseOneLifetime(int index)
    {
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                EnableFileLogging = true,
                EnableConsoleLogging = false,
                LogFilePath = Path.Combine(_dir, $"lifetime-{index}.log"),
                EnabledLogCategories = AppLogCategory.Application
            }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IAppLogger>(_ => new AppLogger(config));
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<IAppLogger>();
        logger.Info(AppLogCategory.Application, $"lifetime {index}");

        var unhooks = new List<Action>();
        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
            logger.Info(AppLogCategory.Application, e.ExceptionObject.ToString() ?? "");
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        unhooks.Add(() => AppDomain.CurrentDomain.UnhandledException -= onUnhandled);
        var router = new ConsoleLogRouter(logger);
        router.Install();
        unhooks.Add(router.Uninstall);

        // Disposing the logger joins its pump threads. The cap is a DEADLOCK
        // detector (tests AGENTS.md), never a budget: a release that never
        // returns would otherwise hang the gate instead of failing it.
        var release = Task.Run(() => App.ReleaseProcessWideState(unhooks, provider));
        Assert.True(release.Wait(TimeSpan.FromSeconds(60)),
            "releasing one app lifetime did not return within the 60 s deadlock cap — the container's "
            + "dispose (the logger's pump threads) is blocked.");

        Assert.Empty(unhooks);
        return new WeakReference(logger);
    }

    [Fact]
    public void TheHeadlessFixture_ReleasesTheAppAfterEveryDispatch()
    {
        var code = SourceText.StripComments(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "HeadlessAppFixture.cs")));

        var dispatches = Regex.Matches(code, @"Get\(\)\.Dispatch\(").Count;
        var released = Regex.Matches(code,
            @"finally\s*\{\s*DrainAppBackgroundWork\(\);\s*ReleaseIsolatedApp\(\);\s*\}").Count;

        Assert.True(dispatches >= 4, $"found {dispatches} Dispatch overloads — the lint lost its subject");
        Assert.True(dispatches == released,
            $"{dispatches} Dispatch overloads but {released} release the app after the drain — every "
            + "dispatch boots a whole App, and one that is not released leaks it for the rest of the run.");
        Assert.Contains("(Application.Current as DotCharter.UI.App)?.ReleaseForIsolatedTeardown()", code);
        Assert.Contains("main.ReleaseForIsolatedTeardown()", code);
        Assert.Contains("Window.WindowOpenedEvent.AddClassHandler", code);
        // The running-timer list is bound by reflection; a null binding would
        // silently leave every abandoned app's timers ticking.
        Assert.NotNull(DotCharter.Tests.Perf.HeadlessSessionFixture.DispatcherTimersField);
        Assert.Equal(typeof(List<Avalonia.Threading.DispatcherTimer>),
            DotCharter.Tests.Perf.HeadlessSessionFixture.DispatcherTimersField!.FieldType);
    }

    /// <summary>
    ///     Every process-wide hook the app installs must be recorded for the
    ///     release, or it roots that app's logger (and the graph behind it) for the
    ///     rest of the process.
    /// </summary>
    [Fact]
    public void EveryProcessWideHookTheAppInstalls_IsRecordedForTheRelease()
    {
        var uiRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var code = string.Concat(Directory.EnumerateFiles(uiRoot, "App*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => SourceText.StripComments(File.ReadAllText(f))));

        var hooks = Regex.Matches(code,
                @"(AppDomain\.CurrentDomain\.\w+|TaskScheduler\.\w+|ChartTheme\.ThemeChanged)\s*\+=|\.Install\(\)")
            .Count;
        var recorded = Regex.Matches(code, @"_processWideUnhooks\.Add\(").Count;

        Assert.True(hooks >= 5, $"found {hooks} process-wide hooks in App*.cs — the lint lost its subject");
        Assert.True(hooks == recorded,
            $"App*.cs installs {hooks} process-wide hooks but records {recorded} unhooks in _processWideUnhooks — "
            + "add the matching unhook so ReleaseForIsolatedTeardown can undo it.");
    }
}
