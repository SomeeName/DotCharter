namespace DotCharter.Core.Domain.Validation;

/// <summary>
///     One chart-check WARNING the charter accepted as intended: the rule's code, the finding's English
///     sentence and a fingerprint of its subject (the element or lyric line it is about, as it stood when ignored).
///     The Issues tab hides a finding whose three values match; an edit to the subject changes the fingerprint, so
///     the finding comes back. Saved in the project as <c>&lt;dcIgnoredFindings&gt;</c>. An export ERROR is never
///     ignored: the Issues tab only matches warnings, and export validation never reads this list.
/// </summary>
public sealed record IgnoredFinding(string Code, string Message, string Subject);
