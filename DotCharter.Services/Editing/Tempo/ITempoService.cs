using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;

namespace DotCharter.Services.Editing.Tempo;

public interface ITempoService
{
    FractionalPosition SnapToGrid(double timeMs, IList<BeatMarker>? beats, int gridDivisor);
    void UpdateBpm(IList<BeatMarker> beats, int beatIndex, double newBpm);
    /// <summary>
    ///     Re-times the beats AFTER <paramref name="startIndex" /> from the tempo
    ///     map. <paramref name="startIndex" /> itself never moves: a tempo written
    ///     on a beat says how long the beats that FOLLOW it last.
    /// </summary>
    void RecalculateBeatTimes(IList<BeatMarker> beats, int startIndex);

    /// <summary>
    ///     Moves beat <paramref name="index" /> onto the grid its PREDECESSOR
    ///     implies, leaving every BPM alone — what un-anchoring a beat means: the
    ///     tempo map, not the charter, now decides where it sits. A no-op at index
    ///     0 (nothing precedes it) or out of range. The beats after it are the
    ///     caller's job; <see cref="RecalculateBeatTimes" /> only ever walks
    ///     forward and so can never do this on its own.
    /// </summary>
    void RetimeBeatFromTempo(IList<BeatMarker> beats, int index);
    void ApplyTempoChange(double newBpm, int beatIndex, IList<BeatMarker> beats);

    void GenerateInitialBeats(IList<BeatMarker> beats, double totalAudioLengthMs, double bpm,
        double startOffsetMs = 0.0);

    void SetUniformBpm(IList<BeatMarker> beats, double bpm);
}
