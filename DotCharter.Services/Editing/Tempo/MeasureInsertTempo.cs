namespace DotCharter.Services.Editing.Tempo;

/// <summary>
///     The time signature and tempo of the bars Insert measures at playhead adds:
///     <paramref name="BeatsPerMeasure" />/<paramref name="NoteDenominator" /> at
///     <paramref name="Bpm" /> quarter notes a minute.
/// </summary>
public readonly record struct MeasureInsertTempo(int BeatsPerMeasure, int NoteDenominator, double Bpm)
{
    /// <summary>One beat's length in ms: a quarter note at <see cref="Bpm" />, scaled to the beat unit.</summary>
    public double BeatIntervalMs => 60000.0 / Bpm * 4.0 / NoteDenominator;
}
