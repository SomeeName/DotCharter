namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Pure math behind right-clicking an anchor beat: removes its anchor flag
/// and re-prices the surrounding segment to a single uniform BPM.
///
/// Algorithm:
/// 1. Find the previous anchor (or beat 0) and next anchor (or last beat).
/// 2. Clear the IsAnchor flag on the target.
/// 3. Compute BPM from the time span between the two surrounding anchors
///    and redistribute every interior beat at that BPM.
/// </summary>
public static class AnchorRemovalMath
{
    /// <summary>Minimum span in milliseconds before BPM is allowed to grow arbitrarily.</summary>
    public const double MinSpanMs = 10.0;

    /// <summary>
    /// Returns deep-copied old/new beat lists for the anchor-removal action.
    /// No-op (returns null) when the index is out of range or the target is
    /// beat 0 / past the last beat.
    /// </summary>
    public static (List<BeatMarker> Old, List<BeatMarker> New)? Compute(
        IReadOnlyList<BeatMarker> beats, int beatIndex, double minSpanMs = MinSpanMs)
    {
        if (beats == null) throw new ArgumentNullException(nameof(beats));
        if (beatIndex <= 0 || beatIndex >= beats.Count) return null;
        if (minSpanMs <= 0) throw new ArgumentOutOfRangeException(nameof(minSpanMs));

        var oldBeats = TempoMapRecalculator.Snapshot(beats);

        var prevAnchor = 0;
        for (var i = beatIndex - 1; i >= 0; i--)
            if (i == 0 || oldBeats[i].IsAnchor) { prevAnchor = i; break; }

        var nextAnchor = oldBeats.Count - 1;
        for (var i = beatIndex + 1; i < oldBeats.Count; i++)
            if (i == oldBeats.Count - 1 || oldBeats[i].IsAnchor) { nextAnchor = i; break; }

        var newBeats = TempoMapRecalculator.Snapshot(oldBeats);
        newBeats[beatIndex].IsAnchor = false;

        var span = newBeats[nextAnchor].Time - newBeats[prevAnchor].Time;
        if (span < minSpanMs) span = minSpanMs;
        double beatCount = nextAnchor - prevAnchor;
        var bpm = beatCount * 60000.0 / span;
        var msPerBeat = 60000.0 / bpm;
        newBeats[prevAnchor].Bpm = bpm;
        for (var i = prevAnchor + 1; i < nextAnchor; i++)
        {
            newBeats[i].Time = newBeats[i - 1].Time + msPerBeat;
            newBeats[i].Bpm = bpm;
        }

        return (oldBeats, newBeats);
    }
}
