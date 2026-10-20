namespace DotCharter.Services.Gameplay;

/// <summary>
/// Pure plan for an ADDITIONAL count-in placed on the beat grid immediately
/// before the FIRST expected sound of a run.
///
/// <para>The run-start count-in (<see cref="CountInPlan"/>) plays N pre-roll
/// clicks and then starts the transport. On a song with a long noteless intro
/// that leaves the player hearing the pre-roll, then a stretch of silence, then
/// notes arriving unannounced. This plan closes that gap: when the first note
/// sits well past the transport start, it picks the N beats immediately before
/// the note so a driver can click them sample-accurately over the render clock
/// (via <c>IAudioPlaybackService.TryScheduleClick</c>) as the playhead
/// approaches — the same clicks land ON the beats that lead into the note.</para>
///
/// <para><b>Tempo-change safe by construction.</b> The click times are taken
/// straight from the tempo map's actual beat marker times, never re-derived from
/// a single BPM, so a tempo change inside the count-in window is honoured exactly.
/// No clock, no audio, no I/O — the schedule is fully unit-testable.</para>
///
/// <para><b>Why this is only a PLAN, scheduled later.</b> The native engine drops
/// any click scheduled more than ~5 s ahead of its render clock, and the whole
/// point of this feature is a first note that is many seconds away. So the plan
/// only decides WHICH beats get clicks (and their accent); the driver schedules
/// each one as the playhead comes within the engine's reach. See
/// <c>LivePlayViewModel</c>.</para>
/// </summary>
public static class FirstNoteCountInPlan
{
    /// <summary>One scheduled count-in click at an absolute chart time.
    /// <paramref name="Accent"/> marks a measure downbeat (measure-tick voice),
    /// mirroring <see cref="CountInPlan"/>'s first-beat-accented pattern.</summary>
    public readonly record struct Click(double TimeMs, bool Accent);

    /// <summary>Extra clearance, in beats, required BEYOND the N count-in beats
    /// between the transport start and the first note. Below this the first note
    /// is "within the run-start count-in's reach" and nothing is added — the gate
    /// is "the first note sits ≥ N + <see cref="ClearanceBeats"/> beats past the
    /// transport start position".</summary>
    public const int ClearanceBeats = 2;

    /// <summary>
    /// Builds the pre-first-note count-in schedule, or an empty array when it does
    /// not apply.
    /// </summary>
    /// <param name="beatTimesMs">Beat marker times of the tempo map, in ms
    /// (ascending; a non-monotonic entry is tolerated — the selected window is
    /// re-sorted). Non-finite entries are ignored.</param>
    /// <param name="firstNoteMs">Chart time of the first expected sound.</param>
    /// <param name="runStartMs">Chart time the transport starts at (the run's
    /// start position, not necessarily 0).</param>
    /// <param name="beats">Configured count-in beats (off / 4 / 8; normalised by
    /// <see cref="CountInPlan.Sanitize"/>).</param>
    /// <param name="beatsPerMeasure">Beats per measure, for the accent pattern.</param>
    /// <returns>The N clicks on the N beats immediately preceding the first note,
    /// earliest first; empty when the count-in is off, there is no first note, or
    /// the first note is within reach of the run-start count-in.</returns>
    public static Click[] Build(
        IReadOnlyList<double>? beatTimesMs, double firstNoteMs,
        double runStartMs, int beats, int beatsPerMeasure)
    {
        var n = CountInPlan.Sanitize(beats);
        if (n == 0) return [];
        if (beatTimesMs is null || beatTimesMs.Count == 0) return [];
        if (double.IsNaN(firstNoteMs) || double.IsInfinity(firstNoteMs)) return [];

        // The beats the player would otherwise hear pass in silence between the
        // transport start and the first note. A beat exactly on the run start or
        // on the note itself is excluded: the run start is not "before" the note's
        // lead-in, and the note carries its own audio.
        var window = new List<double>();
        foreach (var t in beatTimesMs)
        {
            if (double.IsNaN(t) || double.IsInfinity(t)) continue;
            if (t > runStartMs && t < firstNoteMs) window.Add(t);
        }

        // Gate: at least N + ClearanceBeats such beats, i.e. the first note sits
        // ≥ N + ClearanceBeats beats past the transport start. Fewer than that and
        // the run-start pre-roll already led the player in — add nothing.
        if (window.Count < n + ClearanceBeats) return [];

        // Defend against a non-monotonic tempo map so "the last N before the note"
        // is well-defined.
        window.Sort();

        var clicks = new Click[n];
        var startIndex = window.Count - n; // the N beats immediately preceding the note
        for (var i = 0; i < n; i++)
            clicks[i] = new Click(window[startIndex + i], CountInPlan.IsAccent(i, beatsPerMeasure));
        return clicks;
    }
}
