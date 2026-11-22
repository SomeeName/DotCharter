using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Tone;

/// <summary>#350: a fire-and-forget worker queued around shutdown meets a disposed gate and must not fault.</summary>
public sealed class VstGateTests
{
    [Fact]
    public async Task TryEnterAsync_OnADisposedGate_ReturnsFalse_InsteadOfThrowing()
    {
        var gate = new SemaphoreSlim(1, 1);
        gate.Dispose();

        Assert.False(await gate.TryEnterAsync());
    }

    [Fact]
    public async Task TryEnterAsync_OnALiveGate_HoldsIt()
    {
        using var gate = new SemaphoreSlim(1, 1);

        Assert.True(await gate.TryEnterAsync());
        Assert.Equal(0, gate.CurrentCount);
    }
}
