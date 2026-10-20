namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     Opens the latency / A-V calibration flow — the "Calibrate…" button beside
///     the read-only Measure control on Settings ▸ Audio, and the same flow the
///     first-run wizard offers.
///     <para>
///         The flow itself is a hosted dialog; this is the one verb its callers
///         need, declared where they can reach it. Same shape as
///         <c>DotCharter.Services.Diagnostics.IMessagePrompt</c>: a layer
///         below declares the prompt, the dialog's launcher implements it.
///     </para>
/// </summary>
public interface ILatencyCalibrationPrompt
{
    /// <summary>Completes when the wizard closes, however it closed; the first-run wizard re-reads its summary then.</summary>
    Task OpenAsync();

    /// <summary>
    ///     Raised with the visual lead in milliseconds when the player accepts
    ///     an A-V result. The flow writes that number to the config itself, so a
    ///     surface holding its own cached copy — the Graphics tab's lead slider
    ///     — has to hear about it or the next save puts the stale value back.
    /// </summary>
    event EventHandler<double>? CalibrationApplied;
}
