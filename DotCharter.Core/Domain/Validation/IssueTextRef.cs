namespace DotCharter.Core.Domain.Validation;

/// <summary>
///     A resource key plus its arguments, used as an argument of
///     <see cref="ArrangementIssue.MessageArgs" /> when a phrase inside a finding's sentence
///     must itself be translated (for example a fret range).
/// </summary>
public sealed record IssueTextRef(string Key, IReadOnlyList<object>? Args = null);
