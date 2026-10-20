namespace DotCharter.Services.Infrastructure.Shell;

/// <summary>
///     Opens the first-run setup flow — the shell's startup hook when the app
///     has never been configured, and Settings ▸ General's "Run setup again".
///     <para>
///         The flow is a wizard the UI owns; this is the one verb its callers
///         need, declared where they can reach it. Same shape as
///         <see cref="DotCharter.Services.Diagnostics.IMessagePrompt" /> and
///         <see cref="DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt" />.
///     </para>
/// </summary>
public interface IFirstRunSetupPrompt
{
    /// <summary>Opens the wizard at its first page; a re-run never inherits a previous run's skips.</summary>
    void Open();
}
