using System.Collections.Concurrent;
using System.Globalization;

namespace DotCharter.Core.Domain.Guitar;

/// <summary>What a bend label says: nothing, a bend up, a release down, or a prebend.</summary>
public enum BendGestureKind : byte
{
    None,
    Bend,
    Release,
    Prebend
}

/// <summary>
///     The label one bend point carries. <see cref="Amount" /> is in half steps: a bend's or a prebend's TARGET
///     pitch (as Guitar Pro writes "Full" over a bend to a whole step), a release's DROP (how far it lets down).
/// </summary>
public readonly record struct BendGesture(BendGestureKind Kind, float Amount);

/// <summary>
///     Which points of a bend curve carry a label, and what it reads — the ONE rule the 2D timeline and the 3D highway
///     share, so a curve reads as Guitar Pro 8 shows it:
///     <list type="bullet">
///         <item>A GESTURE is a run of points moving the same way. It is labelled once, at the point it ends on (the
///         peak of a bend, the landing of a release), so a plain bend 0 → ½ → Full is one "↗Full".</item>
///         <item>A hold (two points at one pitch) ends a gesture, so 0 → ½, hold, → Full is two rises, "↗1/2" and
///         "↗Full"; a turn ends one too, so a bend straight into a release labels the peak and the landing.</item>
///         <item>A struck note whose first point sits on its onset above the string is a PREBEND ("PB Full"); a note
///         TIED in from a bent note starts at the pitch it was carried in at, so its fall is a release, not a prebend.</item>
///     </list>
///     Pure and allocation-free (the texts are cached), because both renderers call it per visible note per frame.
/// </summary>
public static class BendGestures
{
    /// <summary>Two steps closer than this are one pitch (a hold), in half steps.</summary>
    public const float Tolerance = 0.05f;

    private static readonly ConcurrentDictionary<int, string> TextCache = new();
    private static readonly ConcurrentDictionary<int, string> AmountCache = new();

    /// <summary>
    ///     Fills <paramref name="gestures" /> (at least as long as <paramref name="steps" />) with the label of each
    ///     point of a curve in time order. <paramref name="firstAtOnset" />: the first point sits on the note's onset.
    ///     <paramref name="carriedIn" />: the pitch a tie carried into the note, or null for a struck note.
    /// </summary>
    public static void Classify(ReadOnlySpan<float> steps, bool firstAtOnset, float? carriedIn, Span<BendGesture> gestures)
    {
        gestures[..steps.Length].Clear();
        if (steps.IsEmpty) return;

        var pitch = carriedIn ?? 0f;
        var start = 0;
        if (firstAtOnset && carriedIn is null)
        {
            if (steps[0] > Tolerance) gestures[0] = new BendGesture(BendGestureKind.Prebend, steps[0]);
            pitch = steps[0];
            start = 1;
        }

        var direction = 0;
        var from = pitch;
        for (var i = start; i < steps.Length; i++)
        {
            var delta = steps[i] - pitch;
            var sign = delta > Tolerance ? 1 : delta < -Tolerance ? -1 : 0;
            if (direction != 0 && sign != direction)
            {
                Close(steps, i - 1, direction, from, gestures);
                direction = 0;
            }

            if (sign != 0 && direction == 0)
            {
                direction = sign;
                from = pitch;
            }

            pitch = steps[i];
        }

        if (direction != 0) Close(steps, steps.Length - 1, direction, from, gestures);
    }

    private static void Close(ReadOnlySpan<float> steps, int end, int direction, float from, Span<BendGesture> gestures) =>
        gestures[end] = direction > 0
            ? new BendGesture(BendGestureKind.Bend, steps[end])
            : new BendGesture(BendGestureKind.Release, from - steps[end]);

    /// <summary>The label's text: "↗Full" for a bend, "↘Full" for a release, "PB Full" for a prebend, "" for none.</summary>
    public static string Text(BendGesture gesture)
    {
        if (gesture.Kind == BendGestureKind.None) return "";
        var key = (int)gesture.Kind * 100_000 + Key(gesture.Amount);
        if (TextCache.TryGetValue(key, out var text)) return text;
        var amount = AmountText(gesture.Amount);
        text = gesture.Kind switch
        {
            BendGestureKind.Bend => "↗" + amount,
            BendGestureKind.Release => "↘" + amount,
            _ => "PB " + amount
        };
        if (TextCache.Count < 512) TextCache.TryAdd(key, text);
        return text;
    }

    /// <summary>An amount in half steps as Guitar Pro writes it: "1/2", "Full", "1 1/2", "2", else whole steps.</summary>
    public static string AmountText(float steps)
    {
        var key = Key(steps);
        if (AmountCache.TryGetValue(key, out var text)) return text;
        text = key switch
        {
            10 => "1/2",
            20 => "Full",
            30 => "1 1/2",
            40 => "2",
            _ => (key / 20.0).ToString("0.#", CultureInfo.InvariantCulture)
        };
        if (AmountCache.Count < 256) AmountCache.TryAdd(key, text);
        return text;
    }

    private static int Key(float steps) => (int)Math.Round(steps * 10f);
}
