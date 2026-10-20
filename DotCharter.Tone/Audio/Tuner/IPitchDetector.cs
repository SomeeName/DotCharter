namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     One pitch estimate for a single analysis window.
///     <see cref="FrequencyHz"/> &lt;= 0 (i.e. <c>default</c>) means "no pitch"
///     — silence, noise, or a reading the detector's gates rejected.
/// </summary>
public readonly record struct PitchReading(double FrequencyHz, double Clarity)
{
    /// <summary>True when this reading carries a usable pitch.</summary>
    public bool HasPitch => FrequencyHz > 0;

    /// <summary>The canonical "no pitch" reading.</summary>
    public static PitchReading None => default;
}

/// <summary>
///     A monophonic pitch detector over fixed-size sample windows.
///     <para>
///     <b>0.18 tone-rig framing:</b> this is the MANAGED slice of the 0.18
///     tuner suite. The full tone rig is native-first (JuceSharp.Native C ABI,
///     0.18 Phase 0), but these implementations ship value now off the
///     existing managed input-capture tap
///     (<c>IAudioPlaybackService.ReadInputCaptureWindow</c> +
///     <c>InputSampleRate</c>) and serve as the validated REFERENCE
///     implementation: when Phase 0 graduates the tuner to native, the unit
///     tests over these classes carry over as the acceptance contract.
///     </para>
///     <para>
///     Real-time discipline applies even here: <see cref="Detect"/> is
///     span-based and allocation-free after construction (all scratch buffers
///     are preallocated), with no LINQ/closures on the per-window path.
///     </para>
/// </summary>
public interface IPitchDetector
{
    /// <summary>Sample rate the detector was built for.</summary>
    int SampleRate { get; }

    /// <summary>Exact number of samples <see cref="Detect"/> expects.</summary>
    int WindowSize { get; }

    /// <summary>
    ///     Analyzes one window of mono samples (exactly
    ///     <see cref="WindowSize"/> long) and returns the detected pitch, or
    ///     <see cref="PitchReading.None"/> when gated out. Allocation-free.
    /// </summary>
    PitchReading Detect(ReadOnlySpan<float> window);
}
