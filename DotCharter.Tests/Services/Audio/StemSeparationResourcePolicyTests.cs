using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Regression for the owner-reported playback stutters (2026-07-18): the
///     old <c>count - 2</c> policy ran 14 inference threads on a 16-core
///     machine and starved playback for the whole separation. The policy is
///     now half-the-machine (spin-waiting AVX threads cost a full core each).
/// </summary>
public sealed class StemSeparationResourcePolicyTests
{
    [Theory]
    [InlineData(16, 8)]  // the owner's machine: was 14, starved playback
    [InlineData(12, 6)]
    [InlineData(8, 4)]
    [InlineData(6, 3)]
    [InlineData(4, 2)]
    public void HalfTheMachine_OnTypicalCores(int cores, int expected)
        => Assert.Equal(expected, StemSeparationResourcePolicy.RecommendedWorkerThreads(cores));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)] // half=1; ceiling=0 would be invalid — floor of 1 wins
    [InlineData(3, 1)]
    public void SmallMachines_KeepAtLeastOneWorker(int cores, int expected)
        => Assert.Equal(expected, StemSeparationResourcePolicy.RecommendedWorkerThreads(cores));

    [Fact]
    public void NeverExceedsProcessorCountMinusReserved()
    {
        for (var cores = 1; cores <= 64; cores++)
        {
            var threads = StemSeparationResourcePolicy.RecommendedWorkerThreads(cores);
            Assert.InRange(threads, 1, Math.Max(1, cores - StemSeparationResourcePolicy.ReservedCores));
            Assert.True(threads <= Math.Max(1, cores / 2) || cores <= 3,
                $"{cores} cores -> {threads} threads exceeds half the machine");
        }
    }
}
