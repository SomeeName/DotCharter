namespace DotCharter.Services.Gameplay;

/// <summary>
/// Pure plan for WHERE a gameplay run starts.
///
/// <para>
/// Play Mode does not hard-seek to 0: that is right only for a Rocksmith import, whose beat grid
/// starts near zero — but not for a chart authored here. With <c>PadSilenceOnNewProject</c>
/// on, beat 0 sits at <c>LeadingSilenceSeconds</c> (default 10 s) — the leading silence is
/// baked into the working audio so the music begins there too (the fallback with no ffmpeg
/// just shifts the grid), and every note is anchored to a beat. There is therefore no chart
/// content at all before 10 s.
/// </para>
/// <para>
/// The 3D highway draws ONLY chart content — there is no scrolling ground plane — and a beat
/// line is only visible within ~1.75 s of the playhead. So starting at 0 gave the player a
/// pixel-identical frozen frame for ~8 s while the transport was actually running, and the
/// first beat crossed the strike line at exactly 10 s. It reads as "the game hung for ten
/// seconds", which is precisely how it was reported.
/// </para>
/// <para>
/// So: start just before the first thing the player can see, with enough lead-in to watch it
/// fly in rather than have it appear on top of them.
/// </para>
/// </summary>
public static class GameplayStartPlan
{
    /// <summary>
    /// Lead-in before the first chart content, at the DEFAULT highway settings. Comfortably
    /// longer than the beat-line visibility horizon there, so the highway is populated and
    /// moving before the first note is actionable.
    /// </summary>
    /// <remarks>
    /// This is a floor, not the whole story: the horizon is user-controlled (highway scroll
    /// speed × draw distance) and reaches ~12 s, so callers that know the horizon must use
    /// <see cref="LeadInForHorizon"/>.
    /// </remarks>
    public const double DefaultLeadInMs = 3000.0;

    /// <summary>Slack on top of the horizon, so the first note enters from the far edge of an
    /// empty board rather than being already on it at t=0.</summary>
    public const double LeadInMarginMs = 500.0;

    /// <summary>
    /// The lead-in a given visible horizon needs. The point of the lead-in is that the first
    /// note FLIES IN — it enters at the far edge and travels the board — instead of appearing
    /// on top of the player. That requires starting the transport at least one full horizon
    /// before the note, so the note is not already halfway down the highway when the run
    /// begins. A fixed 3 s lead-in does that at the default 2275 ms horizon, but at the slow
    /// end (12 s of chart on screen) it would drop the first note a quarter of the way down.
    /// </summary>
    public static double LeadInForHorizon(double horizonMs)
    {
        if (double.IsNaN(horizonMs) || double.IsInfinity(horizonMs) || horizonMs <= 0)
            return DefaultLeadInMs;
        return Math.Max(DefaultLeadInMs, horizonMs + LeadInMarginMs);
    }

    /// <summary>
    /// The transport position a run should start at, given the earliest chart content.
    /// </summary>
    /// <remarks>
    /// Returns 0 when the content already starts inside the lead-in — so a PSARC whose grid
    /// starts near zero behaves exactly as it does today, and only charts with a real gap
    /// (authored ones, with their shifted beat grid) skip anything.
    /// </remarks>
    public static double ResolveStartMs(double? firstContentMs, double leadInMs = DefaultLeadInMs)
    {
        if (firstContentMs is not { } content) return 0.0;          // nothing to play
        if (double.IsNaN(content) || double.IsInfinity(content)) return 0.0;
        var lead = leadInMs > 0 && !double.IsNaN(leadInMs) ? leadInMs : 0.0;
        return Math.Max(0.0, content - lead);
    }

    /// <summary>
    /// Earliest visible chart content out of a set of candidate times (the first beat marker
    /// and the first playable note). Ignores NaN/infinite entries. Null when there is nothing.
    /// </summary>
    public static double? EarliestOf(params double?[] candidates)
    {
        double? earliest = null;
        foreach (var c in candidates)
        {
            if (c is not { } v || double.IsNaN(v) || double.IsInfinity(v)) continue;
            if (earliest is null || v < earliest) earliest = v;
        }
        return earliest;
    }
}
