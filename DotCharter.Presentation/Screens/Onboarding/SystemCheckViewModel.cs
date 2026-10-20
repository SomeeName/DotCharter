using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Onboarding;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Onboarding;

/// <summary>
///     Runs the two capability probes once, off the UI thread, and projects their
///     answers onto <see cref="ISystemCheckScreen" />. The rows start in the
///     checking state; the finished check replaces the list on the UI thread.
/// </summary>
public sealed class SystemCheckViewModel : ObservableObject, ISystemCheckScreen
{
    private readonly INativeEngineProbe _engine;
    private readonly IGpuRenderProbe _gpu;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private Task? _run;

    public SystemCheckViewModel(INativeEngineProbe engine, IGpuRenderProbe gpu, ILocalizationService localization,
        IUiDispatcher ui, IAppLogger? logger = null)
    {
        _engine = engine;
        _gpu = gpu;
        _localization = localization;
        _ui = ui;
        _logger = logger;
        Summary = localization["SystemCheck_Checking"];
        Capabilities =
        [
            SystemCapability.Checking(localization["SystemCheck_Engine_Name"], localization["SystemCheck_Status_Checking"]),
            SystemCapability.Checking(localization["SystemCheck_Gpu_Name"], localization["SystemCheck_Status_Checking"])
        ];
    }

    public bool IsBusy { get; private set; } = true;

    public bool HasProblem { get; private set; }

    public string Summary { get; private set; }

    public IReadOnlyList<ISystemCapability> Capabilities { get; private set; }

    /// <summary>The run <see cref="Start" /> began; completed before the first start.</summary>
    internal Task Run => _run ?? Task.CompletedTask;

    /// <summary>Starts the check the first time; the answers cannot change within a process, so later calls reuse it.</summary>
    public Task Start() => _run ??= RunAsync();

    private async Task RunAsync()
    {
        var engine = Task.Run(ProbeEngine);
        var gpu = Task.Run(ProbeGpu);
        var engineCheck = await engine.ConfigureAwait(false);
        var gpuCheck = await gpu.ConfigureAwait(false);
        await _ui.InvokeAsync(() => Apply(engineCheck, gpuCheck)).ConfigureAwait(false);
    }

    private NativeEngineCheck ProbeEngine()
    {
        NativeEngineCheck check;
        try
        {
            check = _engine.Probe();
        }
        catch (Exception ex)
        {
            check = new NativeEngineCheck(NativeEngineState.Missing, Detail: ex.Message);
        }

        Log(check.State == NativeEngineState.Ready, $"System check: audio engine {check.State}. {check.Detail}");
        return check;
    }

    private GpuRenderCheck ProbeGpu()
    {
        GpuRenderCheck check;
        try
        {
            check = _gpu.Probe();
        }
        catch (Exception ex)
        {
            check = new GpuRenderCheck(GpuRenderState.NoVulkan, Detail: ex.Message);
        }

        Log(check.State == GpuRenderState.Ready, $"System check: graphics {check.State}. {check.Detail}");
        return check;
    }

    private void Log(bool ok, string message)
    {
        if (ok) _logger?.Info(AppLogCategory.Application, message);
        else _logger?.Warning(AppLogCategory.Application, message);
    }

    private void Apply(NativeEngineCheck engine, GpuRenderCheck gpu)
    {
        var rows = new[] { EngineRow(engine), GpuRow(gpu) };
        Capabilities = rows;
        HasProblem = rows.Any(r => r.IsUnavailable);
        Summary = _localization[HasProblem ? "SystemCheck_Problem" : "SystemCheck_Ready"];
        IsBusy = false;
        OnPropertyChanged(nameof(Capabilities));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsBusy));
    }

    private SystemCapability EngineRow(NativeEngineCheck check)
    {
        var name = _localization["SystemCheck_Engine_Name"];
        return check.State switch
        {
            NativeEngineState.Ready => SystemCapability.Ready(name, _localization["SystemCheck_Status_Ready"]),
            NativeEngineState.AbiMismatch => Off(name,
                _localization.Format("SystemCheck_Engine_AbiMismatch", check.ExpectedAbi, check.NativeAbi)),
            _ => Off(name, _localization["SystemCheck_Engine_Missing"])
        };
    }

    private SystemCapability GpuRow(GpuRenderCheck check)
    {
        var name = _localization["SystemCheck_Gpu_Name"];
        return check.State switch
        {
            GpuRenderState.Ready => SystemCapability.Ready(name, _localization["SystemCheck_Status_Ready"]),
            GpuRenderState.NoDevice => Off(name, _localization["SystemCheck_Gpu_NoDevice"]),
            GpuRenderState.NoInteropDevice => Off(name, _localization["SystemCheck_Gpu_NoInterop"]),
            _ => Off(name, _localization["SystemCheck_Gpu_NoVulkan"])
        };
    }

    private SystemCapability Off(string name, string reason) =>
        SystemCapability.Off(name, _localization["SystemCheck_Status_Off"], reason);
}

/// <summary>One immutable row of <see cref="SystemCheckViewModel" />.</summary>
public sealed class SystemCapability : ObservableObject, ISystemCapability
{
    private SystemCapability(string name, string statusText, bool isAvailable, bool isUnavailable, string reason)
    {
        Name = name;
        StatusText = statusText;
        IsAvailable = isAvailable;
        IsUnavailable = isUnavailable;
        Reason = reason;
    }

    public string Name { get; }

    public string StatusText { get; }

    public bool IsAvailable { get; }

    public bool IsUnavailable { get; }

    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public static SystemCapability Checking(string name, string status) => new(name, status, false, false, string.Empty);

    public static SystemCapability Ready(string name, string status) => new(name, status, true, false, string.Empty);

    public static SystemCapability Off(string name, string status, string reason) => new(name, status, false, true, reason);
}
