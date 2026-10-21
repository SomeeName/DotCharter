using Avalonia.Media;

namespace DotCharter.UI.Editing.Linking;

/// <summary>One placement as the LINKS row draws it: its group, its span in ms and the
/// group's letter, size and colour.</summary>
public readonly record struct LinkSpan(
    LinkGroup Group,
    LinkPlacement Placement,
    double StartMs,
    double EndMs,
    string Letter,
    int GroupSize,
    Color Color);

/// <summary>
///     The LINKS row of one arrangement: every placement of every link group in it, in time
///     order. Pure, so the layer, the hit tester and the hover card read the same spans.
/// </summary>
public static class LinkLaneModel
{
    /// <summary>
    ///     The group colours: a secondary cue next to the letter, so they only need to tell
    ///     neighbours apart, and none of them is a section colour.
    /// </summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(140, 140, 240), // lavender
        Color.FromRgb(242, 102, 90),  // coral
        Color.FromRgb(64, 196, 186),  // teal
        Color.FromRgb(232, 186, 64),  // gold
        Color.FromRgb(226, 110, 190), // pink
        Color.FromRgb(140, 206, 90),  // lime
        Color.FromRgb(90, 168, 240),  // sky
        Color.FromRgb(236, 146, 70)   // orange
    ];

    /// <summary>The colour of <paramref name="group" />: by its position in the project's list.</summary>
    public static Color ColorOf(ChartProject project, LinkGroup group)
    {
        var index = project.LinkGroups.IndexOf(group);
        return Palette[(index < 0 ? 0 : index) % Palette.Length];
    }

    public static List<LinkSpan> Spans(ChartProject? project, Arrangement? arrangement)
    {
        var spans = new List<LinkSpan>();
        if (project == null || arrangement == null) return spans;
        foreach (var group in project.LinkGroups)
        {
            var color = ColorOf(project, group);
            foreach (var placement in group.Placements)
            {
                if (!ReferenceEquals(placement.Arrangement, arrangement)) continue;
                spans.Add(new LinkSpan(group, placement, placement.Position.GetPositionMs(project.Beats),
                    placement.EndPosition.GetPositionMs(project.Beats), group.Letter, group.Placements.Count, color));
            }
        }

        spans.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return spans;
    }

    /// <summary>Does any link group have a placement in <paramref name="arrangement" />? No allocation:
    /// the renderer asks on every ribbon layout.</summary>
    public static bool HasLinks(ChartProject? project, Arrangement arrangement)
    {
        if (project == null) return false;
        foreach (var group in project.LinkGroups)
        {
            foreach (var placement in group.Placements)
            {
                if (ReferenceEquals(placement.Arrangement, arrangement)) return true;
            }
        }

        return false;
    }

    /// <summary>The span under <paramref name="timeMs" />, or null.</summary>
    public static LinkSpan? HitTest(IReadOnlyList<LinkSpan> spans, double timeMs)
    {
        foreach (var span in spans)
        {
            if (timeMs >= span.StartMs && timeMs < span.EndMs) return span;
        }

        return null;
    }

    /// <summary>The placement of <paramref name="arrangement" /> that holds <paramref name="position" />, with its group.</summary>
    public static (LinkGroup Group, LinkPlacement Placement)? PlacementAt(ChartProject? project, Arrangement? arrangement,
        FractionalPosition position)
    {
        if (project == null || arrangement == null) return null;
        foreach (var group in project.LinkGroups)
        {
            foreach (var placement in group.Placements)
            {
                if (ReferenceEquals(placement.Arrangement, arrangement) && position >= placement.Position &&
                    position < placement.EndPosition)
                {
                    return (group, placement);
                }
            }
        }

        return null;
    }

    /// <summary>"Lead ×3, Rhythm ×3": the group's placements counted per arrangement, in project order.</summary>
    public static string PerArrangement(ChartProject project, LinkGroup group) =>
        string.Join(", ", project.Arrangements
            .Select(a => (a.Name, Count: group.Placements.Count(p => ReferenceEquals(p.Arrangement, a))))
            .Where(c => c.Count > 0)
            .Select(c => $"{c.Name} ×{c.Count}"));
}
