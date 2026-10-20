using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation.Gpu;

/// <summary>Why a stem separation runs where it runs.</summary>
public enum StemExecutionReason
{
    /// <summary>DirectML on the chosen (or automatically chosen) adapter.</summary>
    DirectMl,

    /// <summary>The user picked the CPU.</summary>
    UserChoseCpu,

    /// <summary>The loaded ONNX Runtime has no DirectML provider.</summary>
    NoDirectMlInRuntime,

    /// <summary>No hardware adapter with enough video memory.</summary>
    NoUsableGpu
}

/// <summary>Where a separation runs: the provider, its device id and the reason, for the log and the status line.</summary>
/// <param name="Provider">The provider requested from the session factory.</param>
/// <param name="DeviceId">The DirectML device id (DXGI adapter index); 0 on the CPU.</param>
/// <param name="Reason">Why the run is on this provider.</param>
/// <param name="Adapter">The chosen adapter, or null on the CPU or without an adapter list.</param>
/// <param name="GpuChoiceReason">Why this adapter (<see cref="StemGpuChoice.Reason" />), or empty when no adapter list was read.</param>
public sealed record StemExecutionPlan(
    OnnxExecutionProvider Provider,
    int DeviceId,
    StemExecutionReason Reason,
    StemGpuAdapter? Adapter,
    string GpuChoiceReason = "");

/// <summary>
///     Pure decision for which provider and GPU the ONNX stem backend REQUESTS, given
///     the user's preference, whether the runtime has DirectML, the machine's adapters
///     and the stored GPU choice. The factory still falls back on its own, but asking
///     for the right thing up front is what puts the run on the GPU no display hangs on
///     (<see cref="StemGpuRanking" />) rather than on adapter 0 by accident, and what lets the status line say WHY a run is on
///     the CPU.
///     <para>
///         The app's ONNX Runtime is the DirectML build; there is no CUDA. A stored
///         "Cuda" (an older version's automatic choice) reads as automatic, which is
///         DirectML whenever a usable GPU exists.
///     </para>
/// </summary>
public static class StemExecutionDecision
{
    /// <param name="userPreference">The persisted provider ("Auto", "DirectML", "cpu", or a retired "Cuda"); case-insensitive.</param>
    /// <param name="directMlInRuntime">True when the loaded onnxruntime.dll reports <c>DmlExecutionProvider</c>.</param>
    /// <param name="adapters">Every adapter DXGI lists, software one included.</param>
    /// <param name="storedAdapterKey">The persisted GPU choice (<see cref="StemGpuRanking.KeyFor" />), or empty for the automatic choice.</param>
    public static StemExecutionPlan Decide(
        string? userPreference,
        bool directMlInRuntime,
        IReadOnlyList<StemGpuAdapter> adapters,
        string? storedAdapterKey)
    {
        if (OnnxExecutionProviderParser.Parse(userPreference) == OnnxExecutionProvider.Cpu)
            return new StemExecutionPlan(OnnxExecutionProvider.Cpu, 0, StemExecutionReason.UserChoseCpu, null);
        if (!directMlInRuntime)
            return new StemExecutionPlan(OnnxExecutionProvider.Cpu, 0, StemExecutionReason.NoDirectMlInRuntime, null);

        var choice = StemGpuRanking.Choose(StemGpuRanking.Rank(adapters), storedAdapterKey);
        return choice.Gpu == null
            ? new StemExecutionPlan(OnnxExecutionProvider.Cpu, 0, StemExecutionReason.NoUsableGpu, null, choice.Reason)
            : new StemExecutionPlan(OnnxExecutionProvider.DirectML, choice.Gpu.Adapter.DeviceId, StemExecutionReason.DirectMl,
                choice.Gpu.Adapter, choice.Reason);
    }

    /// <summary>Where a plan runs, for the log and the Settings status line (diagnostics, English).</summary>
    public static string Describe(StemExecutionPlan plan) => plan.Reason switch
    {
        StemExecutionReason.DirectMl when plan.Adapter != null =>
            $"DirectML on '{plan.Adapter.Name}' (adapter {plan.DeviceId}, {plan.Adapter.DedicatedVideoMemoryBytes / 1048576} MB)",
        StemExecutionReason.DirectMl => $"{plan.Provider} (no adapter list)",
        StemExecutionReason.UserChoseCpu => "CPU (chosen in Settings → Stem Models)",
        StemExecutionReason.NoDirectMlInRuntime => "CPU — this ONNX Runtime has no DirectML provider",
        _ => $"CPU — no DirectX 12 GPU with {StemGpuRanking.MinimumVideoMemoryBytes >> 30} GB of video memory"
    };

    /// <summary>
    ///     Why a run that asked for a GPU ended on the CPU — the separator's own reason, or
    ///     a generic one — and null when it did not. A GPU request that became a CPU run
    ///     must never look like a GPU run, so the service logs this and shows it.
    /// </summary>
    public static string? CpuFallbackReason(
        OnnxExecutionProvider requested, OnnxExecutionProvider active, string? separatorReason) =>
        requested != OnnxExecutionProvider.Cpu && active == OnnxExecutionProvider.Cpu
            ? separatorReason ?? "the GPU provider did not attach"
            : null;
}
