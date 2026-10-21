using DotCharter.Core.Linking;

namespace DotCharter.UI.ViewModels.Editor.Linking;

/// <summary>Measure numbers for linked placements, as the timeline's measure pills print them (M1 is the first measure).</summary>
public static class LinkMeasureLabels
{
    /// <summary>The 1-based number of the measure starting at <paramref name="position" />, or 0.</summary>
    public static int MeasureAt(IReadOnlyList<MeasureSpan> measures, FractionalPosition position)
    {
        for (var i = 0; i < measures.Count; i++)
        {
            if (measures[i].Start == position) return i + 1;
        }

        return 0;
    }

    /// <summary>How many measures the placement spans.</summary>
    public static int MeasureCount(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement) =>
        LinkRuns.TryResolve(measures, placement, out _, out var count) ? count : 0;

    /// <summary>"M13–M20", or "M13" for one measure.</summary>
    public static string Range(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement)
    {
        var first = MeasureAt(measures, placement.Position);
        return MeasureRangeLabel.Format(first, MeasureCount(measures, placement));
    }

    /// <summary>"4/4" when every measure of the placement has that signature, else "".</summary>
    public static string Signature(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement)
    {
        if (!LinkRuns.TryResolve(measures, placement, out var first, out var count) || count == 0) return "";
        var head = measures[first];
        for (var i = first + 1; i < first + count; i++)
        {
            if (measures[i].BeatsInMeasure != head.BeatsInMeasure || measures[i].NoteDenominator != head.NoteDenominator) return "";
        }

        return $"{head.BeatsInMeasure}/{head.NoteDenominator}";
    }
}
