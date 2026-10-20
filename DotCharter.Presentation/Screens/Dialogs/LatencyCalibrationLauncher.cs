using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     <see cref="ILatencyCalibrationPrompt" /> over the hosted wizard: Settings ▸ Audio,
///     the first-run wizard, a guest's first run and the palette all open it through
///     here, and the Graphics tab hears an applied lead from <see cref="CalibrationApplied" />.
///     A failure to open is logged here, so every caller may fire and forget.
/// </summary>
public sealed class LatencyCalibrationLauncher(IDialogs dialogs, IAppLogger logger) : ILatencyCalibrationPrompt
{
    public event EventHandler<double>? CalibrationApplied;

    public async Task OpenAsync()
    {
        double? lead;
        try
        {
            lead = await dialogs.ShowAsync<ILatencyWizardScreen, double?>().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Audio, "The latency wizard could not open.", ex);
            return;
        }

        if (lead is { } applied) CalibrationApplied?.Invoke(this, applied);
    }
}
