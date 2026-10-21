namespace DotCharter.Core.Linking;

/// <summary>How a run of measures is written wherever linked measures are shown: "M13–M20", or "M13" for one measure.</summary>
public static class MeasureRangeLabel
{
    /// <summary>The label for <paramref name="count" /> measures starting at the 1-based measure <paramref name="first" />.</summary>
    public static string Format(int first, int count) =>
        count <= 1 ? $"M{first}" : $"M{first}–M{first + count - 1}";
}
