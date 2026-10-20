using DotCharter.Services.Audio.LivePlay;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Playback;

/// <summary>
///     The stretch of song a "hear what I just charted" audition plays — PURE.
/// </summary>
public static class AuditionSpan
{
    /// <summary>
    ///     Let the last sound RING rather than cutting it at its notated end.
    ///     RS convention trims a chord's sustain to a strike and lets the hand
    ///     shape carry the duration, so an audition ending exactly on the last
    ///     sustain would clip almost every strum.
    /// </summary>
    public const double TailMs = 250;

    /// <summary>
    ///     Span of the given sounds in ms, or false when there are none.
    ///     <list type="bullet">
    ///         <item>start = the earliest onset, exactly — no lead-in, so what
    ///         you hear starts on the thing you selected;</item>
    ///         <item>end = the latest sound end plus <see cref="TailMs" />,
    ///         widened to <see cref="LivePlayLoop.MinLengthMs" /> so a single
    ///         zero-sustain note (which is what every placement produces) is
    ///         still a legal region rather than a refused zero-width one;</item>
    ///         <item>clamped to <paramref name="totalAudioLengthMs" /> when that
    ///         is known — an end past the song can never be REACHED, so the pass
    ///         would never complete and the audition would never stop.</item>
    ///     </list>
    /// </summary>
    public static bool TryCompute(
        IReadOnlyList<GuitarNote> notes,
        IReadOnlyList<Chord> chords,
        IList<BeatMarker> beats,
        double totalAudioLengthMs,
        out double startMs,
        out double endMs)
    {
        startMs = 0;
        endMs = 0;
        if (notes.Count == 0 && chords.Count == 0) return false;

        var first = double.MaxValue;
        var last = double.MinValue;

        foreach (var note in notes)
        {
            first = Math.Min(first, note.Position.GetPositionMs(beats));
            last = Math.Max(last, note.EndPosition.GetPositionMs(beats));
        }

        foreach (var chord in chords)
        {
            first = Math.Min(first, chord.Position.GetPositionMs(beats));
            last = Math.Max(last, chord.EndPosition.GetPositionMs(beats));
        }

        if (first > last) return false;

        startMs = Math.Max(0, first);
        endMs = Math.Max(last, startMs) + TailMs;
        if (endMs - startMs < LivePlayLoop.MinLengthMs) endMs = startMs + LivePlayLoop.MinLengthMs;

        if (totalAudioLengthMs > 0 && endMs > totalAudioLengthMs) endMs = totalAudioLengthMs;
        return endMs - startMs >= LivePlayLoop.MinLengthMs;
    }
}
