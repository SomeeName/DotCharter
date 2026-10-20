using System.Globalization;
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

    /// <summary>m:ss.f, the way the review card reads a time.</summary>
    public static string Clock(double ms) =>
        TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
}
