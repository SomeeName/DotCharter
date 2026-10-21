using DotCharter.Presentation.Navigation;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     <c>StemModelsSettingsViewModel</c> is built with the settings shell, and
///     its three blocks each probe real machine state:
///     <c>LegacyGpuRuntimeFolder.InstalledBytes</c> (a walk of the old CUDA
///     runtime folder), the backend's hardware probe plus
///     <c>DescribeBackendAsync</c> (both initialise the native ONNX Runtime
///     environment; the probe also enumerates the DXGI adapters) and
///     <c>StemCacheStore.MeasureUsage</c> (an enumeration of every file in the
///     durable stem cache, the folder the code itself calls "an unexplained
///     tens-of-GB folder"). None of them blocks the UI thread, which is why they
///     were invisible: the cost is working set, three pool threads and the disk
///     head, spent concurrently with the audio-device open and the library scan,
///     for a screen nobody has opened.
///     <para>
///         Behavioural, not wall-clock (the <c>StartupWorkDeferralTests</c> /
///         <c>PhraseCatalogDeferralTests</c> shape): the fakes COUNT what the
///         probes actually did. The last case is a source lint, because a
///         deferral no surface ever opens is a dead feature rather than a fix.
///     </para>
/// </summary>
public sealed class StemModelsProbeDeferralTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ConstructingTheScreen_ProbesNothing()
    {
        var separation = new CountingSeparation();
        var ui = new CountingDispatcher();

        using var vm = Build(separation, ui);

        Assert.Equal(0, separation.BackendDescribes);
        // The GPU and cache blocks report through the dispatcher even on their
        // "not wired" early exits, so zero invocations means neither ran.
        Assert.Equal(0, ui.Invocations);
        Assert.Equal("", vm.Backend.Status);
    }

    [Fact]
    public void OnShown_RunsTheThreeProbes()
    {
        var separation = new CountingSeparation();
        var ui = new CountingDispatcher();
        using var vm = Build(separation, ui);

        vm.OnShown();

        Assert.Equal(1, separation.BackendDescribes);
        Assert.Equal(2, ui.Invocations); // the GPU and cache blocks; the backend reports on its own await
        Assert.Equal("ONNX / CPU", vm.Backend.Status);
    }

    /// <summary>
    ///     Idempotent: the screen is shown, hidden and shown again on every
    ///     visit to Settings, and re-probing the driver and the cache folder
    ///     each time would move the cost rather than remove it. A user who wants
    ///     a fresh reading has each block's own Refresh command.
    /// </summary>
    [Fact]
    public void OnShown_IsIdempotent()
    {
        var separation = new CountingSeparation();
        var ui = new CountingDispatcher();
        using var vm = Build(separation, ui);

        vm.OnShown();
        vm.OnShown();
        vm.OnShown();

        Assert.Equal(1, separation.BackendDescribes);
        Assert.Equal(2, ui.Invocations);
    }

    /// <summary>The explicit Refresh command still probes — the deferral must not cost the screen its manual re-read.</summary>
    [Fact]
    public void TheRefreshCommand_StillProbesAfterOnShown()
    {
        var separation = new CountingSeparation();
        using var vm = Build(separation, new CountingDispatcher());

        vm.OnShown();
        vm.Backend.RefreshCommand.Execute(null);

        Assert.Equal(2, separation.BackendDescribes);
    }

    /// <summary>
    ///     The opener. The screen declares <see cref="IScreenLifecycle" /> and
    ///     the settings shell is what calls it — a deferral nothing opens is a
    ///     dead feature, and this is the half a reflection test cannot see.
    /// </summary>
    [Fact]
    public void TheShellAsksForTheProbeWhenTheScreenIsShown()
    {
        Assert.True(typeof(IScreenLifecycle).IsAssignableFrom(typeof(StemModelsSettingsViewModel)),
            "the screen must declare IScreenLifecycle or nothing will ever open its probes");

        var shell = Read("DotCharter.Presentation", "Screens", "Settings", "SettingsShellViewModel.cs");
        Assert.Contains("IScreenLifecycle)?.OnShown();", shell, StringComparison.Ordinal);

        // ...and the ctor no longer starts them itself. It runs from its
        // signature to the first member declared after it.
        var source = Read("DotCharter.Presentation", "Screens", "Settings", "StemModelsSettingsViewModel.cs");
        var ctorStart = source.IndexOf("public StemModelsSettingsViewModel(", StringComparison.Ordinal);
        var ctorEnd = source.IndexOf("public string ModelsDirectory", StringComparison.Ordinal);
        Assert.True(ctorStart >= 0 && ctorEnd > ctorStart, "the ctor moved — repoint this lint");
        var ctor = source[ctorStart..ctorEnd];

        // Statement-level starts only. The ctor legitimately SUBSCRIBES a
        // ModelsChanged handler whose lambda re-probes when the model set
        // actually changes; that is deferred work, not boot work, and a bare
        // substring scan reports it as the very fault this guards.
        var eagerStarts = new[]
        {
            "_ = _backend.RefreshStatusAsync();",
            "_ = _gpu.RefreshStatusAsync();",
            "_ = _cache.RefreshUsageAsync();"
        };
        var offenders = ctor
            .Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.Contains("=>", StringComparison.Ordinal) && !l.Contains("+=", StringComparison.Ordinal))
            .Where(l => eagerStarts.Any(e => l.StartsWith(e, StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "the ctor still starts a probe at construction: " + string.Join(" | ", offenders));

        // Non-vacuity: all three must still be started from OnShown, or this
        // scan would pass against a rename that broke the deferral.
        var shown = source[source.IndexOf("public void OnShown()", StringComparison.Ordinal)..];
        foreach (var start in eagerStarts) Assert.Contains(start, shown, StringComparison.Ordinal);
    }

    // Only the model manager and these two fakes are touched — the old CUDA
    // runtime folder and the stem cache store are left null on purpose, so a probe
    // that ran anyway takes its "not wired" branch and still REPORTS, which is
    // what the dispatcher count above sees.
    private StemModelsSettingsViewModel Build(IStemSeparationService separation, IUiDispatcher ui)
    {
        var manager = new StemModelManager(modelsDirectoryOverride: _dir);
        var localization = FakeLocalization.English();
        return new StemModelsSettingsViewModel(
            manager, localization, ui,
            new StemModelsBlocks(
                new StemBackendViewModel(manager, new FakeConfigurationService(), localization, separation),
                new GpuRuntimeViewModel(localization, ui),
                new StemCacheViewModel(localization, ui)),
            new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog expected")));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path);
    }

    private sealed class CountingSeparation : IStemSeparationService
    {
        public int BackendDescribes { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default)
        {
            BackendDescribes++;
            return Task.FromResult("ONNX / CPU");
        }

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException("Not reached: these tests only probe.");
    }

    /// <summary>Inline, so a probe completes before the call that started it returns — no waiting, no timing.</summary>
    private sealed class CountingDispatcher : IUiDispatcher
    {
        public int Invocations { get; private set; }

        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            Invocations++;
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            Invocations++;
            return Task.FromResult(func());
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            Invocations++;
            return func();
        }
    }
}
