using DotCharter.Rendering.Vulkan.Composed;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Pure decision rules of the Avalonia-composed 3D output path (0.16):
///     device pick by compositor adapter LUID and the exportable-image pool
///     rotation. No Vulkan instance required.
/// </summary>
public class ComposedPathPolicyTests
{
    private static byte[] Luid(byte seed)
    {
        var b = new byte[8];
        for (var i = 0; i < 8; i++) b[i] = (byte)(seed + i);
        return b;
    }

    // ---- ComposedDevicePolicy -------------------------------------------

    [Fact]
    public void PickDevice_MatchesCompositorLuid()
    {
        var candidates = new[]
        {
            new ComposedDeviceCandidate(true, true, Luid(1)),
            new ComposedDeviceCandidate(true, true, Luid(9))
        };

        Assert.Equal(1, ComposedDevicePolicy.PickDeviceIndex(candidates, Luid(9)));
    }

    [Fact]
    public void PickDevice_LuidMatchWithoutExtensions_IsNotUsable()
    {
        var candidates = new[]
        {
            new ComposedDeviceCandidate(false, true, Luid(9)),
            new ComposedDeviceCandidate(true, true, Luid(1))
        };

        // The compositor's adapter can't export — never silently render on a
        // different GPU instead.
        Assert.Equal(-1, ComposedDevicePolicy.PickDeviceIndex(candidates, Luid(9)));
    }

    [Fact]
    public void PickDevice_NoTargetLuid_FirstCapableWins()
    {
        var candidates = new[]
        {
            new ComposedDeviceCandidate(false, false, Luid(1)),
            new ComposedDeviceCandidate(true, false, Luid(2)),
            new ComposedDeviceCandidate(true, false, Luid(3))
        };

        Assert.Equal(1, ComposedDevicePolicy.PickDeviceIndex(candidates, null));
    }

    [Fact]
    public void PickDevice_NoCapableDevice_ReturnsMinusOne()
    {
        var candidates = new[] { new ComposedDeviceCandidate(false, true, Luid(1)) };
        Assert.Equal(-1, ComposedDevicePolicy.PickDeviceIndex(candidates, null));
        Assert.Equal(-1, ComposedDevicePolicy.PickDeviceIndex(Array.Empty<ComposedDeviceCandidate>(), null));
    }

    [Fact]
    public void PickDevice_ShortTargetLuid_IsTreatedAsAbsent()
    {
        var candidates = new[] { new ComposedDeviceCandidate(true, true, Luid(1)) };
        Assert.Equal(0, ComposedDevicePolicy.PickDeviceIndex(candidates, [1, 2]));
    }

    // ---- ComposedImagePoolPolicy ----------------------------------------

    [Fact]
    public void PickNext_RotatesRoundRobinFromLastAcquired()
    {
        var states = new[]
            { ComposedImageState.Free, ComposedImageState.Free, ComposedImageState.Free };

        Assert.Equal(1, ComposedImagePoolPolicy.PickNext(states, 0));
        Assert.Equal(2, ComposedImagePoolPolicy.PickNext(states, 1));
        Assert.Equal(0, ComposedImagePoolPolicy.PickNext(states, 2));
    }

    [Fact]
    public void PickNext_SkipsBusyImages()
    {
        var states = new[]
            { ComposedImageState.AwaitingPresent, ComposedImageState.Rendering, ComposedImageState.Free };

        Assert.Equal(2, ComposedImagePoolPolicy.PickNext(states, 0));
    }

    [Fact]
    public void PickNext_NothingFree_ReturnsMinusOne()
    {
        var states = new[]
            { ComposedImageState.AwaitingPresent, ComposedImageState.AwaitingPresent, ComposedImageState.Rendering };

        Assert.Equal(-1, ComposedImagePoolPolicy.PickNext(states, 1));
        Assert.Equal(-1, ComposedImagePoolPolicy.PickNext([], 0));
    }

    [Fact]
    public void PickNext_NegativeLastAcquired_StartsAtZero()
    {
        var states = new[] { ComposedImageState.Free, ComposedImageState.Free };
        Assert.Equal(0, ComposedImagePoolPolicy.PickNext(states, -1));
    }

    [Fact]
    public void PickNext_NeverReturnsBroken()
    {
        var states = new[] { ComposedImageState.Broken, ComposedImageState.Broken, ComposedImageState.Broken };
        Assert.Equal(-1, ComposedImagePoolPolicy.PickNext(states, 0));
        Assert.True(ComposedImagePoolPolicy.NeedsRebuild(states));
    }

    [Fact]
    public void NeedsRebuild_HealthyPool_False()
    {
        var states = new[]
            { ComposedImageState.Free, ComposedImageState.Rendering, ComposedImageState.AwaitingPresent };
        Assert.False(ComposedImagePoolPolicy.NeedsRebuild(states));
    }
}
