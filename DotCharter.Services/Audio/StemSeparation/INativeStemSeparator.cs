using JuceSharp;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     The native (demucs.cpp) separation entry point, behind an interface.
///     <para>
///         <b>This exists so the parent's HANDLING of what the wrapper throws is
///         testable.</b> ABI v36 made the native run cancellable and gave
///         "the weights are not on disk" its own answer, so
///         <see cref="JuceStemSeparationService" /> now has three outcomes to get
///         right — a cancel that must PROPAGATE, a missing model that must become
///         the download offer, and everything else that becomes a failed result.
///         Reaching any of them through the real <see cref="JuceStemSeparation" />
///         needs a <c>-DDOTCHARTER_STEMS_DEMUCS_CPP=ON</c> build, a several
///         hundred MB weights file and minutes of inference; reaching them through
///         a fake is a unit test.
///     </para>
/// </summary>
internal interface INativeStemSeparator
{
    /// <summary>Whether this build has the native backend at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    ///     Runs one blocking separation.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> was
    ///     signalled — the run STOPPED, it did not fail.</exception>
    /// <exception cref="FileNotFoundException"><paramref name="modelPath" /> does
    ///     not exist. A download, not a bug.</exception>
    void Separate(string inputAudioPath, string outputDirectory, string modelPath, int numThreads,
        Action<float, string>? onProgress, CancellationToken ct);
}

/// <summary>The product implementation: a direct call into the wrapper, with no
/// logic of its own. Stateless, so one instance serves every caller.</summary>
internal sealed class JuceNativeStemSeparator : INativeStemSeparator
{
    internal static readonly JuceNativeStemSeparator Instance = new();

    public bool IsSupported => JuceStemSeparation.IsSupported;

    public void Separate(string inputAudioPath, string outputDirectory, string modelPath, int numThreads,
        Action<float, string>? onProgress, CancellationToken ct) =>
        // The SIX-argument overload. The five-argument one passes
        // CancellationToken.None internally, which is the whole bug.
        JuceStemSeparation.Separate(inputAudioPath, outputDirectory, modelPath, numThreads, onProgress, ct);
}
