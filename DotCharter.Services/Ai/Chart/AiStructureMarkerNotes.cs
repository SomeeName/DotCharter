using DotCharter.Services.Editing.Events;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     The compact extra facts the structure listing gives a marker so an <c>event</c> change keeps
///     or changes them on purpose: the status (a section), the solo flag (a phrase), the tone placed
///     there, the Rocksmith event codes and the arrangements that carry the same marker. Only
///     non-default fields are listed, so a plain marker costs no extra tokens.
/// </summary>
internal static class AiStructureMarkerNotes
{
    /// <summary>The notes of one marker, "" when it has only defaults.</summary>
    /// <param name="arrangement">The arrangement being charted.</param>
    /// <param name="position">The marker's position.</param>
    /// <param name="point">The event point at <paramref name="position" />, or null.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="project">Every arrangement of the song; null leaves the scope out.</param>
    /// <param name="ownsPosition">False for a phrase sharing its beat with a section: tone, codes and scope are listed once, on the section.</param>
    /// <param name="phrase">The phrase's name, for the solo flag.</param>
    internal static string Of(Arrangement arrangement, FractionalPosition position, EventPoint? point,
        IList<BeatMarker> beats, IReadOnlyList<Arrangement>? project, bool ownsPosition, string? phrase = null)
    {
        var parts = new List<string>();
        if (point is { Status: not SectionStatus.None } && phrase is null)
            parts.Add("status " + point.Status.ToString().ToLowerInvariant());
        if (phrase is not null && arrangement.PhraseInfos.TryGetValue(phrase, out var info) && info.Solo) parts.Add("solo");
        if (!ownsPosition) return string.Join(", ", parts);
        var timeMs = position.GetPositionMs(beats);
        if (EventMarkerEdit.NearestTone(arrangement.ToneChanges, timeMs, beats) is { } tone) parts.Add($"tone \"{tone.ToneName}\"");
        if (!string.IsNullOrWhiteSpace(point?.Events)) parts.Add("events " + string.Join(' ', RsEventCatalog.Parse(point.Events)));
        if (point is not null && project is { Count: > 1 } && Scope(point, timeMs, beats, project) is { Length: > 0 } scope)
            parts.Add("on " + scope);
        return string.Join(", ", parts);
    }

    private static string Scope(EventPoint point, double timeMs, IList<BeatMarker> beats, IReadOnlyList<Arrangement> project)
    {
        var names = project.Where(a => EventMarkerEdit.EventAt(a, timeMs, beats) is { } other &&
                                       other.Section == point.Section && other.Phrase == point.Phrase)
            .Select(a => a.Name).ToList();
        return names.Count == project.Count ? "all" : string.Join(", ", names);
    }
}
