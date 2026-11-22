using DotCharter.Core.Linking;

namespace DotCharter.Services.Parsing.GuitarPro;

public static class GpSourceMap
{
    internal static GpBarInfo DescribeLegacy(GpLegacyMeasureHeader header, GpMasterBarSequence.UnfoldedIndex bar) => new()
    {
        BeatsInMeasure = header.Numerator,
        NoteDenominator = header.Denominator,
        TripletFeel = header.TripletFeel,
        SourceBarIndex = bar.OriginalIndex,
        RepeatPass = bar.RepeatPass,
        EndingMask = header.AlternateEndingsMask,
        StartsBlock = bar.StartsBlock,
        EndsBlock = bar.EndsBlock,
        Marker = header.Marker?.Trim() ?? ""
    };

    public static IReadOnlyList<GpSourceBar> Capture(GpImportMetadata metadata, IList<BeatMarker> beats)
    {
        var measures = MeasureGrid.Build(beats);
        if (measures.Count != metadata.Bars.Count) return [];
        return [.. measures.Select(m => Capture(m, metadata.Bars[m.Index]))];
    }

    public static bool Matches(IReadOnlyList<GpSourceBar> bars, IReadOnlyList<MeasureSpan> measures) =>
        bars.Count > 0 && bars.Count == measures.Count && measures.All(m => Matches(bars[m.Index], m));

    private static bool Matches(GpSourceBar bar, MeasureSpan measure) =>
        bar.StartBeat == measure.StartBeat && bar.BeatCount == measure.BeatCount &&
        bar.BeatsInMeasure == measure.BeatsInMeasure && bar.NoteDenominator == measure.NoteDenominator;

    private static GpSourceBar Capture(MeasureSpan measure, GpBarInfo bar) => new()
    {
        StartBeat = measure.StartBeat,
        BeatCount = measure.BeatCount,
        BeatsInMeasure = measure.BeatsInMeasure,
        NoteDenominator = measure.NoteDenominator,
        WrittenBar = bar.SourceBarIndex,
        Pass = bar.RepeatPass,
        EndingMask = bar.EndingMask,
        StartsBlock = bar.StartsBlock,
        EndsBlock = bar.EndsBlock,
        Marker = bar.Marker
    };
}
