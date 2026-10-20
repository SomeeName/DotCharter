using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The one answer to "what tones does this chart actually have", shared by
///     everything that has to ask.
///
///     <para>
///         A tone name reaches the rig only if TWO things line up: the arrangement
///         declares it (a slot, or a <c>ToneChange</c> naming it) and the project
///         owns a BODY for it (<see cref="ChartProject.ToneDefinitions" />). Those
///         are separate collections, edited in separate places, and the failure
///         when they disagree is silent — a tone change that never applies. So the
///         answer is derived in ONE place rather than re-spelled at each caller.
///     </para>
///     <para>
///         Matching is trimmed and case-insensitive throughout, and a body is
///         indexed under its Key AND its Name, because that is exactly how
///         <c>RsTonePlaybackCoordinator</c> resolves a <c>ToneChange.ToneName</c>
///         at playback. A stricter comparison here would report a tone as missing
///         that the rig then plays perfectly.
///     </para>
/// </summary>
public static class ChartToneNames
{
    /// <summary>Every name the project owns a tone BODY for — keys and display
    /// names both, since a chart may reference either.</summary>
    public static IReadOnlyList<string> Defined(ChartProject? project)
    {
        var result = new List<string>();
        if (project is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tone in project.ToneDefinitions)
        {
            AddIfNew(result, seen, tone.Key);
            AddIfNew(result, seen, tone.Name);
        }

        return result;
    }

    /// <summary>Every name the arrangement DECLARES: the base/starting tone, the
    /// four Rocksmith slots, and <c>ToneNames</c>. Deliberately NOT the tone
    /// changes — a change naming an undeclared tone is the fault, not a
    /// declaration of it.</summary>
    public static IReadOnlyList<string> Declared(Arrangement? arrangement)
    {
        var result = new List<string>();
        if (arrangement is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfNew(result, seen, arrangement.StartingTone);
        AddIfNew(result, seen, arrangement.ToneBase);
        AddIfNew(result, seen, arrangement.ToneA);
        AddIfNew(result, seen, arrangement.ToneB);
        AddIfNew(result, seen, arrangement.ToneC);
        AddIfNew(result, seen, arrangement.ToneD);
        foreach (var name in arrangement.ToneNames)
            AddIfNew(result, seen, name);

        return result;
    }

    /// <summary>
    ///     What a tone picker should offer, most-relevant first: the tones this
    ///     arrangement declares, then any other body the project owns, then names
    ///     already used by its tone changes (so an imported chart's own vocabulary
    ///     is reachable even where nothing declares it).
    /// </summary>
    public static IReadOnlyList<string> Offerable(ChartProject? project, Arrangement? arrangement)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Declared(arrangement)) AddIfNew(result, seen, name);
        foreach (var name in Defined(project)) AddIfNew(result, seen, name);
        if (arrangement is not null)
        {
            foreach (var change in arrangement.ToneChanges)
                AddIfNew(result, seen, change.ToneName);
        }

        return result;
    }

    /// <summary>True when the project owns a body for <paramref name="toneName" />
    /// — i.e. the tone can actually be BUILT. Blank is false: a nameless tone
    /// change resolves to nothing.</summary>
    public static bool HasBody(ChartProject? project, string? toneName)
    {
        if (project is null || string.IsNullOrWhiteSpace(toneName)) return false;

        var needle = toneName.Trim();
        foreach (var tone in project.ToneDefinitions)
        {
            if (Matches(tone.Key, needle) || Matches(tone.Name, needle))
                return true;
        }

        return false;
    }

    /// <summary>True when the arrangement declares <paramref name="toneName" /> on
    /// a slot. Blank is false.</summary>
    public static bool IsDeclared(Arrangement? arrangement, string? toneName)
    {
        if (arrangement is null || string.IsNullOrWhiteSpace(toneName)) return false;

        var needle = toneName.Trim();
        foreach (var name in Declared(arrangement))
        {
            if (Matches(name, needle))
                return true;
        }

        return false;
    }

    private static bool Matches(string? candidate, string trimmedNeedle) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        string.Equals(candidate.Trim(), trimmedNeedle, StringComparison.OrdinalIgnoreCase);

    private static void AddIfNew(List<string> into, HashSet<string> seen, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (seen.Add(trimmed)) into.Add(trimmed);
    }
}
