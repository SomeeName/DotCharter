using System.Diagnostics;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;

namespace DotCharter.Services.Editing.Snapping;

public class SnapService : ISnapService
{
    public SnapMode Mode { get; set; } = SnapMode.Beat;
    public double Divisor { get; set; } = 4.0; // e.g., 1/4 beat, 1/4 measure, 1/10 ms

    public double SnapToGrid(double timeMs, IEnumerable<BeatMarker> beats)
    {
        if (Divisor <= 0) return timeMs;

        if (Mode == SnapMode.Milliseconds)
        {
            var step = 1.0 / Divisor;
            return Math.Round(timeMs / step) * step;
        }

        var beatList = beats as IList<BeatMarker> ?? beats.ToList();
        if (beatList.Count == 0) return timeMs;
        Debug.Assert(
            beatList.Zip(beatList.Skip(1), (current, next) => current.Time <= next.Time).All(isSorted => isSorted),
            "SnapService expects beats to be sorted by time.");

        var prevBeat = beatList.LastOrDefault(b => b.Time <= timeMs) ?? beatList.First();
        var nextBeat = beatList.FirstOrDefault(b => b.Time > timeMs) ?? prevBeat;

        if (prevBeat == nextBeat) return prevBeat.Time;

        if (Mode == SnapMode.Beat)
        {
            var beatDuration = nextBeat.Time - prevBeat.Time;
            var stepMs = beatDuration / Divisor;
            var stepsFromPrev = Math.Round((timeMs - prevBeat.Time) / stepMs);
            return prevBeat.Time + stepsFromPrev * stepMs;
        }
        else // SnapMode.Measure
        {
            var prevMeasure = beatList.LastOrDefault(b => b.Time <= timeMs && b.IsMeasure) ?? prevBeat;
            var nextMeasure = beatList.FirstOrDefault(b => b.Time > timeMs && b.IsMeasure) ?? nextBeat;

            if (prevMeasure == nextMeasure) return prevMeasure.Time;

            var measureDuration = nextMeasure.Time - prevMeasure.Time;
            var stepMs = measureDuration / Divisor;
            var stepsFromPrev = Math.Round((timeMs - prevMeasure.Time) / stepMs);
            return prevMeasure.Time + stepsFromPrev * stepMs;
        }
    }
}
