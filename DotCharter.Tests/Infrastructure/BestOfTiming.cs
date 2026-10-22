using System.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The timing of an algorithmic-complexity budget ("the lookup is not scanning again").
///     A budget a quadratic version cannot meet is the SUBJECT of such a test, so the bound
///     stays, but one measurement on a shared box is a coin flip: a starved pool thread
///     stretches a linear run by seconds. A regression is slow on EVERY attempt, a starved
///     run is slow on one, so the verdict is the fastest of a few attempts, and a passing
///     run stops at the first attempt inside the bound.
/// </summary>
internal static class BestOfTiming
{
    /// <summary>Attempts after which a run that never met its bound is a regression.</summary>
    public const int Attempts = 4;

    /// <summary>
    ///     Runs <paramref name="work" /> until one run finishes inside <paramref name="bound" />
    ///     (or <paramref name="attempts" /> runs were made) and returns the fastest run.
    ///     Every run must be repeatable: it builds its own state or leaves none behind.
    /// </summary>
    public static TimeSpan Fastest(Action work, TimeSpan bound, int attempts = Attempts)
    {
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var clock = Stopwatch.StartNew();
            work();
            clock.Stop();
            if (clock.Elapsed < best) best = clock.Elapsed;
            if (best < bound) break;
        }

        return best;
    }
}
