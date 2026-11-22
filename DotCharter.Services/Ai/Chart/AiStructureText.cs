using DotCharter.Localization;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// How a structure change reads to the charter — ONE wording for the review card's row
///     (<c>AiChartReviewViewModel</c>) and the chat's reply (<c>AiChatInstructionRunner</c>), so the
///     two never describe the same change differently.
/// </summary>
public static class AiStructureText
{
    /// <summary>"Section · Chorus", "Phrase · solo2": the kind and the name the marker ends with
    /// (the old name for a remove).</summary>
    public static string Label(AiStructureChange change, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(loc);
        if (change.Event is not null) return loc["AiStructure_EventLabel"];
        var name = change.NameAfter ?? change.OldName ?? "";
        return change.Kind == AiStructureKind.Section
            ? loc.Format("AiStructure_Section", RsSectionCatalog.DisplayName(name))
            : loc.Format("AiStructure_Phrase", name);
    }

    /// <summary>"New", "Renamed from “Verse”", "Moved from 0:12.5", "Removed" — and, for a phrase
    /// that brings the hand position it needs, " · + hand position at fret 5".</summary>
    public static string Detail(AiStructureReviewItem item, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(loc);
        var change = item.Change;
        if (change.Event is { } edit) return EventDetail(edit, loc);
        var detail = change.Op switch
        {
            AiStructureOp.Add => loc["AiStructure_Add"],
            AiStructureOp.Rename => loc.Format("AiStructure_Rename", change.Kind == AiStructureKind.Section
                ? RsSectionCatalog.DisplayName(change.OldName ?? "")
                : change.OldName ?? ""),
            AiStructureOp.Move => loc.Format("AiStructure_Move", Clock(item.FromMs ?? 0)),
            _ => loc["AiStructure_Remove"]
        };
        return AnchorNote(change, loc) is { } note ? $"{detail} · {note}" : detail;
    }

    /// <summary>The fields an event change sets, one phrase each, then the arrangements it covers: "Section · Chorus · Status Draft · On Lead, Rhythm".</summary>
    private static string EventDetail(AiEventEdit e, ILocalizationService loc)
    {
        var parts = new List<string>();
        if (e.Section is { } section)
        {
            parts.Add(section.Length == 0
                ? loc["AiStructure_EventClearSection"]
                : loc.Format("AiStructure_Section", RsSectionCatalog.DisplayName(section)));
        }

        if (e.Status is { } status) parts.Add(loc.Format("AiStructure_EventStatus", loc["SectionStatus_" + status]));
        if (e.Phrase is { } phrase) parts.Add(phrase.Length == 0 ? loc["AiStructure_EventClearPhrase"] : loc.Format("AiStructure_Phrase", phrase));
        if (e.Solo is { } solo) parts.Add(loc[solo ? "AiStructure_EventSolo" : "AiStructure_EventNotSolo"]);
        if (e.Tone is { } tone) parts.Add(tone.Length == 0 ? loc["AiStructure_EventClearTone"] : loc.Format("AiStructure_EventTone", tone));
        if (e.Events is { } codes)
            parts.Add(codes.Count == 0 ? loc["AiStructure_EventNoCodes"] : loc.Format("AiStructure_EventCodes", string.Join(", ", codes)));
        parts.Add(loc.Format("AiStructure_EventOn", string.Join(", ", e.Arrangements)));
        return string.Join(" · ", parts);
    }

    /// <summary>"+ hand position at fret 5" (one fret on every level that gets one) or "+ hand
    /// positions at frets 3, 5"; null when the change adds no hand position.</summary>
    public static string? AnchorNote(AiStructureChange change, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(loc);
        var frets = change.Anchors.Select(a => a.Fret).Distinct().Order().ToList();
        return frets.Count switch
        {
            0 => null,
            1 => loc.Format("AiStructure_AnchorAdded", frets[0]),
            _ => loc.Format("AiStructure_AnchorsAdded", string.Join(", ", frets))
        };
    }

    /// <summary>Elapsed song time with tenths, the way the review card reads a time.</summary>
    public static string Clock(double ms) =>
        ElapsedTimeLabel.Format(ms, 1);
}
