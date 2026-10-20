using System.Globalization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>
///     #451: no converted Rocksmith delay may regenerate. A delay's repeats must die
///     away however far its Feedback knob is turned, because Rocksmith's own delays
///     do. Rendered through the REAL native rig (<see cref="RsToneFidelityHarness" />)
///     so a loop gain the managed mapping cannot see — saturation, filter
///     resonance, a module's own regen curve — fails here too.
///     <para>
///         The case that broke: <c>Pedal_AnalogueDelay</c> routes to <c>bbddelay</c>,
///         whose regen curve is built to self-oscillate over the top fifth of its
///         travel (loop gain 1.0 at feedback ≈ 0.82, dcdsp_bbddelay.h). The
///         converter fed it the RS percentage straight, so the pedal's own
///         catalogue default (Feedback 92) became a runaway loop.
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class RsDelayRegenerationTests : IClassFixture<RsDelayRegenerationTests.Fixture>
{
    private const int Sr = ReferenceDi.SampleRate;
    private const int BurstSamples = Sr / 10;
    private const int TailSeconds = 8;

    private readonly Fixture _fx;

    public RsDelayRegenerationTests(Fixture fx) => _fx = fx;

    /// <summary>Every catalogue gear the converter routes as a delay.</summary>
    public static TheoryData<string> DelayGear =>
    [
        .. RsGearCatalog.Shared.AllGear
            .Where(g => g.Category == "Delay" && g.Knobs is { Count: > 0 })
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    ///     The Delay tone of "It's Raining Nevertheless" (Tuyu), rebuilt knob for knob
    ///     from the package, through the product's own apply path (input gate
    ///     prepended, as <c>RsTonePlaybackCoordinator</c> does). Before the fix its
    ///     tail rose from -28 to +1 dBFS over ten seconds of silence.
    /// </summary>
    [Fact]
    public void Issue451_RainingNevertheless_DelayTone_TailDecays()
    {
        RequireNative();
        var preset = RsInputGate.Prepend(RsToneFidelityTones.Convert(
            "Amp_MarshallDSL100H", "Cab_Marshall1960TV_Ribbon_Cone",
            RsToneFidelityTones.Pots(("Bass", 80), ("Gain", 30), ("Mid", 65), ("Pres", 30), ("Res", 30), ("Treble", 70)),
            prePedals: [("Pedal_Compression", RsToneFidelityTones.Pots(("Attack", 30), ("Comp", 66), ("Release", 200)))],
            racks:
            [
                ("Rack_StudioGraphicEQ", null),
                ("Rack_StudioChamber", RsToneFidelityTones.Pots(("Depth", 80), ("Mix", 50), ("Time", 66), ("Tone", 60)))
            ],
            postPedals: [("Pedal_AnalogueDelay", RsToneFidelityTones.Pots(("Feedback", 92), ("Mix", 55), ("Time", 320)))]));

        var windows = TailWindowsDb(preset);

        // Measured after the fix: -28 to -45 dBFS, a steady ~2.4 dB a second.
        AssertDecays(windows, minDropDb: 12, "Delay (#451)");
    }

    /// <summary>
    ///     Every delay gear at the worst setting a tone can carry: Feedback and Mix at
    ///     the top of their knobs and every time knob at its MINIMUM — the most laps
    ///     per second, and (for the BBD) the brightest, least lossy loop.
    /// </summary>
    [Theory]
    [MemberData(nameof(DelayGear))]
    public void DelayGear_AtFullFeedback_TailDecays(string gearKey)
    {
        RequireNative();
        Assert.True(RsGearCatalog.Shared.TryGetGear(gearKey, out var gear));
        var pots = new List<(string, float)>();
        foreach (var knob in gear.Knobs!)
        {
            if (knob.Pot is "Feedback" or "Regen" or "Repeats" or "Mix")
                pots.Add((knob.Pot, knob.MaxValue));
            else if (knob.Pot is "Time" or "TimeL" or "TimeR" or "Delay")
                pots.Add((knob.Pot, knob.MinValue));
        }

        Assert.Contains(pots, p => p.Item1 is "Feedback" or "Regen" or "Repeats");

        var pedal = (gearKey, (IReadOnlyDictionary<string, float>?)RsToneFidelityTones.Pots([.. pots]));
        var isRack = gear.Type.StartsWith("Rack", StringComparison.Ordinal);
        var preset = RsToneFidelityTones.Convert(
            "Amp_MarshallDSL100H", "Cab_Marshall1960TV_Ribbon_Cone",
            racks: isRack ? [pedal] : null,
            postPedals: isRack ? null : [pedal]);

        AssertDecays(TailWindowsDb(preset), minDropDb: 12, gearKey);
    }

    /// <summary>
    ///     The pure half: the converter's BBD mapping inverts the native regen curve,
    ///     so the RS percentage IS the per-repeat gain (as it is on <c>delay</c> and
    ///     <c>tapedelay</c>) and never reaches the self-oscillating top of the knob.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    [InlineData(0.92f)]
    [InlineData(1f)]
    public void BbdFeedback_InvertsTheNativeRegenCurve(float rsRegen)
    {
        var feedback = RsToneConverter.BbdFeedbackForRegen(rsRegen);
        var regen = RsToneConverter.BbdRegen(feedback);
        Assert.Equal(Math.Min(rsRegen, RsToneConverter.RsMaxDelayRegen), regen, 0.001f);
        Assert.True(regen < 1f, $"regen {regen} at RS {rsRegen}");
    }

    private static void AssertDecays(double[] windows, double minDropDb, string what)
    {
        var series = string.Join(" ", windows.Select(w => w.ToString("0.0", CultureInfo.InvariantCulture)));
        var first = windows[0];
        Assert.True(first > -80, $"{what}: no echo reached the output (first-second peak {first:0.0} dBFS) — the render measured nothing\nper-second peaks: {series}");
        Assert.True(windows.Max() <= first + 1.0, $"{what}: the tail GROWS — a delay loop gain at or above 1\nper-second peaks (dBFS): {series}");
        Assert.True(windows[^1] <= first - minDropDb,
            $"{what}: the tail did not die away ({first - windows[^1]:0.0} dB over {TailSeconds - 1} s, want >= {minDropDb})\nper-second peaks (dBFS): {series}");
    }

    /// <summary>A 100 ms 220 Hz pluck, then silence: the peak (dBFS) of each whole
    /// second of the tail that follows it.</summary>
    private double[] TailWindowsDb(DcRigPreset preset)
    {
        var input = new float[BurstSamples + (TailSeconds * Sr)];
        for (var i = 0; i < BurstSamples; i++)
            input[i] = 0.12f * MathF.Sin(2 * MathF.PI * 220 * i / Sr) * (1f - ((float)i / BurstSamples));

        var output = _fx.Harness.RenderInput(preset, input);
        var windows = new double[TailSeconds];
        for (var w = 0; w < TailSeconds; w++)
        {
            var peak = 0f;
            for (var i = BurstSamples + (w * Sr); i < BurstSamples + ((w + 1) * Sr); i++)
                peak = MathF.Max(peak, MathF.Abs(output[i]));
            windows[w] = 20 * Math.Log10(peak + 1e-20);
        }

        return windows;
    }

    private void RequireNative()
        => Assert.SkipWhen(!_fx.Harness.Available, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

    public sealed class Fixture : IDisposable
    {
        public RsToneFidelityHarness Harness { get; } = new();

        public void Dispose() => Harness.Dispose();
    }
}
