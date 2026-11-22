using DotCharter.Localization;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Selection;

/// <summary>Why a "select more like this" command added what it added (or nothing).</summary>
public enum SelectionAddOutcome
{
    /// <summary>No focused or active track to search.</summary>
    NoTrack,

    /// <summary>Nothing was selected to derive the match from.</summary>
    NothingSelected,

    /// <summary>The seed sits on another arrangement than the one being searched.</summary>
    ForeignSeed,

    /// <summary>The search ran; <see cref="SelectionAddition.Total" /> may still be zero.</summary>
    Searched
}

/// <summary>
///     What a select-similar or pattern run ADDED to the selection, per kind, and how many
///     elements are selected afterwards.
/// </summary>
public readonly record struct SelectionAddition(
    SelectionAddOutcome Outcome, int Notes, int Chords, int HandShapes, int Anchors, int Selected)
{
    public int Total => Notes + Chords + HandShapes + Anchors;

    public static SelectionAddition None(SelectionAddOutcome outcome) => new(outcome, 0, 0, 0, 0, 0);
}

/// <summary>The status-rail words for a <see cref="SelectionAddition" />: "Added 12 notes, 2 chords (14 selected)".</summary>
public static class SelectionAdditionStatus
{
    public static (string Text, StatusSeverity Severity) Describe(SelectionAddition r, ILocalizationService l)
    {
        switch (r.Outcome)
        {
            case SelectionAddOutcome.NoTrack:
            case SelectionAddOutcome.NothingSelected:
                return (l["Status_SelectSimilarNeedsSelection"], StatusSeverity.Warning);
            case SelectionAddOutcome.ForeignSeed:
                return (l["Status_SelectSimilarForeignSeed"], StatusSeverity.Warning);
        }

        if (r.Total == 0) return (l["Status_SelectSimilarNothingFound"], StatusSeverity.Info);

        var parts = new List<string>(4);
        if (r.Notes > 0) parts.Add(l.Plural("Status_SelectAddedNote", "Status_SelectAddedNotes", r.Notes));
        if (r.Chords > 0) parts.Add(l.Plural("Status_SelectAddedChord", "Status_SelectAddedChords", r.Chords));
        if (r.HandShapes > 0) parts.Add(l.Plural("Status_SelectAddedHandShape", "Status_SelectAddedHandShapes", r.HandShapes));
        if (r.Anchors > 0) parts.Add(l.Plural("Status_SelectAddedAnchor", "Status_SelectAddedAnchors", r.Anchors));
        return (l.Format("Status_SelectAdded", string.Join(", ", parts), r.Selected), StatusSeverity.Info);
    }
}
