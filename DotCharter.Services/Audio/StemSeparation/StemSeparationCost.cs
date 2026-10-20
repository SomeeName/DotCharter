namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     How long separating THIS song will take, derived from how long separation
///     really took on THIS machine.
///
///     <para><b>Why it is measured and not a constant.</b> Separation cost is
///     dominated by the machine (CPU cores, which GPU DirectML runs on)
///     and by the model, and nothing in the repo knows either well enough to
///     predict a duration. A hard-coded "~40 s" would be a number nothing
///     computes — so instead the app REMEMBERS: every successful separation
///     records seconds-of-work per second-of-audio, and the estimate is that rate
///     times this song's length. Before the first run there is no rate, and the
///     screen shows no estimate rather than a guess.</para>
///
///     <para>Pure, so the whole contract is pinned without a separator, an audio
///     file or a clock.</para>
/// </summary>
public static class StemSeparationCost
{
    /// <summary>A rate at or below this reads as "never measured". It also
    /// rejects a nonsense measurement (a cache hit that returned instantly would
    /// otherwise teach the app that separation is free).</summary>
    public const double MinimumRate = 0.01;

    /// <summary>Above this the measurement is discarded as an outlier — a
    /// machine that took 100× the audio length was doing something else
    /// (thermal throttling, a first-run model download) and must not poison the
    /// estimate for every later song.</summary>
    public const double MaximumRate = 100.0;

    /// <summary>Weight given to the NEWEST measurement. Half-and-half: the
    /// estimate follows a real change (a GPU runtime arriving) within a couple
    /// of runs without swinging on one unlucky one.</summary>
    public const double BlendWeight = 0.5;

    /// <summary>The shortest audio worth measuring. A four-second clip is
    /// dominated by model load, not by inference.</summary>
    public const double MinimumAudioSeconds = 10.0;

    /// <summary>Rate implied by one run, or <c>null</c> when the run says
    /// nothing usable.</summary>
    public static double? RateFrom(double elapsedSeconds, double audioSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(audioSeconds)) return null;
        if (audioSeconds < MinimumAudioSeconds || elapsedSeconds <= 0) return null;

        var rate = elapsedSeconds / audioSeconds;
        return rate is >= MinimumRate and <= MaximumRate ? rate : null;
    }

    /// <summary>Folds a fresh measurement into the stored one. With no stored
    /// rate the measurement is taken whole — the first run has to be able to
    /// produce an estimate for the second.</summary>
    public static double Blend(double storedRate, double measuredRate) =>
        storedRate < MinimumRate
            ? measuredRate
            : storedRate * (1 - BlendWeight) + measuredRate * BlendWeight;

    /// <summary>How long this song will probably take, or <c>null</c> when the
    /// machine has never finished a separation (or the song has no length yet).
    /// <c>null</c> is the honest answer and callers must render it as "no
    /// estimate", never as zero.</summary>
    public static double? EstimateSeconds(double storedRate, double audioSeconds)
    {
        if (storedRate < MinimumRate || !double.IsFinite(storedRate)) return null;
        if (!double.IsFinite(audioSeconds) || audioSeconds <= 0) return null;
        return storedRate * audioSeconds;
    }
}
