using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     <b>The Stem Models tab's backend choices must come back from disk on a
///     reload.</b>
///     <para>
///         <c>Load()</c> is this block's load path — the settings shell runs it
///         on every open of the screen, "Reset section" routes at it, and it
///         runs after a config-backup IMPORT has replaced the files underneath.
///         The screen's own load used to rebuild only the model ROWS.
///     </para>
///     <para>
///         So the backend knobs were read exactly once, in the constructor, and
///         never again for the life of the process. After an import the tab
///         still showed the pre-import values, and because this tab PERSISTS ON
///         CHANGE (<c>Persist</c>, not on Save), the next touch of a knob wrote
///         those stale values straight back over the imported config. (Two of
///         those knobs, max concurrent separations and CPU fallback, configured
///         an orchestrator that never separated and were deleted with it, #662.)
///     </para>
/// </summary>
public sealed class StemBackendViewModelTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly FakeConfigurationService _cfg = new();

    public StemBackendViewModelTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "DotCharter_StemPerf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
    }

    private const ulong Gb = 1UL << 30;

    private static readonly StemGpuAdapter Rtx5070 = new(0, "NVIDIA GeForce RTX 5070 Ti", 16 * Gb, false);
    private static readonly StemGpuAdapter Rtx3080 = new(1, "NVIDIA GeForce RTX 3080", 10 * Gb, false);
    private static readonly StemGpuAdapter Igpu = new(2, "Intel(R) UHD Graphics 770", 128UL << 20, false);
    private static readonly StemGpuAdapter Warp = new(3, "Microsoft Basic Render Driver", 0, true);

    private StemBackendViewModel BuildVm(FakeLocalization? localization = null, IStemGpuEnvironment? gpu = null)
    {
        var vm = new StemBackendViewModel(
            new StemModelManager(_cfg, logger: null, modelsDirectoryOverride: _tmpDir), _cfg,
            localization ?? FakeLocalization.English(), gpu: gpu);
        vm.Load();
        return vm;
    }

    private async Task<StemBackendViewModel> BuildProbedVm(
        IReadOnlyList<StemGpuAdapter> adapters, bool directMl = true, FakeLocalization? localization = null)
    {
        var vm = BuildVm(localization, new FakeGpuEnvironment(directMl, adapters));
        await vm.RefreshHardwareAsync();
        return vm;
    }

    [Fact]
    public void Load_SeedsTheKnobsFromConfig()
    {
        _cfg.AppConfig.StemSeparationExecutionProvider = "cpu";

        var vm = BuildVm();

        Assert.Equal("cpu", vm.PreferredExecutionProvider);
        Assert.Equal("CPU", vm.SelectedExecutionProvider.Label);
    }

    /// <summary>
    ///     Versions that shipped the CUDA build stored "Cuda" as their automatic
    ///     choice. On a machine with a usable GPU every stored choice but "cpu" reads
    ///     as DirectML, shows as that entry, and nothing throws on the way.
    /// </summary>
    [Theory]
    [InlineData("Cuda")]
    [InlineData("DirectML")]
    [InlineData("directml")]
    [InlineData("Auto")]
    [InlineData("something a future version wrote")]
    public async Task AStoredGpuChoice_LoadsAsDirectMl_WhereAGpuCanRunIt(string stored)
    {
        _cfg.AppConfig.StemSeparationExecutionProvider = stored;
        var vm = await BuildProbedVm([Rtx3080]);
        Assert.Equal("DirectML", vm.PreferredExecutionProvider);
        Assert.True(vm.SelectedExecutionProvider.IsAvailable);
        Assert.Equal("DirectML", vm.SelectedExecutionProvider.Label);
    }

    /// <summary>
    ///     With no GPU that can run the model, DirectML stays in the list, greyed,
    ///     with its reason: removing it would leave the user with no idea why the GPU
    ///     is not used. Picking it is refused, writes nothing, and the picker is told
    ///     to re-read the entry in effect.
    /// </summary>
    [Fact]
    public async Task WithoutAUsableGpu_DirectMlIsGreyedWithItsReason_AndCannotBePicked()
    {
        var vm = await BuildProbedVm([Igpu, Warp], localization: FakeLocalization.KeysOnly());
        var directMl = vm.Options.Single(o => o.Value == "DirectML");
        Assert.False(directMl.IsAvailable);
        Assert.Equal("Settings_StemModels_DirectMlUnavailable(3 GB)", directMl.UnavailableReason);
        Assert.All(vm.Options.Where(o => o != directMl), o => Assert.True(o.IsAvailable));
        Assert.Equal("cpu", vm.PreferredExecutionProvider);

        var writesBefore = _cfg.Writes.Count;
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        vm.SelectedExecutionProvider = directMl;

        Assert.Equal("cpu", vm.PreferredExecutionProvider);
        Assert.Equal(writesBefore, _cfg.Writes.Count);
        Assert.Contains(nameof(StemBackendViewModel.SelectedExecutionProvider), notified);
    }

    [Fact]
    public async Task ARuntimeWithoutDirectMl_SaysSo()
    {
        var vm = await BuildProbedVm([Rtx3080], directMl: false, localization: FakeLocalization.KeysOnly());
        var directMl = vm.Options.Single(o => o.Value == "DirectML");
        Assert.False(directMl.IsAvailable);
        Assert.Equal("Settings_StemModels_DirectMlMissing", directMl.UnavailableReason);
    }

    /// <summary>
    ///     A GPU missing TODAY (a driver update, an unplugged eGPU) shows the CPU but
    ///     must not overwrite the stored preference: the probe persists nothing.
    /// </summary>
    [Fact]
    public async Task AMissingGpu_ShowsTheCpu_ButKeepsTheStoredPreference()
    {
        _cfg.AppConfig.StemSeparationExecutionProvider = "DirectML";
        var writesBefore = _cfg.Writes.Count;
        var vm = await BuildProbedVm([]);
        Assert.Equal("cpu", vm.PreferredExecutionProvider);
        Assert.Equal(writesBefore, _cfg.Writes.Count);
        Assert.Equal("DirectML", _cfg.AppConfig.StemSeparationExecutionProvider);
    }

    [Fact]
    public async Task TheGpuRow_ShowsOnlyWithTwoOrMoreHardwareGpus()
    {
        Assert.False((await BuildProbedVm([Rtx3080, Warp])).IsGpuRowVisible);
        Assert.True((await BuildProbedVm([Rtx3080, Rtx5070, Warp])).IsGpuRowVisible);
    }

    [Fact]
    public async Task TheGpuRow_HidesWhileTheCpuIsChosen()
    {
        var vm = await BuildProbedVm([Rtx3080, Rtx5070]);
        vm.SelectedExecutionProvider = vm.Options.Single(o => o.Value == "cpu");
        Assert.False(vm.IsGpuRowVisible);
    }

    /// <summary>
    ///     The adapter DXGI lists first is the display one. Nothing stored means "Automatic",
    ///     which names the card it picks: the largest when none drives a display.
    /// </summary>
    [Fact]
    public async Task TheDefaultGpu_IsAutomatic_NamingTheLargest_NotAdapterZero()
    {
        var vm = await BuildProbedVm([Igpu with { DeviceId = 0 }, Rtx3080, Rtx5070 with { DeviceId = 2 }]);
        Assert.Equal("Automatic (NVIDIA GeForce RTX 5070 Ti · 16 GB)", vm.SelectedGpu!.Label);
        Assert.Equal(["Automatic (NVIDIA GeForce RTX 5070 Ti · 16 GB)", "NVIDIA GeForce RTX 5070 Ti · 16 GB",
                "NVIDIA GeForce RTX 3080 · 10 GB", "Intel(R) UHD Graphics 770 · 128 MB"],
            vm.GpuOptions.Select(o => o.Label));
    }

    /// <summary>#463: the owner's 5070 Ti is adapter 0 and drives the monitors; Automatic takes the idle 3080.</summary>
    [Fact]
    public async Task Automatic_PrefersTheGpuThatDrivesNoDisplay()
    {
        var vm = await BuildProbedVm([Rtx5070 with { DrivesDisplay = true }, Rtx3080]);
        Assert.Equal("Automatic (NVIDIA GeForce RTX 3080 · 10 GB)", vm.SelectedGpu!.Label);
        Assert.Equal("NVIDIA GeForce RTX 3080 · 10 GB", vm.GpuOptions[1].Label);
    }

    [Fact]
    public async Task PickingAutomatic_ClearsAStoredGpu()
    {
        var vm = await BuildProbedVm([Rtx5070, Rtx3080]);
        vm.SelectedGpu = vm.GpuOptions.Single(o => o.Label.StartsWith("NVIDIA GeForce RTX 3080", StringComparison.Ordinal));
        Assert.Equal("1:NVIDIA GeForce RTX 3080", _cfg.AppConfig.StemSeparationGpuAdapter);

        vm.SelectedGpu = vm.GpuOptions[0];
        Assert.Equal("", _cfg.AppConfig.StemSeparationGpuAdapter);
        Assert.StartsWith("Automatic (", vm.SelectedGpu!.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGpuThatCannotRunTheModel_IsGreyedWithItsReason_AndCannotBePicked()
    {
        var vm = await BuildProbedVm([Rtx3080, Igpu], localization: FakeLocalization.KeysOnly());
        var small = vm.GpuOptions.Single(o => o.Label.StartsWith("Intel", StringComparison.Ordinal));
        Assert.False(small.IsAvailable);
        Assert.Equal("Settings_StemModels_GpuTooSmallFmt(3 GB, 128 MB)", small.UnavailableReason);

        var writesBefore = _cfg.Writes.Count;
        vm.SelectedGpu = small;
        Assert.Equal("Settings_StemModels_GpuAuto(NVIDIA GeForce RTX 3080 · 10 GB)", vm.SelectedGpu!.Label);
        Assert.Equal(writesBefore, _cfg.Writes.Count);
    }

    [Fact]
    public async Task PickingAGpu_PersistsIt_AndAReloadKeepsIt()
    {
        var vm = await BuildProbedVm([Rtx5070, Rtx3080]);
        vm.SelectedGpu = vm.GpuOptions.Single(o => o.Label.StartsWith("NVIDIA GeForce RTX 3080", StringComparison.Ordinal));
        Assert.Equal("1:NVIDIA GeForce RTX 3080", _cfg.AppConfig.StemSeparationGpuAdapter);

        var reloaded = await BuildProbedVm([Rtx5070, Rtx3080]);
        Assert.StartsWith("NVIDIA GeForce RTX 3080", reloaded.SelectedGpu!.Label, StringComparison.Ordinal);
    }

    private sealed class FakeGpuEnvironment(bool directMl, IReadOnlyList<StemGpuAdapter> adapters) : IStemGpuEnvironment
    {
        public bool IsDirectMlAvailable => directMl;

        public IReadOnlyList<StemGpuAdapter> Adapters() => adapters;
    }

    /// <summary>A real pick still persists, or the refusal above could be met by a dead picker.</summary>
    [Fact]
    public async Task PickingAnAvailableEntry_Persists()
    {
        var vm = await BuildProbedVm([Rtx3080]);

        vm.SelectedExecutionProvider = vm.Options.Single(o => o.Value == "cpu");

        Assert.Equal("cpu", vm.PreferredExecutionProvider);
        Assert.Equal("cpu", _cfg.AppConfig.StemSeparationExecutionProvider);
    }

    /// <summary>The group's subtitle and the status line read in the UI language: they are resx keys, not prose.</summary>
    [Fact]
    public async Task TheHintAndTheStatus_AreResxStrings()
    {
        var vm = BuildVm(FakeLocalization.KeysOnly());

        Assert.Equal("Settings_StemModels_BackendHint", vm.Hint);
        await vm.RefreshStatusAsync();
        Assert.Equal("Settings_StemModels_NoService", vm.Status);
    }

    /// <summary>The bug. A backup import replaces appconfig.json and then calls
    /// <c>ReloadFromDisk</c>, which reaches this tab through Refresh.</summary>
    [Fact]
    public async Task Load_ReReadsTheKnobsAfterTheConfigChangedUnderneath()
    {
        var vm = await BuildProbedVm([Rtx5070]);
        Assert.Equal("DirectML", vm.PreferredExecutionProvider);

        // …an import (or any other writer) replaces what is on disk.
        _cfg.AppConfig.StemSeparationExecutionProvider = "cpu";

        vm.Load();

        Assert.Equal("cpu", vm.PreferredExecutionProvider);
    }

    /// <summary>
    ///     …and the half that makes the first one safe. This tab saves on
    ///     CHANGE, so a load that read as an edit would write every time the
    ///     Settings page is opened — and, worse, would write the values
    ///     it had just replaced back over an import if the order ever slipped.
    /// </summary>
    [Fact]
    public void Load_PersistsNothing()
    {
        var vm = BuildVm();
        _cfg.AppConfig.StemSeparationExecutionProvider = "cpu";
        var writesBefore = _cfg.Writes.Count;

        vm.Load();

        Assert.Equal(writesBefore, _cfg.Writes.Count);
    }

    /// <summary>A real edit still persists — otherwise the guard above could be
    /// satisfied by breaking the tab.</summary>
    [Fact]
    public async Task AnEditStillPersists()
    {
        var vm = await BuildProbedVm([Rtx5070]);
        Assert.Equal("DirectML", vm.PreferredExecutionProvider);
        var writesBefore = _cfg.Writes.Count;

        vm.PreferredExecutionProvider = "cpu";

        Assert.True(_cfg.Writes.Count > writesBefore);
        Assert.Equal("cpu", _cfg.AppConfig.StemSeparationExecutionProvider);
    }

    /// <summary>
    ///     The dead twin must stay dead. <c>ExecutionProvider</c> wrote the
    ///     model manager's in-memory field and nothing else — no config field —
    ///     so the pick did nothing an engine could see, and
    ///     the next load overwrote it from the persisted value. A row bound to
    ///     it reads as live.
    /// </summary>
    [Fact]
    public void TheDeadExecutionProviderTwin_IsGoneAndUnbound()
    {
        Assert.Null(typeof(StemBackendViewModel).GetProperty(
            "ExecutionProvider", BindingFlags.Public | BindingFlags.Instance));

        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "StemModelsSettingsView.axaml")));

        // The `` matters: `ExecutionProviderOptions` — the RIGHT binding —
        // contains the dead name as a substring.
        Assert.False(
            Regex.IsMatch(markup, @"\{Binding\s+(?:Path=)?ExecutionProviderOptions"),
            "the view still binds the dead ExecutionProviderOptions list.");
        Assert.Matches(@"\{Binding\s+Backend\.SelectedExecutionProvider,\s+Mode=TwoWay\}", markup);
    }
}
