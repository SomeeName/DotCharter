using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;

namespace DotCharter.Services.Editing.Tempo;

public class TempoService : ITempoService
{
    private readonly IAppLogger _logger;

    public TempoService(IAppLogger logger) => _logger = logger;

    public FractionalPosition SnapToGrid(double timeMs, IList<BeatMarker>? beats, int gridDivisor)
    {
        if (beats == null || beats.Count == 0) return FractionalPosition.Zero;

        var beatIdx = TimelineMath.BinarySearchBeats(beats, timeMs);
        if (beatIdx < 0) beatIdx = 0;
        var beat = beats[beatIdx];
        var nextBeatTime = beatIdx + 1 < beats.Count
            ? beats[beatIdx + 1].Time
            // Past the grid, extrapolate off the LAST interval. This is the third
            // copy of a bug fixed twice before (TimelineMath.PositionFromMs,
            // AudioToChartAnalyzer): using the FIRST interval disagrees with
            // CalculateTimeMs on any chart with a tempo change, so the snapped
            // position renders at a DIFFERENT time than the click that produced
            // it. On a 60→240 BPM chart whose beat map ends before the audio, a
            // click 200 ms past the end snapped to a position rendering 153 ms
            // BEFORE the click. Reachable after any RS-XML/EOF import or a Remove
            // Beat.
            : beat.Time + (beats.Count >= 2 ? beats[^1].Time - beats[^2].Time : 500);

        var frac = (timeMs - beat.Time) / Math.Max(1.0, nextBeatTime - beat.Time);
        frac = Math.Clamp(frac, 0.0, 0.999);

        var den = gridDivisor > 0 ? gridDivisor : 16;
        var num = (int)Math.Round(frac * den);

        if (num == den)
        {
            beatIdx++;
            num = 0;
        }

        return new FractionalPosition(beatIdx, new Fraction(num, den));
    }

    public void UpdateBpm(IList<BeatMarker> beats, int beatIndex, double newBpm)
    {
        if (beatIndex < 0 || beatIndex >= beats.Count)
        {
            _logger.Warning(AppLogCategory.Editing,
                $"UpdateBpm rejected: beatIndex={beatIndex} out of range (count={beats.Count}).");
            return;
        }

        var previousBpm = beats[beatIndex].Bpm;
        beats[beatIndex].Bpm = newBpm;
        RecalculateBeatTimes(beats, beatIndex);
        _logger.Info(AppLogCategory.Editing,
            $"Updated BPM at beat {beatIndex} from {previousBpm:0.###} to {newBpm:0.###}; recalculated {beats.Count - beatIndex} downstream beats.");
    }

    public void RecalculateBeatTimes(IList<BeatMarker> beats, int startIndex)
    {
        if (beats.Count == 0) return;

        // The walk starts AFTER startIndex, never on it. A tempo written on a
        // beat says how long the beats that FOLLOW it last; the beat itself is
        // where the charter put it. Starting on startIndex re-derived that
        // beat's own time from its PREDECESSOR's tempo — a no-op on a grid whose
        // spacing matches its printed BPM, and a silent yank on every imported
        // or hand-dragged one, where the BPM field only approximates the spacing.
        for (var i = Math.Max(1, startIndex + 1); i < beats.Count; i++)
        {
            var prev = beats[i - 1];
            var current = beats[i];

            // If the measure/beat is anchored by the user, we do not shift its absolute time
            // which enforces a natural tempo ramp/interpolation leading up to it.
            if (current.IsAnchor) continue;

            var msPerBeat = 60000.0 / prev.Bpm * 4.0 / prev.NoteDenominator;
            current.Time = prev.Time + msPerBeat;

            // Carry the tempo forward over the unanchored beats that follow.
            current.Bpm = prev.Bpm;
        }
    }

    public void RetimeBeatFromTempo(IList<BeatMarker> beats, int index)
    {
        if (index <= 0 || index >= beats.Count) return;

        var prev = beats[index - 1];
        // A zero or negative BPM/denominator divides to Infinity and poisons every
        // beat downstream of it without ever throwing — the same guard
        // RscpBeatsParser applies to a corrupt "4/0" signature off disk.
        var bpm = prev.Bpm > 0 ? prev.Bpm : 120.0;
        var denominator = prev.NoteDenominator > 0 ? prev.NoteDenominator : 4;
        beats[index].Time = prev.Time + 60000.0 / bpm * 4.0 / denominator;

        _logger.Trace(AppLogCategory.Editing,
            $"Re-timed beat {index} onto the tempo grid at {beats[index].Time:0.###} ms (from beat {index - 1} at {prev.Time:0.###} ms, {bpm:0.###} BPM).");
    }

    public void ApplyTempoChange(double newBpm, int beatIndex, IList<BeatMarker> beats) =>
        // Simply push the tempo change into the beat map. 
        // Notes naturally inherit this change via their FractionalPosition relative to the beats.
        UpdateBpm(beats, beatIndex, newBpm);

    public void GenerateInitialBeats(IList<BeatMarker> beats, double totalAudioLengthMs, double bpm,
        double startOffsetMs = 0.0)
    {
        if (beats.Count > 0 || bpm <= 0)
        {
            _logger.Trace(AppLogCategory.Project,
                $"GenerateInitialBeats skipped (existing={beats.Count}, bpm={bpm}).");
            return;
        }

        var msPerBeat = 60000.0 / bpm;
        var beatCount = 0;
        for (var t = startOffsetMs; t <= totalAudioLengthMs; t += msPerBeat)
        {
            beats.Add(new BeatMarker
            { Time = t, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = beatCount == 0 });
            beatCount++;
            if (beatCount >= 4) beatCount = 0;
        }

        _logger.Info(AppLogCategory.Project,
            $"Generated {beats.Count} initial beats at {bpm:0.###} BPM (startOffsetMs={startOffsetMs:0.##}, totalMs={totalAudioLengthMs:0.##}).");
    }

    public void SetUniformBpm(IList<BeatMarker> beats, double bpm)
    {
        if (beats.Count == 0 || bpm <= 0)
        {
            _logger.Trace(AppLogCategory.Editing,
                $"SetUniformBpm skipped (count={beats.Count}, bpm={bpm}).");
            return;
        }

        var startTime = beats[0].Time;
        beats[0].Bpm = bpm;
        beats[0].IsAnchor = false;
        for (var i = 1; i < beats.Count; i++)
        {
            beats[i].Bpm = bpm;
            beats[i].IsAnchor = false;
        }

        // First beat stays at its current time; recalculate everything after it based on the single BPM
        // while preserving the per-beat NoteDenominator (for odd time signatures).
        for (var i = 1; i < beats.Count; i++)
        {
            var prev = beats[i - 1];
            var msPerBeat = 60000.0 / bpm * 4.0 / prev.NoteDenominator;
            beats[i].Time = prev.Time + msPerBeat;
        }

        // Keep the first beat at startTime (in case we perturbed it).
        beats[0].Time = startTime;

        _logger.Info(AppLogCategory.Editing,
            $"Set uniform BPM to {bpm:0.###} across {beats.Count} beats; all anchors cleared.");
    }
}
