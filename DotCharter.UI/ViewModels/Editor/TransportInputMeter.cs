// The Mix popover's INPUT LEVEL meter: the capture peak, its readout and its clip flag, off the engine's multi-consumer tap.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     `Play Menus.dc.html`'s INPUT LEVEL meter as its own collaborator of
///     <see cref="TransportViewModel" /> (exposed as <c>InputMeter</c>). A
///     property either reads the engine or does not exist: a drawn control that
///     does nothing is the registered-but-dead shape the root guide forbids.
/// </summary>
public sealed partial class TransportInputMeter : ObservableObject
{
    private readonly IAudioPlaybackService _audioService;
    private readonly ILocalizationService _localization;

    /// <summary>The window the peak is taken over. Same size as
    /// <c>LivePlayViewModel</c>'s probe, and for the same reason: it is the
    /// windowed (multi-consumer) tap, so reading it cannot starve live note
    /// detection.</summary>
    private readonly float[] _probe = new float[2048];

    public TransportInputMeter(IAudioPlaybackService audioService, ILocalizationService localization)
    {
        _audioService = audioService;
        _localization = localization;
    }

    /// <summary>
    ///     Live capture peak in dBFS. <see cref="double.NegativeInfinity" />
    ///     until something is heard — the meter's own floor decides how that
    ///     draws, exactly as on the amp rig's header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeakLabel))]
    [NotifyPropertyChangedFor(nameof(IsClipping))]
    private double _levelDb = double.NegativeInfinity;

    /// <summary>
    ///     The mockup's `−14.2 dB PEAK`, from the real number. Silence prints an
    ///     em dash rather than `-∞ dB`, and the minus sign is U+2212 because the
    ///     readout is mono and a hyphen sits a pixel high in IBM Plex.
    /// </summary>
    public string PeakLabel =>
        double.IsNegativeInfinity(LevelDb) || LevelDb < -90
            ? _localization["Toolbar_Mix_NoInput"]
            : string.Format(CultureInfo.InvariantCulture, "{0}{1:0.0} dB PEAK",
                LevelDb < 0 ? "−" : "+", Math.Abs(LevelDb));

    /// <summary>Digital full scale is 0 dBFS, so at or above it the converter
    /// could not represent the sample. A real threshold on a real peak.</summary>
    public bool IsClipping => LevelDb >= 0.0;

    /// <summary>
    ///     One tick of the Mix popover's meter. Driven by the VIEW's clock,
    ///     which runs only while that popover is open — never by a timer inside
    ///     a view model that outlives the surface (the amp rig's rule, and the
    ///     reason the app does not accumulate meter clocks).
    /// </summary>
    public void Poll()
    {
        var read = _audioService.ReadInputCaptureWindow(_probe);
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var a = Math.Abs(_probe[i]);
            if (a > peak) peak = a;
        }

        LevelDb = peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);
    }
}
