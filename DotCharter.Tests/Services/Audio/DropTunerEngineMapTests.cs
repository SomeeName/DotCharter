using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The drop tuner's quality mode across its three representations.
///
///     <para>The bug this guards was invisible in the worst way: the settings SAVE
///     was correct and the engine's STARTUP read was correct, so the mode worked
///     perfectly — after an app restart. Only the live push to the running engine
///     was wrong (<c>index == 1 ? 1 : 0</c>), so picking Smooth and listening
///     immediately auditioned Tight. The mode you would restart the app to try was
///     the one you could never hear, and it is the mode with the longest splice-lag
///     ceiling — the one that helps on chords, which is exactly the material the
///     tuner is judged on.</para>
///
///     <para>So the load-bearing assertion is not "the enum has three values". It is
///     <b>the live push and the after-restart path resolve to the SAME mode</b>.</para>
/// </summary>
public sealed class DropTunerEngineMapTests
{
    /// <summary>
    ///     Round-trip through the config exactly as the app does: the UI saves the
    ///     combo index to a string, and on the next launch the engine reads that
    ///     string back into a native mode. That must equal what the LIVE push sends
    ///     for the same combo index. If these two ever disagree, a mode behaves one
    ///     way now and a different way after a restart — which is what happened.
    /// </summary>
    [Theory]
    [InlineData(DropTunerEngineMap.Tight)]
    [InlineData(DropTunerEngineMap.Balanced)]
    [InlineData(DropTunerEngineMap.Smooth)]
    public void TheLivePush_AgreesWithTheAfterRestartPath(int comboIndex)
    {
        var livePush = DropTunerEngineMap.ToNativeMode(comboIndex);

        // …save to config, relaunch, read it back:
        var afterRestart = DropTunerEngineMap.FromConfigString(
            DropTunerEngineMap.ToConfigString(comboIndex));

        Assert.Equal(afterRestart, livePush);
    }

    /// <summary>Smooth specifically, because that is the one that broke and the one
    /// that matters: 35 ms splice-lag ceiling, long enough for a chord's composite
    /// period. It must NOT resolve to Tight.</summary>
    [Fact]
    public void Smooth_IsSmooth_NotTight()
    {
        Assert.Equal(2, DropTunerEngineMap.ToNativeMode(DropTunerEngineMap.Smooth));
        Assert.NotEqual(DropTunerEngineMap.Tight, DropTunerEngineMap.ToNativeMode(DropTunerEngineMap.Smooth));
        Assert.Equal("smooth", DropTunerEngineMap.ToConfigString(DropTunerEngineMap.Smooth));
        Assert.Equal(DropTunerEngineMap.Smooth, DropTunerEngineMap.FromConfigString("smooth"));
    }

    [Theory]
    [InlineData("tight", DropTunerEngineMap.Tight)]
    [InlineData("balanced", DropTunerEngineMap.Balanced)]
    [InlineData("smooth", DropTunerEngineMap.Smooth)]
    [InlineData("SMOOTH", DropTunerEngineMap.Smooth)]   // casing must not matter
    [InlineData("  smooth ", DropTunerEngineMap.Smooth)]
    public void ConfigStrings_Parse(string stored, int expected)
        => Assert.Equal(expected, DropTunerEngineMap.FromConfigString(stored));

    /// <summary>The pre-CSVD engines. A config written by an older build must land on
    /// Balanced, not throw and not silently pick Tight.</summary>
    [Theory]
    [InlineData("rubberband")]
    [InlineData("timedomain")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void LegacyAndUnknownValues_FallBackToBalanced(string? stored)
        => Assert.Equal(DropTunerEngineMap.Balanced, DropTunerEngineMap.FromConfigString(stored));

    /// <summary>A corrupt index must clamp — it is fed straight to the native ABI.</summary>
    [Theory]
    [InlineData(-1, DropTunerEngineMap.Tight)]
    [InlineData(99, DropTunerEngineMap.Smooth)]
    public void AnOutOfRangeIndex_IsClamped(int index, int expected)
        => Assert.Equal(expected, DropTunerEngineMap.ToNativeMode(index));
}
