namespace DotCharter.Services.Export.Sheet;

/// <summary>
/// When the sheet engraves a tempo marking, and what it prints.
///     <para>An RS-XML beat map stores each beat in whole milliseconds, so its per-beat
///     tempo wobbles by a few tenths of a BPM. The old rule (a marking whenever the bar's
///     first beat moved more than 0.05 BPM) engraved one on nearly every bar of an
///     imported song.</para>
/// </summary>
internal static class SheetTempoMarking
{
    /// <summary>The smallest move from the last ENGRAVED tempo that earns a new marking.
    /// Measured against the last marking, not the last bar, so a slow drift still gets
    /// one once it adds up.</summary>
    public const double StepBpm = 1.0;

    public static bool IsNew(double lastEngravedBpm, double bpm) =>
        double.IsNaN(lastEngravedBpm) || Math.Abs(bpm - lastEngravedBpm) >= StepBpm;

    /// <summary>A whole number, as in any printed score. Half rounds up, so two tempos a
    /// <see cref="StepBpm" /> apart never print the same number.</summary>
    public static int Engraved(double bpm) => (int)Math.Round(bpm, MidpointRounding.AwayFromZero);

    /// <summary>The bar's tempo: the mean of its beats' tempos, so the millisecond wobble
    /// of single beats cancels out.</summary>
    public static double BarTempo(IList<BeatMarker> beats, int firstIdx, int lastIdx, double fallback)
    {
        var sum = 0.0;
        var count = 0;
        for (var b = firstIdx; b <= lastIdx; b++)
        {
            if (beats[b].Bpm <= 0) continue;
            sum += beats[b].Bpm;
            count++;
        }

        return count == 0 ? fallback : sum / count;
    }
}
