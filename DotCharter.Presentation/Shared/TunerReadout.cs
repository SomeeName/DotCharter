using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Services.Audio.Tuning;

namespace DotCharter.Presentation.Shared;

/// <summary>
///     The Tools popover's one-line tuner readout over the shared <see cref="LiveTuner" />:
///     "In tune", the signed cents, or an em dash while nothing is heard.
/// </summary>
public sealed class TunerReadout : ObservableObject
{
    private readonly LiveTuner _tuner;
    private readonly ILocalizationService _localization;

    public TunerReadout(LiveTuner tuner, ILocalizationService localization)
    {
        _tuner = tuner;
        _localization = localization;
        tuner.Changed += () =>
        {
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsSilent));
            OnPropertyChanged(nameof(IsInTune));
        };
    }

    public string Text =>
        !_tuner.HasReading ? "—"
        : _tuner.IsInTune ? _localization["PlayMode_GetReady_Tuner_InTune"]
        : Screens.Dialogs.TunerViewModel.FormatCents(_tuner.CentsOff, CultureInfo.CurrentCulture);

    public bool IsSilent => !_tuner.HasReading;

    public bool IsInTune => _tuner.HasReading && _tuner.IsInTune;

    /// <summary>Joins detection without resetting the strings; null (and no status message) when there is nothing to tune.</summary>
    public IDisposable? Begin() => _tuner.Acquire(fresh: false);
}
