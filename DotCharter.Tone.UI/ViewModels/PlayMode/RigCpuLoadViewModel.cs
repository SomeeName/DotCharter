using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The rig footer's CPU readout: the audio callback's load as the native engine
///     measured it INSIDE the callback (<c>dca_engine_get_callback_load</c>, ABI v46)
///     — the average as the number, the decaying peak in the tooltip, and a warning
///     face once the peak passes <see cref="HighPeak" /> of the buffer period.
///     <para>
///         It reads at most 4 times a second: <see cref="Tick" /> is driven by the
///         rig view's 20 Hz meter clock (which runs only while the screen is
///         attached and visible, on the UI thread) and reads the engine on every
///         <see cref="TicksPerRead" />th tick. With no engine it prints a dash and
///         says why — it never shows a number nothing computed
///         (docs/agents/ui/fire/unbacked-data.md).
///     </para>
/// </summary>
public sealed partial class RigCpuLoadViewModel : ObservableObject
{
    /// <summary>Meter-clock ticks per engine read: 20 Hz / 5 = 4 Hz.</summary>
    public const int TicksPerRead = 5;

    /// <summary>The peak load (fraction of the buffer period) from which the readout
    /// warns: the engine's own near-miss threshold.</summary>
    public const double HighPeak = 0.8;

    private readonly Func<AudioCallbackLoad?> _read;
    private readonly ILocalizationService _loc;
    private int _ticks;

    public RigCpuLoadViewModel(Func<AudioCallbackLoad?> read, ILocalizationService loc)
    {
        _read = read;
        _loc = loc;
        _toolTip = loc["Rig_CpuNoEngine"];
    }

    /// <summary>A reader over a device seam: null whenever it has no engine to ask.</summary>
    public static Func<AudioCallbackLoad?> Over(IAudioDeviceControl audio)
        => () => audio.TryGetCallbackLoad(out var load) ? load : null;

    /// <summary>The average load in percent ("14%", "99+" past the whole budget), or a
    /// dash with no engine.</summary>
    [ObservableProperty] private string _valueText = "–";

    /// <summary>What the number is, the peak beside it, or why there is none.</summary>
    [ObservableProperty] private string _toolTip;

    /// <summary>The peak has passed <see cref="HighPeak" />: a dropout is close.</summary>
    [ObservableProperty] private bool _isHigh;

    /// <summary>How many times the engine was actually asked (tests pin the rate).</summary>
    public int Reads { get; private set; }

    /// <summary>One meter-clock tick; reads the engine on every
    /// <see cref="TicksPerRead" />th one, starting with the first.</summary>
    public void Tick()
    {
        if (_ticks++ % TicksPerRead == 0) Refresh();
    }

    /// <summary>Reads the engine now and updates the readout.</summary>
    public void Refresh()
    {
        Reads++;
        if (_read() is not { } load)
        {
            ValueText = "–";
            IsHigh = false;
            ToolTip = _loc["Rig_CpuNoEngine"];
            return;
        }

        // Three characters at most: the footer has ~60 px left at 1920 and must stay
        // ONE line. A callback past its whole budget reads "99+" (and is amber by
        // then); the tooltip carries the real figure.
        var average = Percent(load.Average);
        ValueText = average >= 100 ? "99+" : average.ToString(CultureInfo.InvariantCulture) + "%";
        IsHigh = load.Peak >= HighPeak;
        ToolTip = _loc.Format("Rig_CpuTip", Percent(load.Average), Percent(load.Peak));
    }

    private static int Percent(double fraction)
        => double.IsFinite(fraction) ? (int)Math.Clamp(Math.Round(fraction * 100.0), 0, 999) : 0;
}
