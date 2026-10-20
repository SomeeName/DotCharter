using DotCharter.Presentation.Contracts.Onboarding;
using DotCharter.Presentation.Screens.Onboarding;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Graphics;

namespace DotCharter.Tests.Presentation.Onboarding;

/// <summary>
///     The first-run wizard's check of this computer (#288), one case per state the
///     first page can show: all fine, the native audio library missing, the library
///     from another build (ABI mismatch), and each way the GPU cannot run the Vulkan
///     renderer. The probes are injected fakes — no DLL is loaded and no GPU is
///     touched — and the localization answers with KEYS, so each case pins which
///     sentence the row shows rather than its wording.
/// </summary>
public sealed class SystemCheckViewModelTests
{
    private static (SystemCheckViewModel Vm, FakeNativeEngineProbe Engine, FakeGpuRenderProbe Gpu) Build(
        FakeNativeEngineProbe? engine = null, FakeGpuRenderProbe? gpu = null)
    {
        engine ??= new FakeNativeEngineProbe();
        gpu ??= new FakeGpuRenderProbe();
        var vm = new SystemCheckViewModel(engine, gpu, FakeLocalization.KeysOnly(), new OnboardingWizardRig.InlineDispatcher());
        return (vm, engine, gpu);
    }

    private static async Task<SystemCheckViewModel> Checked(FakeNativeEngineProbe? engine = null, FakeGpuRenderProbe? gpu = null)
    {
        var (vm, _, _) = Build(engine, gpu);
        await vm.Start();
        return vm;
    }

    private static ISystemCapability EngineRow(ISystemCheckScreen screen) => screen.Capabilities[0];

    private static ISystemCapability GpuRow(ISystemCheckScreen screen) => screen.Capabilities[1];

    [Fact]
    public void BeforeTheCheckRuns_BothRowsSayChecking_AndNeitherLooksLiveNorOff()
    {
        var (vm, engine, gpu) = Build();

        Assert.True(vm.IsBusy);
        Assert.False(vm.HasProblem);
        Assert.Equal("SystemCheck_Checking", vm.Summary);
        Assert.Equal(["SystemCheck_Engine_Name", "SystemCheck_Gpu_Name"], vm.Capabilities.Select(c => c.Name));
        Assert.All(vm.Capabilities, c =>
        {
            Assert.Equal("SystemCheck_Status_Checking", c.StatusText);
            Assert.False(c.IsAvailable);
            Assert.False(c.IsUnavailable);
            Assert.False(c.HasReason);
        });
        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, gpu.Calls);
    }

    [Fact]
    public async Task AllFine_ShowsTwoQuietReadyRows_AndNothingAlarming()
    {
        var vm = await Checked();

        Assert.False(vm.IsBusy);
        Assert.False(vm.HasProblem);
        Assert.Equal("SystemCheck_Ready", vm.Summary);
        Assert.All(vm.Capabilities, c =>
        {
            Assert.True(c.IsAvailable);
            Assert.False(c.IsUnavailable);
            Assert.Equal("SystemCheck_Status_Ready", c.StatusText);
            Assert.Equal(string.Empty, c.Reason);
            Assert.False(c.HasReason);
        });
    }

    [Fact]
    public async Task EngineMissing_DisablesTheAudioRow_WithItsReason()
    {
        var vm = await Checked(new FakeNativeEngineProbe(new NativeEngineCheck(NativeEngineState.Missing)));

        Assert.True(vm.HasProblem);
        Assert.Equal("SystemCheck_Problem", vm.Summary);
        var row = EngineRow(vm);
        Assert.True(row.IsUnavailable);
        Assert.False(row.IsAvailable);
        Assert.Equal("SystemCheck_Status_Off", row.StatusText);
        Assert.Equal("SystemCheck_Engine_Missing", row.Reason);
        Assert.True(row.HasReason);
        Assert.True(GpuRow(vm).IsAvailable);
    }

    [Fact]
    public async Task AbiMismatch_DisablesTheAudioRow_AndNamesBothVersions()
    {
        var vm = await Checked(new FakeNativeEngineProbe(new NativeEngineCheck(NativeEngineState.AbiMismatch, 42, 41)));

        Assert.True(vm.HasProblem);
        var row = EngineRow(vm);
        Assert.True(row.IsUnavailable);
        Assert.Equal("SystemCheck_Engine_AbiMismatch(42, 41)", row.Reason);
        Assert.True(GpuRow(vm).IsAvailable);
    }

    [Theory]
    [InlineData(GpuRenderState.NoVulkan, "SystemCheck_Gpu_NoVulkan")]
    [InlineData(GpuRenderState.NoDevice, "SystemCheck_Gpu_NoDevice")]
    [InlineData(GpuRenderState.NoInteropDevice, "SystemCheck_Gpu_NoInterop")]
    public async Task VulkanUnsupported_DisablesTheGraphicsRow_WithItsReason(GpuRenderState state, string reasonKey)
    {
        var vm = await Checked(gpu: new FakeGpuRenderProbe(new GpuRenderCheck(state)));

        Assert.True(vm.HasProblem);
        Assert.Equal("SystemCheck_Problem", vm.Summary);
        var row = GpuRow(vm);
        Assert.True(row.IsUnavailable);
        Assert.False(row.IsAvailable);
        Assert.Equal("SystemCheck_Status_Off", row.StatusText);
        Assert.Equal(reasonKey, row.Reason);
        Assert.True(EngineRow(vm).IsAvailable);
    }

    [Fact]
    public async Task BothOff_DisablesBothRows()
    {
        var vm = await Checked(new FakeNativeEngineProbe(new NativeEngineCheck(NativeEngineState.Missing)),
            new FakeGpuRenderProbe(new GpuRenderCheck(GpuRenderState.NoVulkan)));

        Assert.All(vm.Capabilities, c => Assert.True(c.IsUnavailable));
    }

    [Fact]
    public async Task AProbeThatThrows_ReadsAsTheCapabilityBeingOff_NeverAsReady()
    {
        var vm = await Checked(new FakeNativeEngineProbe { Throws = new DllNotFoundException("juce_sharp") },
            new FakeGpuRenderProbe { Throws = new InvalidOperationException("driver") });

        Assert.Equal("SystemCheck_Engine_Missing", EngineRow(vm).Reason);
        Assert.Equal("SystemCheck_Gpu_NoVulkan", GpuRow(vm).Reason);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task StartingTwice_ProbesOnce()
    {
        var (vm, engine, gpu) = Build();

        var first = vm.Start();
        var second = vm.Start();
        await first;

        Assert.Same(first, second);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, gpu.Calls);
    }

    [Fact]
    public async Task TheFinishedCheck_RaisesEveryPropertyItChanged()
    {
        var (vm, _, _) = Build(new FakeNativeEngineProbe(new NativeEngineCheck(NativeEngineState.Missing)));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.Start();

        Assert.Contains(nameof(ISystemCheckScreen.Capabilities), raised);
        Assert.Contains(nameof(ISystemCheckScreen.HasProblem), raised);
        Assert.Contains(nameof(ISystemCheckScreen.Summary), raised);
        Assert.Contains(nameof(ISystemCheckScreen.IsBusy), raised);
    }
}
