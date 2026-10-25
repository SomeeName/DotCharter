using System.Collections.Concurrent;
using DotCharter.Analysis.Lyrics;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Which lyric models have to stay on the CPU for the rest of the process.
///     <para>
///         The provider ladder (<see cref="OnnxSessionFactory" />) only falls back
///         when a session fails to CREATE. A graph can create on DirectML and still
///         fail its first <c>Run</c> (the int8 MMS-FA export does, on a Reshape node
///         DirectML cannot execute), so the failure is learned at run time and has
///         to be remembered somewhere: without it every later sync would load the
///         340 MB graph on the GPU, fail again and pay the retry again.
///     </para>
/// </summary>
public sealed class LyricProviderMemory
{
    private readonly ConcurrentDictionary<string, byte> _cpuOnly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The memory the app and the CLI share: one per process.</summary>
    public static LyricProviderMemory Process { get; } = new();

    /// <summary>Whether <paramref name="modelId" /> failed on a GPU provider and must open on the CPU.</summary>
    public bool IsCpuOnly(string modelId) => _cpuOnly.ContainsKey(modelId);

    /// <summary>Records the failure; true only for the first caller, which is the one that logs it.</summary>
    internal bool RememberCpu(string modelId) => _cpuOnly.TryAdd(modelId, 0);

    private readonly ConcurrentDictionary<string, OnnxExecutionProvider> _lastRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The provider <paramref name="modelId" />'s last finished encode ran on, or null before its first.</summary>
    public OnnxExecutionProvider? LastProvider(string modelId) =>
        _lastRun.TryGetValue(modelId, out var provider) ? provider : null;

    internal void RecordRun(string modelId, OnnxExecutionProvider provider) => _lastRun[modelId] = provider;

    /// <summary>
    ///     Settings ▸ AI &amp; models' "Retry GPU": the next session for
    ///     <paramref name="modelId" /> asks for the GPU again. True when a fallback was remembered.
    /// </summary>
    public bool ForgetCpu(string modelId) => _cpuOnly.TryRemove(modelId, out _);
}

/// <summary>
///     An <see cref="ILyricAcousticSession" /> that answers a failed run on a GPU
///     provider by re-opening the same model on the CPU and encoding the WHOLE song
///     again, then stays on the CPU (<see cref="LyricProviderMemory" />).
///     <para>
///         The retry is per encode, not per window: a window that failed on the
///         GPU says nothing about the windows already written, and a half-GPU,
///         half-CPU matrix would be a mix of two numerics. A failure on the CPU is
///         not retried and reaches the caller unchanged.
///     </para>
/// </summary>
internal sealed class LyricProviderFallbackSession : ILyricAcousticSession
{
    private readonly LyricModel _model;
    private readonly LyricAcousticSessionOpener _open;
    private readonly LyricProviderMemory _memory;
    private readonly Action<string>? _log;
    private readonly Action<string>? _warn;
    private ILyricAcousticSession? _inner;

    /// <param name="model">The model to open.</param>
    /// <param name="requested">The provider the caller asked for; ignored once the model is remembered as CPU-only.</param>
    /// <param name="open">Opens one session.</param>
    /// <param name="memory">Which models stay on the CPU.</param>
    /// <param name="log">Session-level log sink, handed to the opener.</param>
    /// <param name="warn">Receives the one line that says a GPU run failed and the CPU took over.</param>
    public LyricProviderFallbackSession(
        LyricModel model, OnnxExecutionProvider requested, LyricAcousticSessionOpener open,
        LyricProviderMemory memory, Action<string>? log, Action<string>? warn)
    {
        _model = model;
        _open = open;
        _memory = memory;
        _log = log;
        _warn = warn;
        _inner = open(model, memory.IsCpuOnly(model.Id) ? OnnxExecutionProvider.Cpu : requested, log);

        // The alphabet belongs to the graph, not to the provider it runs on.
        Vocabulary = _inner.Vocabulary;
    }

    public LyricVocabulary Vocabulary { get; }

    public OnnxExecutionProvider ActiveProvider => _inner?.ActiveProvider ?? OnnxExecutionProvider.Cpu;

    public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default)
    {
        try
        {
            var emissions = _inner!.Encode(mono16k, cancellationToken);
            _memory.RecordRun(_model.Id, ActiveProvider);
            return emissions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ActiveProvider != OnnxExecutionProvider.Cpu)
        {
            if (_memory.RememberCpu(_model.Id))
            {
                _warn?.Invoke(
                    $"Lyric model '{_model.Id}' failed on {ActiveProvider} ({ex.Message.Split('\n')[0].Trim()}); " +
                    "re-running it on the CPU, which it keeps for the rest of this session.");
            }

            // The GPU session is released before the CPU one opens: both hold the
            // 340 MB graph, and the GPU copy is dead weight now.
            var failed = _inner!;
            _inner = null;
            failed.Dispose();
            _inner = _open(_model, OnnxExecutionProvider.Cpu, _log);

            var emissions = _inner.Encode(mono16k, cancellationToken);
            _memory.RecordRun(_model.Id, ActiveProvider);
            return emissions;
        }
    }

    public void Dispose() => _inner?.Dispose();
}
