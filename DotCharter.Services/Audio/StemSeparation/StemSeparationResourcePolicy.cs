namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// Pure policy for how many worker threads a stem-separation run may use so it
/// never saturates every core and starves the UI / audio callback. The rule:
/// use at most HALF the machine (AVX inference threads spin-wait between ops,
/// so each one costs a full core even when "waiting"), never more than
/// <c>processorCount - <see cref="ReservedCores"/></c>, and always at least
/// one worker.
/// </summary>
/// <remarks>
/// The old <c>count - 2</c> rule put 14 inference threads on the owner's
/// 16-core machine — measured result: charting playback stuttered
/// audibly the whole time a separation ran, while playback alone measures
/// allocation-clean and pause-free. htdemucs scales sublinearly beyond ~8
/// threads, so half the machine costs little separation time and returns the
/// interactive experience.
/// </remarks>
public static class StemSeparationResourcePolicy
{
    /// <summary>Cores intentionally left free for the UI + audio threads.</summary>
    public const int ReservedCores = 2;

    /// <summary>
    /// Worker-thread cap for a machine with <paramref name="processorCount"/>
    /// logical processors. Half the machine, bounded by
    /// <c>processorCount - <see cref="ReservedCores"/></c> below and 1 above.
    /// </summary>
    public static int RecommendedWorkerThreads(int processorCount)
    {
        if (processorCount <= 1) return 1;
        var half = processorCount / 2;
        var ceiling = processorCount - ReservedCores;
        return Math.Max(1, Math.Min(half, ceiling));
    }

    /// <summary>Convenience overload using the current machine.</summary>
    public static int RecommendedWorkerThreads() =>
        RecommendedWorkerThreads(Environment.ProcessorCount);
}
