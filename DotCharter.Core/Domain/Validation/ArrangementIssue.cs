using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Validation;

/// <summary>
///     Result item produced by <c>ArrangementValidationService</c>. The optional
///     domain back-references let the inspector both seek to the issue's time
///     and select the offending element when the user double-clicks an entry.
///     <para>
///         <paramref name="HandShapes" /> is for a SUMMARY finding that stands for
///         several shapes at once (D09 is reported once per arrangement): every
///         shape it covers, so a surface that marks shapes (the 2D timeline's
/// hand-shape bars) reads them off the finding instead of re-running
///         the rule. Null on every per-element finding — those use
///         <paramref name="HandShape" />.
///     </para>
///     <para>
///         <paramref name="Message" /> is always the English sentence (logs, reports, tests).
///         <paramref name="MessageKey" /> and <paramref name="MessageArgs" /> name the resource it
///         was formatted from, so a surface that shows it to the user renders it in the UI
///         language; an argument may itself be an <see cref="IssueTextRef" />.
///     </para>
///     <para>
///         <paramref name="Lyric" /> names the syllable a lyric finding is about by its place in
///         the project's vocal tracks, which survives the copy the Issues tab validates.
///     </para>
/// </summary>
public sealed record ArrangementIssue(
    ArrangementIssueType Type,
    string Message,
    double? TimeMs = null,
    GuitarNote? Note = null,
    Chord? Chord = null,
    HandShape? HandShape = null,
    FretAnchor? Anchor = null,
    int? StringIndex = null,
    IReadOnlyList<HandShape>? HandShapes = null,
    string? MessageKey = null,
    IReadOnlyList<object>? MessageArgs = null,
    LyricPlace? Lyric = null)
{
    /// <summary>Translation metadata renders the existing English sentence; finding identity
    /// remains its original domain fields, including across independent validation passes.</summary>
    public bool Equals(ArrangementIssue? other) => other is not null && Identity().Equals(other.Identity());

    public override int GetHashCode() => Identity().GetHashCode();

    private (ArrangementIssueType, string, double?, GuitarNote?, Chord?, HandShape?, FretAnchor?, int?, IReadOnlyList<HandShape>?, LyricPlace?) Identity() =>
        (Type, Message, TimeMs, Note, Chord, HandShape, Anchor, StringIndex, HandShapes, Lyric);

    public string Code => Type.GetCode();
    public ArrangementIssueSeverity Severity => Type.GetSeverity();

    /// <summary>
    ///     Returns true if the issue is bound to a specific point in time and can
    ///     be navigated to via double-click in the UI.
    /// </summary>
    public bool HasTime => TimeMs.HasValue;
}
