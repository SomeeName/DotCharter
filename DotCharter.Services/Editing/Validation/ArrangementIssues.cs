namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Factory helpers for building <see cref="ArrangementIssue"/> values from a
///     rule. Kept internal so rules share a single construction site; tests
///     reach these indirectly through <c>ArrangementValidationService.Validate</c>.
/// </summary>
internal static class ArrangementIssues
{
    public static ArrangementIssue Note(ArrangementIssueType type, GuitarNote note, double timeMs, string msg,
        int? stringIndex = null) =>
        new(type, msg, timeMs, Note: note, StringIndex: stringIndex ?? note.StringIndex);

    public static ArrangementIssue Chord(ArrangementIssueType type, Chord chord, double timeMs, string msg,
        int? stringIndex = null) =>
        new(type, msg, timeMs, Chord: chord, StringIndex: stringIndex);
}
