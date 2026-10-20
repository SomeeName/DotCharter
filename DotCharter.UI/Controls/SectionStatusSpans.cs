namespace DotCharter.UI.Controls;

/// <summary>
///     Pure span math behind the minimap's v0.29 section-status strip: each
///     STATUSED section marker owns the stretch to the next section marker
///     (any status) or the chart end. No Avalonia types — headless-tested.
/// </summary>
public static class SectionStatusSpans
{
    /// <summary>One section marker, projected to milliseconds.</summary>
    public readonly record struct SectionPoint(double TimeMs, SectionStatus Status);

    /// <summary>One tinted stretch of the status strip.</summary>
    public readonly record struct Span(double StartMs, double EndMs, SectionStatus Status);

    /// <summary>Builds the strip spans. Sorts <paramref name="sections" /> in
    /// place (callers hand in a freshly projected list). Sections with
    /// <see cref="SectionStatus.None" /> produce no span but still TERMINATE
    /// the previous one — an untracked section is not part of its neighbor.</summary>
    public static List<Span> Build(List<SectionPoint> sections, double totalMs)
    {
        sections.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));

        var spans = new List<Span>();
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Status == SectionStatus.None) continue;
            var start = Math.Max(0, sections[i].TimeMs);
            var end = Math.Min(i + 1 < sections.Count ? sections[i + 1].TimeMs : totalMs, totalMs);
            if (end > start) spans.Add(new Span(start, end, sections[i].Status));
        }

        return spans;
    }
}
