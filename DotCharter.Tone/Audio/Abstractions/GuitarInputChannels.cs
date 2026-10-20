using DotCharter.Localization;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>One physical input jack of the audio interface, as a picker row.
/// <see cref="Jack" /> is the driver's 0-based channel index — the number
/// <c>RigConfig.AsioInputChannelLeft</c> / <c>Right</c> store — and the label is
/// what the user reads (1-based, "Input 2").</summary>
public sealed record InputJackOption(int Jack, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
///     The ONE reading of "which jack is the guitar on" that every surface
///     shares — Settings ▸ Audio, the onboarding wizard's Input page, the Amp &amp;
///     FX rig's footer and the latency wizard.
///     <para>
///         <b>Why this exists.</b> The owner's guitar sits on the
///         interface's SECOND input. The only place that could say so was a pair
///         of unlabeled index boxes in Settings ▸ Audio; the rig — where the amp
///         is picked and where "no sound" is noticed — offered nothing, and the
///         first-run wizard's Input page opened the device and showed a meter
///         without ever asking which jack. Every surface now reads and writes
///         the same three <c>RigConfig</c> fields through these helpers and
///         re-applies the engine through <c>ApplyAudioConfiguration</c>, which is
///         the single writer of the live seat baseline
///         (<see cref="LiveInputSeatLayout.FromInputConfig" />) — this type adds
///         no second one.
///     </para>
///     <para>
///         Pure and engine-free: the device's jack COUNT is handed in by the
///         caller (<c>IAudioPlaybackService.GetInputDeviceChannelCount</c>), so
///         the option list is a function of config + count and unit-testable.
///     </para>
/// </summary>
public static class GuitarInputChannels
{
    /// <summary>Jacks offered when the device's count is unknown (no engine, an
    /// enumeration that failed) — enough for any common interface, and the
    /// configured jacks are always appended on top.</summary>
    public const int FallbackJackCount = 8;

    /// <summary>True when the jack INDEXES do anything: the engine builds an
    /// input channel mask only for ASIO (<c>RigConfig.AudioDriver</c>, the engine
    /// driver — ASIO is single-driver-exclusive, so it is the input driver too).
    /// WASAPI opens the device's first channels in order.</summary>
    public static bool JackIndexesAreUsed(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.EnableInput
               && string.Equals(config.AudioDriver, "ASIO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many input channels the engine OPENS for this config —
    /// mirrors <c>JuceAudioPlaybackService.ApplyAudioConfigurationCore</c>'s
    /// <c>InputChannels</c> (Mono ? 1 : 2, 0 with input off).</summary>
    public static int OpenChannelCount(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.EnableInput) return 0;
        return config.AsioInputMono ? 1 : 2;
    }

    /// <summary>The jack a SEAT's guitar is plugged into: seat 0 is the "left"
    /// box, seat 1 the "right" one. With one channel open only seat 0 exists.</summary>
    public static int JackForSeat(RigConfig config, int seat)
    {
        ArgumentNullException.ThrowIfNull(config);
        return seat <= 0 || config.AsioInputMono ? config.AsioInputChannelLeft : config.AsioInputChannelRight;
    }

    /// <summary>
    ///     Points a seat at a jack. The two boxes must name two DIFFERENT jacks
    ///     while two channels are open (a mask with one bit opens one channel and
    ///     the second seat reads a channel that does not exist), so choosing the
    ///     jack the OTHER box already holds swaps them rather than collapsing
    ///     them. Returns true when anything changed.
    /// </summary>
    public static bool SelectJackForSeat(RigConfig config, int seat, int jack)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(jack);
        var left = config.AsioInputChannelLeft;
        var right = config.AsioInputChannelRight;
        if (seat <= 0 || config.AsioInputMono)
        {
            if (left == jack) return false;
            config.AsioInputChannelLeft = jack;
            if (!config.AsioInputMono && right == jack) config.AsioInputChannelRight = left;
            return true;
        }

        if (right == jack) return false;
        config.AsioInputChannelRight = jack;
        if (left == jack) config.AsioInputChannelLeft = right;
        return true;
    }

    /// <summary>
    ///     The picker rows: every jack of the device (0..count-1), plus whatever
    ///     the config currently names even if it lies beyond the count — a saved
    ///     jack must never vanish from the list and silently read as "none".
    /// </summary>
    public static IReadOnlyList<InputJackOption> JackOptions(ILocalizationService localization, int deviceJackCount,
        params int[] alwaysInclude)
    {
        var count = deviceJackCount > 0 ? deviceJackCount : FallbackJackCount;
        var jacks = new SortedSet<int>();
        for (var j = 0; j < count; j++) jacks.Add(j);
        foreach (var j in alwaysInclude)
        {
            if (j >= 0)
                jacks.Add(j);
        }

        return jacks.Select(j => new InputJackOption(j, JackLabel(localization, j))).ToList();
    }

    /// <summary>"Input 2" for jack 1 — one label everywhere, so the rig, the
    /// wizard and Settings can never disagree about which jack is which.</summary>
    public static string JackLabel(ILocalizationService localization, int jack) =>
        localization.Format("InputJack_LabelFmt", jack + 1);

    /// <summary>
    ///     The jack behind an index into the OPEN channel set. ASIO builds a mask
    ///     and JUCE compacts the enabled bits ASCENDING, so with two channels open
    ///     index 0 is the LOWER of the two jacks whichever box it was typed in;
    ///     without a mask (WASAPI) the open index is the jack.
    /// </summary>
    public static int JackForOpenChannel(RigConfig config, int openChannel)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!JackIndexesAreUsed(config)) return Math.Max(0, openChannel);
        if (config.AsioInputMono) return config.AsioInputChannelLeft;
        var lo = Math.Min(config.AsioInputChannelLeft, config.AsioInputChannelRight);
        var hi = Math.Max(config.AsioInputChannelLeft, config.AsioInputChannelRight);
        return openChannel <= 0 ? lo : hi;
    }

    /// <summary>
    ///     The open channel seat 0's capture ring reads under this config — what
    ///     live judging, the tuner and the latency calibration all listen to when
    ///     nothing has re-routed the seat. It is
    ///     <see cref="LiveInputSeatLayout.FromInputConfig" />'s answer, read
    ///     rather than re-derived.
    /// </summary>
    public static int SeatZeroOpenChannel(RigConfig config) =>
        LiveInputSeatLayout.FromInputConfig(config).Channels[0];

    /// <summary>
    ///     The order the loopback calibration listens in: the preferred channel
    ///     first, then every other OPEN channel ascending. A guitar on the
    ///     second jack of a stereo-open interface would fail the calibration
    ///     outright — the analysed ring would be channel 0's — while the input meter
    ///     visibly lit; so the run falls back to the other channel and says so.
    /// </summary>
    public static IReadOnlyList<int> CalibrationChannelOrder(int preferredOpenChannel, int openChannelCount)
    {
        var count = Math.Max(1, openChannelCount);
        var preferred = Math.Clamp(preferredOpenChannel, 0, count - 1);
        var order = new List<int>(count) { preferred };
        for (var c = 0; c < count; c++)
        {
            if (c != preferred)
                order.Add(c);
        }

        return order;
    }
}
