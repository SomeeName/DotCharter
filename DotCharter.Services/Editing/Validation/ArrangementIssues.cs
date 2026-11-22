namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Factory helpers for building <see cref="ArrangementIssue"/> values from a
///     rule. Kept internal so rules share a single construction site; tests
///     reach these indirectly through <c>ArrangementValidationService.Validate</c>.
///     Every finding names the resource its sentence comes from (<c>key</c>, <c>args</c>);
///     the English <see cref="ArrangementIssue.Message" /> is read off that same resource.
/// </summary>
internal static class ArrangementIssues
{
    public static ArrangementIssue Make(ArrangementIssueType type, string key, double? timeMs = null,
        GuitarNote? note = null, Chord? chord = null, HandShape? handShape = null, FretAnchor? anchor = null,
        int? stringIndex = null, IReadOnlyList<HandShape>? handShapes = null, object[]? args = null) =>
        new(type, ArrangementIssueText.English(key, args), timeMs, note, chord, handShape, anchor, stringIndex,
            handShapes, key, args);

    public static ArrangementIssue Note(ArrangementIssueType type, GuitarNote note, double timeMs, string key,
        int? stringIndex = null, object[]? args = null) =>
        Make(type, key, timeMs, note: note, stringIndex: stringIndex ?? note.StringIndex, args: args);

    public static ArrangementIssue Chord(ArrangementIssueType type, Chord chord, double timeMs, string key,
        int? stringIndex = null, object[]? args = null) =>
        Make(type, key, timeMs, chord: chord, stringIndex: stringIndex, args: args);
}
