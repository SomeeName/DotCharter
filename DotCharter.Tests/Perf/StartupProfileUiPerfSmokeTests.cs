using System.Diagnostics;
using System.Text;
using DotCharter.Localization;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI;
using DotCharter.UI.Input;
using DotCharter.UI.Theming;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Cold-start profiler. Builds a SECOND real DI container with every
///     descriptor wrapped in a stopwatch, replays the exact resolution order of
///     <c>App.OnFrameworkInitializationCompleted</c>, then constructs
///     <c>MainWindow</c> and binds it — and dumps per-service EXCLUSIVE
///     construction cost. It exists because the app's own <c>StartupPhases:</c>
///     log line tells you WHICH phase is slow but never WHICH SERVICE, and the
///     only other way to find that out is launching the GUI under a profiler.
///     <para>
///         Report lands in <c>perf-reports/00-startup-profile.txt</c> next to the
///         test binaries (and in the test output). Run it with:
///         <c>dotnet test --filter FullyQualifiedName~StartupProfile</c>.
///     </para>
///     <para>
///         <b>Read round 1 only.</b> Every round constructs a fresh
///         <c>JuceAudioPlaybackService</c>, so from round 2 on there are several
///         live JUCE engines in the process and anything that touches the native
///         engine reads several times too slow. Rounds 2+ are useful for exactly
///         one thing: telling JIT/static-init (huge in round 1, gone afterwards)
///         apart from real work (flat across rounds).
///     </para>
///     <para>
///         No assertions — it is a measurement tool, not a gate. Wall-clock
///         budgets in the default suite have flaked repeatedly in this repo; the
///         structural guards live in <c>StartupWorkDeferralTests</c> instead.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class StartupProfileUiPerfSmokeTests(ITestOutputHelper o)
{
    private const int Rounds = 3;

    private sealed record Row(string Name, double ExclusiveMs, double InclusiveMs);

    /// <summary>Inclusive time already attributed to nested constructions, so a
    /// parent can subtract its children and report its OWN cost.</summary>
    [ThreadStatic] private static double _childMs;

    private static readonly List<Row> Rows = [];

    [Fact]
    public async Task Profile_Startup_Service_Construction()
    {
        // Dispatch goes through the fixture so the app's own background work is drained
        // before Avalonia's 5 s teardown drain sees it - see HeadlessSessionFixture.Dispatch.
        var sb = new StringBuilder();

        await HeadlessSessionFixture.Dispatch(() =>
        {
            for (var round = 1; round <= Rounds; round++)
            {
                sb.AppendLine($"################ ROUND {round} " +
                              (round == 1 ? "(the one to read) " : "(extra JUCE engines live — JIT check only) ") +
                              "################");
                ProfileOnce(sb);
            }
        }, TestContext.Current.CancellationToken);

        var text = sb.ToString();
        o.WriteLine(text);
        var dir = Path.Combine(AppContext.BaseDirectory, "perf-reports");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "00-startup-profile.txt"), text,
            TestContext.Current.CancellationToken);
    }

    private static void ProfileOnce(StringBuilder sb)
    {
        lock (Rows) Rows.Clear();

        var collection = new ServiceCollection();
        var configureMs = TimeStep(() => App.ConfigureServices(collection, new JsonConfigurationService()));
        for (var i = 0; i < collection.Count; i++) collection[i] = Wrap(collection[i]);

        ServiceProvider provider = null!;
        var buildMs = TimeStep(() => provider = collection.BuildServiceProvider());

        // Nothing has been CONSTRUCTED yet — only described. Drop whatever the
        // wrapping itself recorded so the table below is the startup graph.
        lock (Rows) Rows.Clear();

        var steps = new List<(string Label, double Ms)>();
        foreach (var (label, act) in StartupOrder(provider))
        {
            var sw = Stopwatch.StartNew();
            act();
            sw.Stop();
            steps.Add((label, sw.Elapsed.TotalMilliseconds));
        }

        MainWindow window = null!;
        steps.Add(("new MainWindow()", TimeStep(() => window = new MainWindow())));
        steps.Add(("bind DataContext",
            TimeStep(() => window.DataContext = provider.GetRequiredService<MainWindowViewModel>())));
        steps.Add(("UpdateLayout (unshown)", TimeStep(window.UpdateLayout)));

        sb.AppendLine($"ConfigureServices (describe only): {configureMs:0.0} ms");
        sb.AppendLine($"BuildServiceProvider:              {buildMs:0.0} ms");
        sb.AppendLine();
        sb.AppendLine("== Startup steps, inclusive, in App.OnFrameworkInitializationCompleted order ==");
        foreach (var (label, ms) in steps) sb.AppendLine($"  {label,-42} {ms,9:0.00} ms");

        List<Row> snapshot;
        lock (Rows) snapshot = [.. Rows];
        sb.AppendLine();
        sb.AppendLine($"== Service constructors ({snapshot.Count} constructed) by EXCLUSIVE ms ==");
        foreach (var g in snapshot
                     .GroupBy(r => r.Name)
                     .Select(g => (Name: g.Key, Excl: g.Sum(r => r.ExclusiveMs),
                         Incl: g.Sum(r => r.InclusiveMs), Count: g.Count()))
                     .OrderByDescending(x => x.Excl)
                     .Take(30))
        {
            sb.AppendLine($"  {g.Name,-52} excl {g.Excl,8:0.00} ms  incl {g.Incl,8:0.00} ms  x{g.Count}");
        }

        sb.AppendLine();
        sb.AppendLine($"TOTAL exclusive across all constructors: {snapshot.Sum(r => r.ExclusiveMs):0.0} ms");
        sb.AppendLine();
    }

    /// <summary>Mirrors what the composition root resolves, in its order. Keep it
    /// in step with <c>App.OnFrameworkInitializationCompleted</c> — a step that
    /// drifts out just reports its cost against the wrong neighbour.</summary>
    private static (string Label, Action Act)[] StartupOrder(IServiceProvider p) =>
    [
        ("IConfigurationService", () => p.GetRequiredService<IConfigurationService>()),
        ("LoadAppConfig", () => p.GetRequiredService<IConfigurationService>().LoadAppConfig()),
        ("ILocalizationService", () => p.GetRequiredService<ILocalizationService>()),
        ("LoadCommunityLanguages", () => p.GetRequiredService<ILocalizationService>()
            .LoadCommunityLanguages(Path.Combine(WritablePaths.ConfigRoot, "languages"))),
        ("SetConfiguredCulture", () => p.GetRequiredService<ILocalizationService>()
            .SetConfiguredCulture(p.GetRequiredService<IConfigurationService>().LoadAppConfig().LanguageCulture)),
        ("IThemeService", () => p.GetRequiredService<IThemeService>()),
        ("ProjectState", () => p.GetRequiredService<ProjectState>()),
        ("LoadKeybinds", () => p.GetRequiredService<IConfigurationService>().LoadKeybinds()),
        ("IPlayerProfileService",
            () => p.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>()),
        ("IVulkanRenderService", () => p.GetRequiredService<DotCharter.Rendering.Vulkan.IVulkanRenderService>()),
        ("ITrackContextRegistry", () => p.GetRequiredService<DotCharter.Services.Editing.Tracks.ITrackContextRegistry>()),
        ("IEditingScope", () => p.GetRequiredService<IEditingScope>()),
        ("IEditTargetService", () => p.GetRequiredService<DotCharter.Services.Editing.Targets.IEditTargetService>()),
        ("IChartPenService", () => p.GetRequiredService<DotCharter.Services.Editing.Pen.IChartPenService>()),
        ("TransportViewModel", () => p.GetRequiredService<DotCharter.UI.ViewModels.Editor.TransportViewModel>()),
        ("IHistoryService", () => p.GetRequiredService<IHistoryService>()),
        ("IAppLogger", () => p.GetRequiredService<IAppLogger>()),
        // The shell BEFORE the registrar, exactly as the composition root does it
        // — the registrar takes the shell, so the other order hides its cost.
        ("MainWindowViewModel (the whole VM graph)", () => p.GetRequiredService<MainWindowViewModel>()),
        ("AppKeybindRegistrar.RegisterAll", () => p.GetRequiredService<AppKeybindRegistrar>().RegisterAll()),
        ("IKeybindRegistry.RefreshBinds", () => p.GetRequiredService<IKeybindRegistry>().RefreshBinds())
    ];

    private static ServiceDescriptor Wrap(ServiceDescriptor d)
    {
        if (d.ServiceType.IsGenericTypeDefinition) return d;

        if (d.IsKeyedService)
        {
            if (d.KeyedImplementationInstance != null) return d;
            var keyedType = d.KeyedImplementationType;
            var keyedFactory = d.KeyedImplementationFactory;
            return ServiceDescriptor.DescribeKeyed(
                d.ServiceType, d.ServiceKey,
                (sp, key) => Time(NameOf(d, keyedType),
                    () => keyedFactory != null
                        ? keyedFactory(sp, key)
                        : ActivatorUtilities.CreateInstance(sp, keyedType!)),
                d.Lifetime);
        }

        if (d.ImplementationInstance != null) return d;
        var implType = d.ImplementationType;
        var factory = d.ImplementationFactory;
        return ServiceDescriptor.Describe(
            d.ServiceType,
            sp => Time(NameOf(d, implType),
                () => factory != null ? factory(sp) : ActivatorUtilities.CreateInstance(sp, implType!)),
            d.Lifetime);
    }

    private static string NameOf(ServiceDescriptor d, Type? implType) => implType?.Name ?? d.ServiceType.Name;

    private static object Time(string name, Func<object> create)
    {
        var savedChild = _childMs;
        _childMs = 0;
        var sw = Stopwatch.StartNew();
        var instance = create();
        sw.Stop();
        var inclusive = sw.Elapsed.TotalMilliseconds;
        lock (Rows) Rows.Add(new Row(name, inclusive - _childMs, inclusive));
        _childMs = savedChild + inclusive;
        return instance;
    }

    private static double TimeStep(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
