using DotCharter.Localization;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>One output pair of the audio interface, as a picker row.
/// <see cref="Left" /> and <see cref="Right" /> are the driver's 0-based channel
/// indexes — the numbers <c>RigConfig.AsioOutputChannelLeft</c> / <c>Right</c>
/// store for ASIO and WASAPI alike, despite the names. A mono row (a device's
/// last odd output, or a one-output device) has both equal and opens one channel.</summary>
public sealed record OutputPairOption(int Left, int Right, string Label)
{
    public bool IsMono => Left == Right;

    public override string ToString() => Label;
}

/// <summary>
///     The ONE reading of "which output pair carries the master mix" that
///     Settings ▸ Audio and the onboarding wizard's output step share.
///     <para>
///         The engine takes the pair as an output channel bitmask on EVERY driver:
///         JUCE's ASIO device writes the n-th enabled output to the n-th named
///         channel, and its WASAPI device keeps a channel map from the enabled
///         bits to the endpoint's mix-format channels, so a headphone out on
///         outputs 3 and 4 is reachable in shared mode as well. Both devices pack
///         the enabled outputs, so the master bus always sees them as 0 and 1.
///     </para>
///     <para>
///         Pure and engine-free: the device's output COUNT is handed in by the
///         caller (<c>IAudioDeviceControl.GetOutputDeviceChannelCount</c>), so the
///         rows and the mask are functions of config + count and unit-testable.
///     </para>
/// </summary>
public static class OutputChannelPairs
{
    /// <summary>Outputs offered when the device's count is unknown (no engine,
    /// an enumeration that failed) — enough for any common interface; the
    /// configured pair is always appended on top.</summary>
    public const int FallbackOutputCount = 8;

    /// <summary>The engine's output channel mask is 32 bits wide, so a device
    /// reporting more outputs offers the first 32: a channel the mask cannot
    /// name would open the device's first channels instead.</summary>
    public const int MaxOutputs = 32;

    /// <summary>The output channels the engine opens: the configured left and
    /// right as a bitmask (one bit when they are the same channel). A channel
    /// past <see cref="MaxOutputs" /> adds no bit; no bit at all lets the
    /// channel count pick the first pair.</summary>
    public static uint ChannelMask(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Bit(config.AsioOutputChannelLeft) | Bit(config.AsioOutputChannelRight);
    }

    /// <summary>
    ///     <see cref="ChannelMask(RigConfig)" /> checked against the device that
    ///     will open: when a chosen channel lies past the device's output count (a
    ///     pair picked on a 20-output ASIO interface, then a switch to a 2-output
    ///     WASAPI endpoint) JUCE drops the bit and the device would open with
    ///     fewer channels than the pair names, or none, so the mask falls back to
    ///     0 and the first pair opens. An unknown count (0) leaves the mask alone.
    ///     The default pair on a one-output device opens that output: it is the
    ///     device's first "pair", not a choice that went missing.
    /// </summary>
    public static uint ChannelMask(RigConfig config, int deviceOutputCount)
    {
        if (deviceOutputCount == 1 && IsDefaultPair(config)) return Bit(0);
        return PairPastDevice(config, deviceOutputCount) ? 0u : ChannelMask(config);
    }

    /// <summary>True when a channel of the configured pair lies at or past a
    /// KNOWN device output count (<paramref name="deviceOutputCount" /> &gt; 0).
    /// Never for the default pair: outputs 1 and 2 are what an untouched config
    /// names, so a one-output device opening its one output is no fallback and
    /// earns no WARN.</summary>
    public static bool PairPastDevice(RigConfig config, int deviceOutputCount)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (deviceOutputCount <= 0 || IsDefaultPair(config)) return false;
        return config.AsioOutputChannelLeft >= deviceOutputCount || config.AsioOutputChannelRight >= deviceOutputCount;
    }

    /// <summary>Outputs 1 and 2, the pair a config names until the user picks another.</summary>
    public static bool IsDefaultPair(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AsioOutputChannelLeft == 0 && config.AsioOutputChannelRight == 1;
    }

    /// <summary>The WARN line for a pair past the device's output count.</summary>
    public static string PastDeviceMessage(RigConfig config, string deviceName, int deviceOutputCount) =>
        $"Output pair {config.AsioOutputChannelLeft + 1}/{config.AsioOutputChannelRight + 1} is past the " +
        $"{deviceOutputCount} outputs of '{deviceName}'; opening the first output pair instead.";

    private static uint Bit(int channel) => channel is >= 0 and < MaxOutputs ? 1u << channel : 0u;

    /// <summary>
    ///     The picker rows: the device's outputs taken two at a time (0+1, 2+3, …),
    ///     a single mono row for a last odd output or a one-output device, plus the
    ///     pair the config currently names even when it is not one of those (an
    ///     odd-aligned pair typed before the picker existed, or a pair past the
    ///     device) — a saved pair must never vanish from the list and silently
    ///     read as "none".
    /// </summary>
    /// <remarks>The channel names are the open device's (<see cref="ChannelNames" />);
    /// null or empty gives numbered rows only.</remarks>
    public static IReadOnlyList<OutputPairOption> PairOptions(ILocalizationService localization, int deviceOutputCount,
        int configuredLeft, int configuredRight, IReadOnlyList<string>? channelNames = null)
    {
        var count = deviceOutputCount > 0 ? Math.Min(deviceOutputCount, MaxOutputs) : FallbackOutputCount;
        var pairs = new List<(int Left, int Right)>();
        for (var c = 0; c < count; c += 2)
            pairs.Add(c + 1 < count ? (c, c + 1) : (c, c));

        if (configuredLeft >= 0 && configuredRight >= 0 && !pairs.Contains((configuredLeft, configuredRight)))
        {
            pairs.Add((configuredLeft, configuredRight));
            pairs.Sort();
        }

        return pairs.Select(p => new OutputPairOption(p.Left, p.Right, PairLabel(localization, p.Left, p.Right, channelNames)))
            .ToList();
    }

    /// <summary>"Out 3 + 4" for channels 2 and 3, "Out 5 (mono)" for a mono row, each
    /// followed by the driver's name for the pair when it has one ("Out 3 + 4 ·
    /// Headphones") — one label everywhere, so Settings and the wizard never disagree.</summary>
    public static string PairLabel(ILocalizationService localization, int left, int right,
        IReadOnlyList<string>? channelNames = null)
    {
        var number = left == right
            ? localization.Format("OutputPair_MonoLabelFmt", left + 1)
            : localization.Format("OutputPair_LabelFmt", left + 1, right + 1);
        return ChannelNames.WithName(localization, number, ChannelNames.PairName(channelNames, left, right));
    }

    /// <summary>Points the config at a pair. Returns true when anything changed.</summary>
    public static bool SelectPair(RigConfig config, OutputPairOption pair)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pair);
        if (config.AsioOutputChannelLeft == pair.Left && config.AsioOutputChannelRight == pair.Right) return false;
        config.AsioOutputChannelLeft = pair.Left;
        config.AsioOutputChannelRight = pair.Right;
        return true;
    }
}
