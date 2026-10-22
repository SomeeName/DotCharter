namespace DotCharter.Core.Utilities;

/// <summary>One Rocksmith event code and the label a human should see.</summary>
/// <param name="Code">The code as it is written to the XML — case-sensitive.</param>
/// <param name="DisplayName">Editor on Fire's own label for it.</param>
public readonly record struct RsEventDefinition(string Code, string DisplayName);

/// <summary>
///     The Rocksmith EVENTS a chart can carry, alongside its sections and
///     phrases: two metronome ticks and the crowd-control codes.
///     <para>
///         These are a separate vocabulary from <see cref="RsSectionCatalog" />
///         and are stored on the same marker — <c>EventPoint.Events</c>, a
///         comma-joined list, round-tripped by the RS-XML reader/writer and by
///         the <c>.dcproj</c> project format. This catalog is what lets the editor AUTHOR one:
///         without a control for it the list is only filled by the importers, and
///         the only way to get a crowd cue into a chart is to
///         import one that already has it.
///     </para>
///     <para>
///         Ported verbatim from Editor on Fire's
///         <c>eof_rs_predefined_events[]</c> (<c>EOF_SRC/rs.c:66</c>), including
///         its display names.
///     </para>
///     <para>
///         <b>Codes are compared ORDINALLY, unlike section names.</b> EOF matches
///         events with <c>ustrcmp</c> and sections with <c>ustricmp</c>, and the
///         set here is why: <c>e0</c>/<c>e1</c>/<c>e2</c> are lower-case while
///         <c>B0</c>/<c>B1</c>/<c>E3</c>/<c>D3</c>/<c>E13</c> are upper-case, and
///         <c>E3</c> (crowd applause) is not <c>e3</c>.
///     </para>
/// </summary>
public static class RsEventCatalog
{
    /// <summary>Every predefined event, in EOF's order: the ticks first, then
    ///     the crowd controls.</summary>
    public static readonly IReadOnlyList<RsEventDefinition> Events =
    [
        new RsEventDefinition("B0", "High pitch tick (B0)"),
        new RsEventDefinition("B1", "Low pitch tick (B1)"),
        new RsEventDefinition("e0", "Crowd speed slow (e0)"),
        new RsEventDefinition("e1", "Crowd speed default (e1)"),
        new RsEventDefinition("e2", "Crowd speed fast (e2)"),
        new RsEventDefinition("E3", "Crowd applause (E3)"),
        new RsEventDefinition("D3", "Crowd critique applause (D3)"),
        new RsEventDefinition("E13", "End crowd applause (E13)")
    ];

    private static readonly HashSet<string> Lookup = new(Events.Select(e => e.Code), StringComparer.Ordinal);

    /// <summary>True when <paramref name="code" /> is one of the predefined
    ///     events. Ordinal — see the type remarks.</summary>
    public static bool IsKnown(string? code) => code != null && Lookup.Contains(code);

    /// <summary>
    ///     The codes on a marker, in stored order, with blanks and duplicates
    ///     removed. Never throws: the field is free text from someone else's
    ///     exporter as often as it is ours.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? codes)
    {
        if (string.IsNullOrWhiteSpace(codes)) return [];

        var result = new List<string>();
        foreach (var raw in codes.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = raw.Trim();
            if (code.Length == 0) continue;
            if (!result.Contains(code, StringComparer.Ordinal)) result.Add(code);
        }

        return result;
    }

    /// <summary>The stored form: comma-joined, no spaces.</summary>
    public static string Join(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return string.Join(',', codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));
    }

    /// <summary>
    ///     Adds or removes one code, preserving every other code on the marker —
    ///     including ones this catalogue does not know, which belong to whoever
    ///     authored the chart and are not ours to drop.
    /// </summary>
    public static string Toggle(string? codes, string code, bool present)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var list = Parse(codes).ToList();
        var index = list.FindIndex(c => string.Equals(c, code, StringComparison.Ordinal));

        if (present && index < 0) list.Add(code);
        else if (!present && index >= 0) list.RemoveAt(index);

        return Join(list);
    }

    /// <summary>A human label for a code — the predefined ones get EOF's, and
    ///     anything else is shown as itself rather than hidden.</summary>
    public static string DisplayName(string code)
    {
        foreach (var known in Events)
        {
            if (string.Equals(known.Code, code, StringComparison.Ordinal))
                return known.DisplayName;
        }

        return code;
    }
}
