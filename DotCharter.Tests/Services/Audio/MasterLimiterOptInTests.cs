using DotCharter.Tone.Audio.Engine;
namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The master-bus limiter is an OPT-IN, and the opt-in has to be on the path
///     that opens the engine.
///     <para>
///         JuceSharp's limiter (ABI v34) was default-ON and became default-OFF in
///         v35, for the engine's own "a user must not pay for, or hear any change
///         from, a feature they are not using" rule — the stage sits on the live
///         monitor path. The app had been relying on that default, so following
///         the submodule without calling <c>SetMasterLimiter</c> anywhere would
///         have shipped an UNBOUNDED master bus to the driver and, worse, to the
///         streaming mirror, which carries FLOAT: samples past ±1.0 survive into
///         OBS / Discord and wrap in their encoder. Nothing on screen would have
///         said so.
///     </para>
///     <para>
///         The REPLAY half is enumerated by <c>EngineRebuildStateReplayTests</c>
///         the moment the service writes the setter at all (the candidate list is
///         reflected off <c>JuceAudioEngine</c>), so this file pins what that one
///         cannot: that the write is REACHED from the config-apply path, that it
///         comes from the user's configuration rather than a literal, and the
///         latency-cache drop the toggle needs. Source-asserted, like its
///         siblings: reaching any of it at runtime needs a real audio device.
///     </para>
/// </summary>
public sealed class MasterLimiterOptInTests
{
    // ----- the derivation (pure) ---------------------------------------------

    [Fact]
    public void TheShippedDefault_IsOn_AtTheEnginesOwnCeilingAndRelease()
    {
        var setting = MasterLimiterSetting.FromConfig(new AppConfig());

        Assert.True(setting.Enabled,
            "A fresh AppConfig must opt IN: the engine ships the limiter off, so a default of false " +
            "means the app hands the driver and the float streaming mirror an unbounded bus.");
        Assert.Equal(-0.3f, setting.CeilingDb);
        Assert.Equal(80f, setting.ReleaseMs);
        Assert.Equal(MasterLimiterSetting.Default, setting);
    }

    [Fact]
    public void TheUsersSwitch_IsHonoured()
    {
        var setting = MasterLimiterSetting.FromConfig(new AppConfig { EnableMasterLimiter = false });
        Assert.False(setting.Enabled);
        // …and the ceiling still travels, so re-enabling it does not silently
        // land on the shipped default instead of the user's own number.
        Assert.Equal(-0.3f, setting.CeilingDb);
    }

    [Theory]
    [InlineData(-0.3f, -0.3f)]
    [InlineData(-6f, -6f)]
    [InlineData(-24f, -24f)]
    [InlineData(0f, 0f)]
    [InlineData(-100f, -24f)]   // below the native floor
    [InlineData(12f, 0f)]       // above unity
    public void TheCeiling_IsClampedToTheNativeRange(float configured, float expected)
    {
        var setting = MasterLimiterSetting.FromConfig(
            new AppConfig { MasterLimiterCeilingDb = configured });

        Assert.Equal(expected, setting.CeilingDb);
    }

    /// <summary>
    ///     A non-finite ceiling is REJECTED by the wrapper, not clamped — every
    ///     comparison a clamp is built from is false for a NaN, and a NaN ceiling
    ///     is permanent silence. So a corrupt config value must be turned into
    ///     something audible HERE, or it throws out of the rebuild replay.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonFiniteCeiling_FallsBackToTheShippedOne_RatherThanReachingTheEngine(float bad)
    {
        var setting = MasterLimiterSetting.FromConfig(new AppConfig { MasterLimiterCeilingDb = bad });

        Assert.True(float.IsFinite(setting.CeilingDb));
        Assert.Equal(MasterLimiterSetting.Default.CeilingDb, setting.CeilingDb);
    }

    // ----- the wiring (source) -----------------------------------------------

    /// <summary>
    ///     THE VERB LINT. A setting the service holds but never pushes is the
    ///     "registered but never driven" shape one level in: the config round-trips,
    ///     the tests above pass, and the engine still runs unlimited.
    /// </summary>
    [Fact]
    public void TheConfigApplyPath_PushesTheLimiterAtTheEngine()
    {
        var source = ServiceSource();

        // 1. The config-apply path — the ONE place every producer of an engine
        //    config reaches (startup, a settings apply, the watchdog rebuild) —
        //    derives the setting and pushes it.
        var apply = Body(source, "private void ApplyAudioConfigurationCore(RigConfig config, bool forceEngineReplace)");
        Assert.Contains("MasterLimiterSetting.FromConfig(config)", apply, StringComparison.Ordinal);
        Assert.Contains("ApplyMasterLimiter();", apply, StringComparison.Ordinal);

        // 2. …and that push is a real engine write, of the DERIVED value rather
        //    than a literal, or the user's switch is decoration.
        var push = Body(source, "private void ApplyMasterLimiter()");
        Assert.Contains("SetMasterLimiter(limiter.Enabled, limiter.CeilingDb, limiter.ReleaseMs)",
            push, StringComparison.Ordinal);

        // 3. The look-ahead delay line is only in the path while the stage is on,
        //    so OutputLatencySamples MOVES on a toggle (48 samples at 48 kHz) and
        //    everything that aligns against it — the 250 Hz position tick and both
        //    renderers, through EstimatedOutputLatencyMs — must stop serving the
        //    cached one.
        Assert.Contains("_latencyCache.Invalidate()", push, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The rebuild half, stated explicitly. <c>EngineRebuildStateReplayTests</c>
    ///     enumerates it generically, but that test can only fail once the service
    ///     writes the setter at all — this one names the member, so deleting the
    ///     opt-in entirely still costs somebody a red test rather than quietly
    ///     removing both halves at once.
    /// </summary>
    [Fact]
    public void TheEngineRebuild_ReplaysTheLimiter()
    {
        var replay = Body(ServiceSource(), "private void ReapplyMasterBusStateLocked(JuceAudioEngine engine)");

        Assert.Contains("SetMasterLimiter(", replay, StringComparison.Ordinal);
        // …and inside a guarded group, like every other write in there: losing one
        // setting must cost the user that setting, never the engine.
        Assert.Contains("Reapply(\"master limiter\"", replay, StringComparison.Ordinal);
    }

    // ----- helpers -----------------------------------------------------------

    private static string ServiceSource()
    {
        var files = PlaybackServiceSourceFiles.All();
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — has it been renamed?");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) break;
        }

        return source[open..Math.Min(i + 1, source.Length)];
    }
}
