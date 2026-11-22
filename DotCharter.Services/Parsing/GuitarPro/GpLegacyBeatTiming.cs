namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>Places binary beats with the GPIF swing rule, preserving explicit tuplets and notation.</summary>
internal static class GpLegacyBeatTiming
{
    public static void Emit(GpLegacyTimedBeat timed, GpLegacyTimedBeat? next, string feel,
        Action<GpLegacyTimedBeat, GpLegacyBeatContext, Fraction> emit)
    {
        var onset = Onset(timed, feel);
        var end = next != null
            ? Onset(next, feel)
            : timed.Context.Position + new FractionalPosition(0, timed.Duration);
        var span = end - onset;
        var duration = end > onset ? span.BeatId + span.Fraction : timed.Duration;
        emit(timed, timed.Context with { Position = onset }, duration);
    }

    private static FractionalPosition Onset(GpLegacyTimedBeat timed, string feel) => timed.IsTuplet
        ? timed.Context.Position
        : timed.Context.MeasureStart + GpArrangementParser.ApplyFeelToPosition(
            timed.Context.Position - timed.Context.MeasureStart, feel);
}
