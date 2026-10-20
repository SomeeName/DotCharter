using DotCharter.Analysis.Lyrics;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     One loaded acoustic head, as <see cref="ILyricSyncCore" /> uses it: an
///     alphabet and a way to turn 16 kHz mono into CTC emissions.
///     <para>
///         <b>This exists so the orchestration can be tested at all.</b> The
///         shipping model is 340 MB and cannot run in the default gate, so every
///         rule the core enforces around it — the stem preference, the ensemble
///         degradation, the low-confidence count, the seconds it reports — was
///         only reachable by reading the source. A fake session producing a known
///         emission matrix makes those assertable
///         (<c>LyricSyncCoreTests</c>).
///     </para>
/// </summary>
public interface ILyricAcousticSession : IDisposable
{
    /// <summary>The head's own symbol alphabet — what <see cref="CtcEmissions.Width" /> counts.</summary>
    LyricVocabulary Vocabulary { get; }

    /// <summary>Runs the whole song and returns log-softmax emissions.</summary>
    CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default);
}

/// <summary>
///     Opens an <see cref="ILyricAcousticSession" /> for one installed model.
///     Defaults to <see cref="LyricAcousticSessionFactory.Default" />, which is
///     the real ONNX graph.
/// </summary>
/// <param name="model">The model to open.</param>
/// <param name="provider">Execution provider ladder to try.</param>
/// <param name="log">Session-level log sink, or null.</param>
public delegate ILyricAcousticSession LyricAcousticSessionOpener(
    LyricModel model, OnnxExecutionProvider provider, Action<string>? log);

/// <summary>The production opener: <see cref="LyricAcousticModel" /> on disk.</summary>
public static class LyricAcousticSessionFactory
{
    /// <summary>Loads the real ONNX graph and its vocabulary from the model's folder.</summary>
    public static ILyricAcousticSession Default(
        LyricModel model, OnnxExecutionProvider provider, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new OnnxSession(
            LyricAcousticModel.Load(model.ModelPath, model.VocabularyPath, provider, log));
    }

    /// <summary>
    ///     Adapter rather than an interface on <see cref="LyricAcousticModel" />
    ///     itself: that type is the ONNX wrapper and stays free of the
    ///     orchestration's test seams.
    /// </summary>
    private sealed class OnnxSession(LyricAcousticModel model) : ILyricAcousticSession
    {
        public LyricVocabulary Vocabulary => model.Vocabulary;

        public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default) =>
            model.Encode(mono16k, cancellationToken);

        public void Dispose() => model.Dispose();
    }
}
