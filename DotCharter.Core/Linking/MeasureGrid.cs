using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Linking;

/// <summary>
///     One measure of a beat map: the beats <c>[StartBeat, EndBeat)</c>, opened by an
///     <see cref="BeatMarker.IsMeasure" /> beat. The time signature is the opening beat's.
///     A measure the grid cuts short (the last one, or a hand-edited grid) has
///     <see cref="BeatCount" /> different from <see cref="BeatsInMeasure" />; it still
///     compares, because the signature part of a content key carries both.
/// </summary>
public readonly record struct MeasureSpan(int Index, int StartBeat, int EndBeat, int BeatsInMeasure, int NoteDenominator)
{
    public int BeatCount => EndBeat - StartBeat;

    public FractionalPosition Start => new(StartBeat);

    public FractionalPosition End => new(EndBeat);
}

/// <summary>
///     Splits a beat map into <see cref="MeasureSpan" />s. Beats before the first
///     measure marker (a pickup with no downbeat) belong to no measure, so nothing
///     there can be linked.
/// </summary>
public static class MeasureGrid
{
    public static IReadOnlyList<MeasureSpan> Build(IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var starts = new List<int>();
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].IsMeasure) starts.Add(i);
        }

        var measures = new List<MeasureSpan>(starts.Count);
        for (var m = 0; m < starts.Count; m++)
        {
            var start = starts[m];
            var end = m + 1 < starts.Count ? starts[m + 1] : beats.Count;
            var head = beats[start];
            measures.Add(new MeasureSpan(m, start, end, Math.Max(1, head.BeatsInMeasure), Math.Max(1, head.NoteDenominator)));
        }

        return measures;
    }
}
