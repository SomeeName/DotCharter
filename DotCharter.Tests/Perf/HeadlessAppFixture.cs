// ReSharper disable InconsistentNaming -- private static fixture gate uses _camelCase consistent with project style.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using DotCharter.Tests.Perf;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI;

[assembly: AvaloniaTestApplication(typeof(HeadlessAppHelper))]

namespace DotCharter.Tests.Perf;

public static class HeadlessSessionFixture
{
    private static readonly Lock Gate = new();
    private static HeadlessUnitTestSession? _session;
    private static string? _isolatedRoot;

    public static HeadlessUnitTestSession Get()
    {
        // The prewarm pacer waits for QUIET input; this fixture's bounded drain
        // runs queued jobs, not timers, so a test that moved the mouse would
        // find every hidden surface cold. Off here, on in the product.
        DotCharter.UI.Views.Shell.PrewarmPacer.QuietWindowMs = 0;
        lock (Gate)
        {
            AppContext.SetSwitch(DotCharter.UI.App.UseNullAudioPlaybackServiceSwitch, true);
            // Keep the fire-and-forget GitHub update check out of test runs.
            AppContext.SetSwitch(DotCharter.Services.Update.UpdateCheckFlow.DisableStartupCheckSwitch, true);
            // …and the stem cache's one-time WEM-key adoption pass off the real caches.
            AppContext.SetSwitch(DotCharter.Services.Audio.StemSeparation.Cache.WemKeyAdoption.DisableStartupPassSwitch, true);
            // …and the tonelab RS_ASIO.ini startup restore off the developer's game folder.
            AppContext.SetSwitch(DotCharter.Tone.Audio.Tonelab.TonelabGameLauncher.DisableStartupRestoreSwitch, true);
            EnsureWindowTracking();
            if (_session == null)
            {
                IsolateConfigRoots();
                // One run, one drain log — appending across runs would make an
                // old worst case read as this run's.
                BestEffortDelete.File(DrainLogPath);
                BestEffortDelete.File(HeadlessUiThreadClaim.LogPath);
                _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessAppHelper));
            }

            return _session;
        }
    }

    // ---------------------------------------------------------------------
    // Dispatch + drain
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Dispatches <paramref name="action" /> onto the session's UI thread and
    ///     then DRAINS the booted app's own background work before the dispatch
    ///     returns. <b>Use this everywhere instead of dispatching on the raw
    ///     session handed back by <see cref="Get" /></b>;
    ///     <c>Infrastructure/HeadlessDrainLintTests</c> fails the default gate on
    ///     a call site that goes around it.
    ///     <para>
    ///         <b>This is the fix for the "residual session race".</b> It was
    ///         never a race. The session is <c>AvaloniaTestIsolationLevel.PerTest</c>,
    ///         so <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c> tears
    ///         the whole application down after EVERY dispatch, and its teardown
    ///         calls <c>Dispatcher.ResetForUnitTests()</c> — which drains the
    ///         dispatcher queue by RUNNING every job still in it and throws
    ///         <c>InvalidProgramException: You've caused dispatcher loop</c> when
    ///         that drain takes longer than a hard-coded <b>5 seconds</b>
    ///         (Avalonia 12.1.1, <c>Dispatcher.Queue.cs</c>).
    ///     </para>
    ///     <para>
    ///         The app leaves plenty in that queue: <c>HiddenSubtreePrewarmer
    ///         .ScheduleDrain</c> and both <c>MenuFlyoutPrewarmer</c> chains post
    ///         ONE realization per dispatcher turn at
    ///         <see cref="DispatcherPriority.Background" />, each turn re-posting
    ///         the next — a self-refilling queue whose total is "250-500 ms" by
    ///         its own doc comment and far more than that in a Debug headless
    ///         build. So a case that shows a window is racing a five-second
    ///         budget it never declared, which is why the victims moved between
    ///         runs, why they were the window-building cases
    ///         (<c>Scenario_0*</c>, <c>ShellConstruction…</c>,
    ///         <c>StartupProfile…</c>), and why CPU contention made it worse.
    ///     </para>
    ///     <para>
    ///         Draining here moves exactly the same work inside the test's own
    ///         (unbounded) time, so Avalonia's teardown finds an empty queue.
    ///         Nothing is skipped and no assertion changes — the drain runs
    ///         AFTER the body.
    ///     </para>
    /// </summary>
    public static Task Dispatch(Action action, CancellationToken cancellationToken) =>
        Get().Dispatch(() =>
        {
            try
            {
                AssertMediaContextBelongsToThisDispatcher();
                action();
            }
            finally { DrainAppBackgroundWork(); ReleaseIsolatedApp(); }
            HeadlessStaticLeakGuard.ThrowIfAnyStaticHoldsAReleasedApp();
        }, cancellationToken);

    /// <inheritdoc cref="Dispatch(Action,CancellationToken)" />
    public static Task<TResult> Dispatch<TResult>(Func<TResult> action, CancellationToken cancellationToken) =>
        Get().Dispatch(() =>
        {
            TResult result;
            try
            {
                AssertMediaContextBelongsToThisDispatcher();
                result = action();
            }
            finally { DrainAppBackgroundWork(); ReleaseIsolatedApp(); }
            HeadlessStaticLeakGuard.ThrowIfAnyStaticHoldsAReleasedApp();
            return result;
        }, cancellationToken);

    /// <inheritdoc cref="Dispatch(Action,CancellationToken)" />
    public static Task Dispatch(Func<Task> action, CancellationToken cancellationToken) =>
        Get().Dispatch(async () =>
        {
            try
            {
                AssertMediaContextBelongsToThisDispatcher();
                await action();
            }
            finally { DrainAppBackgroundWork(); ReleaseIsolatedApp(); }
            HeadlessStaticLeakGuard.ThrowIfAnyStaticHoldsAReleasedApp();
            return 0;
        }, cancellationToken);

    /// <inheritdoc cref="Dispatch(Action,CancellationToken)" />
    public static Task<TResult> Dispatch<TResult>(
        Func<Task<TResult>> action, CancellationToken cancellationToken) =>
        Get().Dispatch(async () =>
        {
            TResult result;
            try
            {
                AssertMediaContextBelongsToThisDispatcher();
                result = await action();
            }
            finally { DrainAppBackgroundWork(); ReleaseIsolatedApp(); }
            HeadlessStaticLeakGuard.ThrowIfAnyStaticHoldsAReleasedApp();
            return result;
        }, cancellationToken);

    /// <summary>
    ///     How long <see cref="DrainAppBackgroundWork" /> will spend running the
    ///     app's queued background work. It is a DEADLOCK DETECTOR, not a
    ///     budget: nothing is graded against it, and overrunning it only leaves
    ///     the remainder to Avalonia's own five-second drain, i.e. exactly the
    ///     behaviour that existed before this method. Generous on purpose —
    ///     the point is to be nowhere near it on a contended box.
    /// </summary>
    /// <remarks>
    ///     It was 60 s and a real run reached <b>61.8 s</b> — at which point the
    ///     token fired, the drain gave up, and the remainder went straight back
    ///     to Avalonia's five seconds, i.e. the flake returned silently. A cap
    ///     that is ever REACHED is a cap doing the wrong job here, so it is set
    ///     far above any observed backlog. Shortening it to "save time" saves
    ///     none: the work runs either way, just under a budget that throws.
    /// </remarks>
    internal static readonly TimeSpan DrainCap = TimeSpan.FromMinutes(5);

    /// <summary>The longest single drain this process has seen, for reporting.</summary>
    internal static TimeSpan LongestDrain { get; private set; }

    /// <summary>
    ///     Avalonia's <c>Dispatcher.RunJobs(DispatcherPriority?, CancellationToken)</c>.
    ///     The public overload takes no token and would therefore be unbounded;
    ///     a hang is a worse failure than the flake this replaces, so the
    ///     cancellable one is bound by reflection.
    ///     <c>Infrastructure/HeadlessDrainLintTests</c> asserts this binding
    ///     still resolves, because a silent null here silently restores the
    ///     five-second race.
    /// </summary>
    internal static readonly MethodInfo? RunJobsWithToken = typeof(Dispatcher).GetMethod(
        "RunJobs",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(DispatcherPriority?), typeof(CancellationToken)],
        modifiers: null);

    /// <summary>
    ///     Avalonia's internal <c>MediaContext</c> — the per-application object
    ///     that runs every layout pass, render and composition commit — and its
    ///     <c>_dispatcher</c> field. Bound by reflection for
    ///     <see cref="AssertMediaContextBelongsToThisDispatcher" />;
    ///     <c>Infrastructure/HeadlessDrainLintTests</c> asserts both still
    ///     resolve, because a null here turns that guard off silently.
    /// </summary>
    internal static readonly Type? MediaContextType =
        typeof(Avalonia.Media.Brush).Assembly.GetType("Avalonia.Media.MediaContext");

    /// <inheritdoc cref="MediaContextType" />
    internal static readonly FieldInfo? MediaContextDispatcherField =
        MediaContextType?.GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary><c>AvaloniaLocator.Current</c> — public in the runtime assembly,
    /// hidden from the reference assembly this project compiles against.</summary>
    internal static readonly PropertyInfo? CurrentLocatorProperty =
        typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")
            ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static string? _lastCase;
    private static string? _orphanedAfter;

    /// <summary>
    ///     Fails the case, with the reason, when the session has been left with
    ///     an ORPHANED <c>MediaContext</c>: one bound to a dispatcher that no
    ///     longer exists.
    ///     <para>
    ///         Every dispatch gets a fresh application, dispatcher and
    ///         <c>MediaContext</c> (<c>PerTest</c> isolation). But
    ///         <c>MediaContext.Instance</c> is created lazily and bound into
    ///         whatever <c>AvaloniaLocator</c> is current — and BETWEEN
    ///         dispatches that is the process-wide ROOT locator, which every
    ///         later application falls back to. So one layout invalidated
    ///         outside a dispatch (measured 2026-09-19: a static
    ///         <c>UiScaleController.Set</c> from a test class's <c>Dispose</c>,
    ///         reaching a <c>MainWindow</c> an earlier class left shown) pins a
    ///         <c>MediaContext</c> to the dead dispatcher, with a render
    ///         operation queued on it that can never run. From then on no
    ///         window in the process lays out, renders or commits a frame: hit
    ///         tests find nothing, a <c>Width</c> change never reaches
    ///         <c>ClientSize</c>, popups open in the wrong place — and each of
    ///         those reads as a bug in whichever unrelated case runs next.
    ///     </para>
    ///     <para>
    ///         This does not repair it (the process is spent); it makes every
    ///         later case say so and name the case that ran just before the
    ///         damage was first seen, instead of failing on a plausible-looking
    ///         assertion.
    ///     </para>
    /// </summary>
    private static void AssertMediaContextBelongsToThisDispatcher()
    {
        var previous = _lastCase;
        _lastCase = TestContext.Current.Test?.TestDisplayName;
        if (MediaContextType is null || MediaContextDispatcherField is null || CurrentLocatorProperty is null) return;

        // GetService, not MediaContext.Instance: the getter CREATES one when
        // there is none, and this check must not change what it observes.
        var locator = CurrentLocatorProperty.GetValue(null);
        var context = locator?.GetType().GetMethod("GetService", [typeof(Type)])?.Invoke(locator, [MediaContextType]);
        if (context is null) return;
        if (ReferenceEquals(MediaContextDispatcherField.GetValue(context), Dispatcher.UIThread)) return;

        _orphanedAfter ??= previous ?? "(no earlier case)";
        throw new InvalidOperationException(
            "The headless session's Avalonia MediaContext belongs to a dispatcher that no longer exists, "
            + "so nothing in this process can lay out, render or hit-test any more. An EARLIER case touched "
            + "a live visual OUTSIDE HeadlessSessionFixture.Dispatch, typically a static event raised from "
            + "a test class's Dispose or constructor (UiScaleController.Set) reaching a MainWindow another "
            + $"class left shown. First seen after: {_orphanedAfter}. "
            + "See docs/agents/testing/perf-harnesses.md.");
    }

    /// <summary>
    ///     Releases the App this dispatch booted — its process-wide hooks and its
    ///     DI container — before Avalonia's per-test teardown abandons it. Without
    ///     this every dispatch leaked a whole app (two Serilog pump threads and a
    ///     flush timer per logger, which root the rest), ~1 GB a minute, until the
    ///     suite crawled at 26 GB and never finished. See
    ///     <c>App.ReleaseForIsolatedTeardown</c>; linted by
    ///     <c>UI/IsolatedAppTeardownTests</c>. Runs AFTER the drain, so nothing the
    ///     app queued finds its container gone.
    /// </summary>
    private static void ReleaseIsolatedApp()
    {
        // Windows FIRST, while the app's services still answer. A window the
        // case left open used to be abandoned, never closed: Avalonia's
        // MediaContext kept it registered as a top level, so every later drain
        // and layout pass also worked for it (drains grew 14 s -> 17 s a case),
        // and its controls never ran OnDetachedFromVisualTree, so static events
        // (ChartTheme.ThemeChanged, UiScaleController.ScaleChanged) rooted its
        // whole tree. A MainWindow's own Close() terminates the process, hence
        // its isolated-teardown close.
        foreach (var window in OpenWindows.ToArray())
        {
            if (!OpenWindows.Contains(window)) continue; // closed with its owner
            if (window is DotCharter.UI.Views.MainWindow main)
            {
                main.ReleaseForIsolatedTeardown();
            }
            else
            {
                window.Close();
                window.Content = null;
            }
        }
        OpenWindows.Clear();
        (Application.Current as DotCharter.UI.App)?.ReleaseForIsolatedTeardown();

        // Last, every DispatcherTimer still RUNNING. The headless dispatcher
        // outlives each app, and so did its timer list: a timer the app started
        // and never stopped (a status-bar tick, a control's animation clock, a
        // shell guard) kept firing into a dead app for the rest of the run —
        // rooting it through its Tick handler and adding its work to every later
        // drain. They all belong to the app being released.
        if (DispatcherTimersField?.GetValue(Dispatcher.UIThread) is List<DispatcherTimer> timers)
            foreach (var timer in timers.ToArray()) timer.Stop();
    }

    /// <summary>
    ///     <c>Dispatcher._timers</c>, the running <see cref="DispatcherTimer" />s,
    ///     for <see cref="ReleaseIsolatedApp" />. <c>UI/IsolatedAppTeardownTests</c>
    ///     asserts the binding still resolves: a null here silently restores the
    ///     leak.
    /// </summary>
    internal static readonly FieldInfo? DispatcherTimersField =
        typeof(Dispatcher).GetField("_timers", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    ///     Every window shown in the current dispatch and not closed, for
    ///     <see cref="ReleaseIsolatedApp" />. Fed by process-wide class handlers on
    ///     <see cref="Avalonia.Controls.Window.WindowOpenedEvent" /> / <see cref="Avalonia.Controls.Window.WindowClosedEvent" />,
    ///     installed once (this process only ever hosts the headless suite).
    /// </summary>
    private static readonly List<Avalonia.Controls.Window> OpenWindows = [];

    private static bool _windowTrackingInstalled;

    private static void EnsureWindowTracking()
    {
        if (_windowTrackingInstalled) return;
        _windowTrackingInstalled = true;
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) => OpenWindows.Add(window));
        Avalonia.Controls.Window.WindowClosedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) => OpenWindows.Remove(window));
    }

    /// <summary>
    ///     Runs everything the app has queued at <see cref="DispatcherPriority.Background" />
    ///     or above until the queue is quiet. Must be called ON the session's
    ///     dispatch thread.
    /// </summary>
    private static void DrainAppBackgroundWork()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(DrainCap);
            if (RunJobsWithToken != null)
            {
                RunJobsWithToken.Invoke(
                    Dispatcher.UIThread, [(DispatcherPriority?)DispatcherPriority.Background, cts.Token]);
            }
            else
            {
                // Binding lost. Better a bounded-by-nothing drain than none at
                // all — but the lint above is what should have caught this.
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            }
        }
        catch
        {
            // Best effort: a job that throws is the test's business, not the
            // drain's, and Avalonia's own teardown drain swallows the same way.
        }

        watch.Stop();
        Interlocked.Increment(ref _drains);
        if (watch.Elapsed <= LongestDrain) return;
        LongestDrain = watch.Elapsed;
        ReportNewWorstDrain(watch.Elapsed);
    }

    private static int _drains;

    /// <summary>
    ///     Where the worst-case drain is written. <c>Console.WriteLine</c> alone
    ///     is not evidence: xunit CAPTURES a passing case's console output and
    ///     does not print it, so "no line appeared" would say nothing about
    ///     whether the drain ran at all. A file says both — that the drain is
    ///     live, and how close the app's background work gets to the 5 s cliff
    ///     Avalonia's own teardown drain throws at.
    ///     <para>
    ///         Only a NEW worst case is written, so a 330-case run leaves a
    ///         handful of lines, not 330.
    ///     </para>
    /// </summary>
    internal static string DrainLogPath =>
        Path.Combine(AppContext.BaseDirectory, "headless-drain.log");

    private static void ReportNewWorstDrain(TimeSpan elapsed)
    {
        var line = $"[headless] drain #{Volatile.Read(ref _drains)} new worst " +
                   $"{elapsed.TotalSeconds:0.000} s " +
                   "(Avalonia's teardown drain throws at 5 s)";
        Console.WriteLine(line);
        try { File.AppendAllText(DrainLogPath, line + Environment.NewLine); }
        catch { /* diagnostics only */ }
    }

    /// <summary>
    ///     Environment variable naming the UI culture the headless session boots
    ///     in. Unset means <c>en-US</c>.
    ///     <para>
    ///         It is an env var and not a constant so the SAME suite can be
    ///         pointed at any language without an edit:
    ///         <c>$env:DOTCHARTER_HEADLESS_CULTURE = "de-DE"</c> and the run
    ///         grades German layout. Three cultures ship resources
    ///         (<c>en-US</c>, <c>de-DE</c>, <c>ja-JP</c>) and the release script
    ///         sweeps the layout classes under all three — see
    ///         <c>tools/release/New-Release.ps1</c>' <c>Invoke-HeadlessUiGate</c>.
    ///     </para>
    /// </summary>
    public const string CultureEnvVar = "DOTCHARTER_HEADLESS_CULTURE";

    /// <summary>
    ///     The culture the session is (or will be) seeded with. Name it in a
    ///     failure message: an overrun of a fixed column means nothing without
    ///     the LANGUAGE that produced it.
    /// </summary>
    public static string SweptCulture
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(CultureEnvVar);
            return string.IsNullOrWhiteSpace(configured) ? "en-US" : configured.Trim();
        }
    }

    /// <summary>
    ///     Points the booted app at an EMPTY config root, once, before the
    ///     session starts.
    ///     <para>
    ///         <b>Without this the suite boots the real app against the
    ///         developer's real <c>%APPDATA%\DotCharter\appconfig.json</c></b> —
    ///         so every case that asserts a first-run state fails on a machine
    ///         that has used the app, and passes on a CI runner that has not.
    ///         Two did for months, and each was filed as a different bug: one
    ///         wanted an online server address empty, the other an online
    ///         notice undismissed — both true of a fresh install and both false
    ///         on the owner's box. (Both cases went with the online backend in
    ///         v0.60; the failure SHAPE is why this claim exists.) It also stops
    ///         the suite WRITING to that file.
    ///     </para>
    ///     <para>
    ///         <see cref="WritablePaths.TryClaimRootsForTesting" /> and not the
    ///         unconditional setter, and it runs in parallel with the two
    ///         <c>CwdSerialized</c> classes that own that setter. Claiming only
    ///         when nobody holds an override means this can never make their
    ///         resolution wrong — at worst it declines and the session boots the
    ///         way it always did.
    ///     </para>
    ///     <para>
    ///         This note used to add "in the DEFAULT gate this fixture is also
    ///         reached (seven <c>DotCharter.Tests/UI/*</c> classes boot the
    ///         session)". <b>That was false</b> — those seven are pure /
    ///         XML-parsing tests that name the fixture only in a comment
    ///         explaining why they avoid it. Nothing in the default gate boots
    ///         this session, which is what makes <see cref="ShutdownAsync" />'s
    ///         unconditional root release safe, and
    ///         <c>Infrastructure/HeadlessCollectionLintTests
    ///         .EveryTestClassThatBootsTheHeadlessSession_IsInTheSerializedCollection</c>
    ///         is what now keeps it true.
    ///     </para>
    /// </summary>
    private static void IsolateConfigRoots()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"DotCharter-headless-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        if (!WritablePaths.TryClaimRootsForTesting(root, root))
        {
            _isolatedRoot = null;
            return;
        }

        _isolatedRoot = root;

        // …and PIN THE LANGUAGE. `AppConfig.LanguageCulture` defaults to
        // "system", so an otherwise-empty config makes the suite follow the
        // machine's locale — this box is German, and the first isolated run
        // immediately turned up settings rows overflowing their fixed control
        // column with German labels. Those were real product bugs, but a suite
        // whose assertions change with the developer's Windows language is not a
        // gate. So the culture is PINNED — and the pin is a KNOB, not a
        // constant: see <see cref="SweptCulture" />.
        // …and mark onboarding DONE: the first-run wizard opens on its own
        // exactly while AppConfig.OnboardingCompleted is false, and an isolated
        // empty config is a first run by definition. Left unset, every headless
        // boot would overlay the shell with the wizard (HasOpenDialog hides the
        // 3D pane, Esc routes to it first) and every screen assertion would be
        // measuring a dialog nobody asked for. Its own smoke test opens it
        // explicitly through the view model, which is the product path.
        File.WriteAllText(
            Path.Combine(root, "appconfig.json"),
            $$"""{"LanguageCulture":"{{SweptCulture}}","OnboardingCompleted":true}""");

        // The config pins the UI LANGUAGE. It does not pin the FORMATTING
        // culture, and since 2026-08-29 nothing does:
        // `LocalizationService.SetConfiguredCulture` deliberately leaves
        // `CultureInfo.CurrentCulture` at the OS value, because forcing it to
        // the UI language gave every user outside the three built-in languages
        // en-US dates and decimal points on their own machine.
        //
        // That is right for the product and wrong for a LAYOUT gate: a number
        // is 1.5 px wider with a group separator, so leaving it ambient would
        // make `clipped-text` findings depend on the developer's Windows
        // region. So the suite pins it HERE, explicitly, alongside the language
        // — which also states the dependency instead of inheriting it as a
        // side effect of a product call.
        var formatting = CultureInfo.GetCultureInfo(SweptCulture);
        CultureInfo.DefaultThreadCurrentCulture = formatting;
        CultureInfo.CurrentCulture = formatting;
    }

    /// <summary>
    ///     Tears the session down by AWAITING it. The synchronous
    ///     <c>Dispose()</c> deadlocks — see <see cref="HeadlessSessionLifecycle" />.
    ///     <para>
    ///         The session is captured under the lock and disposed OUTSIDE it: the
    ///         await can resume on another thread, and holding a <see cref="Lock" />
    ///         across it is not something the type supports.
    ///     </para>
    /// </summary>
    internal static async ValueTask ShutdownAsync()
    {
        HeadlessUnitTestSession? session;
        lock (Gate)
        {
            session = _session;
            _session = null;
        }

        if (session == null) return;
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch { /* teardown is best effort */ }

        // Release the config roots we claimed and take the throwaway tree with
        // us. Only if WE claimed them — declining is a legal outcome above.
        // This runs ONLY in the headless-filtered process: the shutdown is
        // driven by HeadlessSessionLifecycle, the HeadlessUI collection fixture,
        // and that collection has no members in the default gate. So the
        // unconditional clear below cannot land on a CwdSerialized test's own
        // override.
        var isolated = Interlocked.Exchange(ref _isolatedRoot, null);
        if (isolated == null) return;
        WritablePaths.OverrideRootsForTesting(null, null);
        BestEffortDelete.Directory(isolated);
    }
}

public static class HeadlessAppHelper
{
    /// <summary>
    ///     Headless drawing (a no-op backend) everywhere EXCEPT when the UI
    ///     sweep is asked for pictures.
    ///     <para>
    ///         <c>CaptureRenderedFrame</c> can only return a frame when the
    ///         session rasterizes for real, which needs Skia and therefore
    ///         <c>UseHeadlessDrawing = false</c> — and that makes every layout in
    ///         the session pay real text shaping and rasterization. So it is
    ///         opt-in through <see cref="UiSweepRunner.CaptureEnvVar" />
    ///         (<c>DOTCHARTER_UI_SWEEP_DUMP=1</c>), following the
    ///         <c>DOTCHARTER_GPU_DUMP</c> precedent: assertions run either way,
    ///         images cost extra. Still headless — no window reaches the desktop
    ///         in either mode.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <see cref="HeadlessUiThreadClaim.InstallInto" /> goes LAST, because it
    ///     wraps the runtime-platform initializer <c>UseHeadless</c> sets: it makes
    ///     the session thread the UI thread before any platform init (#192).
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        HeadlessUiThreadClaim.InstallInto(AppBuilder.Configure<App>()
            .UseSkia()
            // Mirror the real Program.BuildAvaloniaApp: without the fallback
            // chain every CJK glyph in a headless layout is tofu, which would
            // make TextFallbackUiPerfSmokeTests measure the fixture rather than
            // the app.
            .With(DotCharter.Tone.UI.Platform.TextFallbackFonts.Options())
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = !UiSweepRunner.CaptureEnabled
            }));
}

/// <summary>
///     xUnit collection fixture that tears the Avalonia headless session down at
///     the end of the assembly run.
///     <para>
///         <b>It must be ASYNC, and that is the whole reason the perf tests used
///         to hang.</b> The run executed all 66 tests in ~42 s and then wedged for
///         minutes at teardown — for a long time the documented reason
///         `UiPerfSmokeTests` are excluded from the default gate. The old comment
///         here blamed "the dispatcher's foreground thread", and a heap dump of a
///         live hang showed that is wrong: there were zero foreground threads. It
///         is a SELF-JOIN.
///     </para>
///     <para>
///         <c>HeadlessUnitTestSession.DispatchCore</c> completes its
///         <c>TaskCompletionSource</c> from inside the queued action, without
///         <c>RunContinuationsAsynchronously</c> — so <c>await session.Dispatch(…)</c>
///         resumes INLINE on the dispatch-loop thread, and so does every await
///         above it in xunit's pipeline, right up to collection teardown. The
///         synchronous <c>Dispose()</c> then does <c>_dispatchTask.Wait()</c> — on
///         the very thread running that task, 300 frames below. It can never
///         return.
///     </para>
///     <para>
///         <c>DisposeAsync</c> unwinds the stack instead of blocking it: the
///         queued action returns, the loop observes the cancellation, the task
///         completes. xunit v3 prefers <see cref="IAsyncDisposable" /> and
///         genuinely awaits it, so implementing it is enough.
///     </para>
///     <para>
///         <b>Verified 2026-08-23, and it did NOT make the suite gate-eligible.</b>
///         Standalone the hang is gone: 326 cases, ~7 min, and ≤7 s between the
///         last result and process exit. But in ONE testhost with the rest of the
///         gate, 310 of those 326 fail — and nothing else does — because
///         <c>Avalonia.Headless</c>'s dispatcher is process-global and xunit runs
///         the other collections in parallel with <c>HeadlessUI</c>. So the
///         exclusion stays, as a PROCESS split (a second <c>dotnet test</c> run
///         with <c>--filter "FullyQualifiedName~UiPerfSmokeTests"</c>), not as a
///         hang. Do not "fix" it by serializing the whole gate.
///     </para>
///     <para>
///         Corollary: <b>every <c>*UiPerfSmokeTests</c> class must carry
///         <c>[Collection("HeadlessUI")]</c></b> (once per partial type). A class
///         without it is its own collection, runs in PARALLEL with this one, and
///         poisons the session for everybody — which is what the intermittent
///         <c>EnsureIsolatedApplication</c> failures were.
///     </para>
/// </summary>
public sealed class HeadlessSessionLifecycle : IAsyncDisposable
{
    public HeadlessSessionLifecycle() => HeadlessSessionFixture.Get();

    public ValueTask DisposeAsync() => HeadlessSessionFixture.ShutdownAsync();
}

[CollectionDefinition("HeadlessUI", DisableParallelization = true)]
public sealed class HeadlessUiCollection : ICollectionFixture<HeadlessSessionLifecycle> { }

