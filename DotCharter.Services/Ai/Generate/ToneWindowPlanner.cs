using System.Globalization;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     The stretch of song one placed tone change governs, and the arithmetic that
///     decided it.
/// </summary>
/// <param name="Index">Index into the SORTED tone-change list.</param>
/// <param name="ToneName">The change's own <see cref="ToneChange.ToneName" />; may
/// be empty on a freshly placed change.</param>
/// <param name="PlacedMs">Where the charter actually put the change.</param>
/// <param name="FromMs">Where the analysis window starts — one measure earlier,
/// clamped.</param>
/// <param name="ToMs">Where it ends: the next change, or the end of the
/// arrangement.</param>
/// <param name="MeasureMs">The length of the measure <see cref="PlacedMs" /> sits
/// in, as the tempo map gives it. This is the LEAD the window was extended by
/// before clamping — it is reported so the arithmetic is inspectable rather than
/// inferred from two timestamps.</param>
/// <param name="LeadMs">How much lead the window ACTUALLY got
/// (<c>PlacedMs - FromMs</c>). Less than <see cref="MeasureMs" /> when the clamp
/// bit.</param>
/// <param name="ClampedBy">Why the lead was shortened, or
/// <see cref="ToneWindowClamp.None" />.</param>
public sealed record ToneWindow(
    int Index,
    string ToneName,
    double PlacedMs,
    double FromMs,
    double ToMs,
    double MeasureMs,
    double LeadMs,
    ToneWindowClamp ClampedBy)
{
    /// <summary>How long the window is.</summary>
    public double DurationMs => ToMs - FromMs;

    /// <summary>One line of provenance: the measure arithmetic, spelled out.</summary>
    public string Describe()
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"window {FromMs:0}-{ToMs:0} ms ({DurationMs / 1000.0:0.0} s): placed at {PlacedMs:0} ms, " +
            $"one measure ({MeasureMs:0} ms at this tempo) of lead -> {LeadMs:0} ms");
        return ClampedBy == ToneWindowClamp.None ? line : line + ", clamped by " + ClampedBy;
    }
}

/// <summary>Why a window's lead was shorter than one measure.</summary>
public enum ToneWindowClamp
{
    /// <summary>It got the full measure.</summary>
    None,

    /// <summary>The previous tone change is less than a measure back — windows
    /// never overlap.</summary>
    PreviousChange,

    /// <summary>The arrangement starts less than a measure back.</summary>
    ArrangementStart
}

/// <summary>
///     Turns placed tone changes into the audio windows a tone is generated FROM.
///     Pure: beat markers in, windows out, no I/O and no clock.
///     <para>
///         <b>A window starts ONE MEASURE BEFORE the change, because a charter
///         places a tone change by ear and by ear is late.</b> The audible change —
///         the moment the amp opens up, the delay arrives — can precede the marker
///         by most of a bar, and a window that starts AT the marker then analyses
///         the tone that follows the one being described. One measure of lead is the
///         charter's own error bar.
///     </para>
///     <para>
///         <b>One measure is MUSICAL time, resolved through the tempo map</b>, never
///         a fixed millisecond value: it is the span of the measure the change sits
///         in, taken from the <see cref="BeatMarker.IsMeasure" /> markers, so a
///         change in a 60 BPM bar gets four times the lead of one in a 240 BPM bar
///         and a tempo change between two bars moves both. A fixed 2,000 ms would be
///         half a bar in one song and three in another.
///     </para>
///     <para>
///         <b>Two clamps, and they are the reason this is a planner rather than a
///         subtraction.</b> The lead is cut back to the previous tone change (so
///         windows never overlap — two changes a beat apart must not both analyse
///         the same audio and disagree) and to the start of the arrangement (a
///         change in the first measure must not produce a negative start).
///     </para>
///     <para>
///         The window ENDS at the next change, or at the end of the arrangement.
///         That half needs no lead: the next change's own window has already taken
///         its measure out of this one's tail, and stopping there is what keeps the
///         two from overlapping.
///     </para>
/// </summary>
public static class ToneWindowPlanner
{
    /// <summary>
    ///     The windows for <paramref name="changes" />, in time order.
    /// </summary>
    /// <param name="changes">The arrangement's tone changes. Order does not matter —
    /// they are sorted by time here, because the collection's order is not
    /// guaranteed chronological (the same rule `ToneChangeSpans.Resolve` states).</param>
    /// <param name="beats">The arrangement's beat map. <b>Required</b>: without it
    /// there is no tempo map, no measure and no window, and the caller is told so
    /// rather than handed a guess.</param>
    /// <param name="arrangementEndMs">Where the last window stops.</param>
    /// <exception cref="ArgumentException"><paramref name="beats" /> is empty.</exception>
    public static IReadOnlyList<ToneWindow> Plan(
        IReadOnlyList<ToneChange> changes, IList<BeatMarker> beats, double arrangementEndMs)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count == 0)
        {
            throw new ArgumentException(
                "A tone window is one MEASURE of musical time before the change, so it cannot be " +
                "planned without a beat map. Detect or import a beat map first.", nameof(beats));
        }

        var ordered = changes
            .Select((c, i) => (Change: c, Ms: c.GetTimeMs(beats), Original: i))
            .OrderBy(x => x.Ms)
            .ThenBy(x => x.Original)
            .ToList();

        var startMs = beats[0].Time;
        var windows = new List<ToneWindow>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var placed = ordered[i].Ms;
            var measure = MeasureLengthAt(placed, beats);

            var wanted = placed - measure;
            var clamp = ToneWindowClamp.None;

            // Never behind the previous change: two windows that overlap are two
            // tones generated from one piece of audio.
            if (i > 0 && wanted < ordered[i - 1].Ms)
            {
                wanted = ordered[i - 1].Ms;
                clamp = ToneWindowClamp.PreviousChange;
            }

            // …and never before the arrangement starts.
            if (wanted < startMs)
            {
                wanted = startMs;
                clamp = ToneWindowClamp.ArrangementStart;
            }

            windows.Add(new ToneWindow(
                i,
                ordered[i].Change.ToneName ?? string.Empty,
                placed,
                wanted,
                arrangementEndMs,
                measure,
                placed - wanted,
                clamp));
        }

        // A WINDOW ENDS WHERE THE NEXT ONE STARTS, not at the next CHANGE.
        //
        // That difference is the whole no-overlap rule. The next change's lead
        // reaches back into this change's stretch of song, so ending here at the
        // next change would hand the same audio to two tones — and they would
        // disagree about it, confidently. The audio in the overlap belongs to the
        // LATER change, because the lead exists precisely to say "the audible
        // change happened before the marker".
        //
        // The consequence is worth stating: two changes closer together than a
        // measure leave the EARLIER one a window that is entirely its own lead —
        // the bar before its marker — and none of the song after it. On the
        // premise this planner rests on (the marker is late), that bar is the
        // better half anyway.
        for (var i = 0; i < windows.Count - 1; i++)
            windows[i] = windows[i] with { ToMs = Math.Max(windows[i].FromMs, windows[i + 1].FromMs) };

        return windows;
    }

    /// <summary>
    ///     The length of the measure <paramref name="ms" /> falls in, from the beat
    ///     map.
    ///     <para>
    ///         <b>The measure's own span, not a computed one.</b> Deriving it from
    ///         BPM × beats-per-measure would disagree with the grid the charter reads
    ///         wherever a marker was nudged, and the markers are what every other
    ///         surface in this app numbers bars by (`Utilities/BarCount`).
    ///     </para>
    ///     <para>
    ///         Three degenerate cases, each with a reason rather than a default: a
    ///         time before the first measure marker uses the FIRST measure's length
    ///         (a pickup belongs to the bar it leads into, the same rule `BarCount`
    ///         states); a time in or after the LAST measure uses the preceding
    ///         measure's length; a map with fewer than two measure markers falls back
    ///         to the beat spacing × the beat's own <see cref="BeatMarker.BeatsInMeasure" />.
    ///     </para>
    /// </summary>
    /// <param name="ms">A time on the arrangement's timeline.</param>
    /// <param name="beats">The beat map.</param>
    public static double MeasureLengthAt(double ms, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count == 0) return 0;

        var bars = new List<double>();
        foreach (var beat in beats)
        {
            if (beat.IsMeasure)
                bars.Add(beat.Time);
        }

        if (bars.Count >= 2)
        {
            // The measure containing `ms`: the last bar line at or before it.
            var index = -1;
            for (var i = 0; i < bars.Count; i++)
            {
                if (bars[i] > ms) break;
                index = i;
            }

            if (index < 0) return bars[1] - bars[0];               // before the first bar line
            if (index >= bars.Count - 1) return bars[^1] - bars[^2]; // in or past the last one
            return bars[index + 1] - bars[index];
        }

        // No usable bar lines: a beat's own spacing times its declared meter. This is
        // the shape a freshly imported chart with a flat grid has.
        var reference = beats[0];
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].Time > ms) break;
            reference = beats[i];
        }

        var spacing = beats.Count >= 2 ? AverageSpacing(beats) : 0;
        var perMeasure = Math.Max(1, reference.BeatsInMeasure);
        return spacing * perMeasure;
    }

    private static double AverageSpacing(IList<BeatMarker> beats)
    {
        var span = beats[^1].Time - beats[0].Time;
        return span <= 0 ? 0 : span / (beats.Count - 1);
    }
}
