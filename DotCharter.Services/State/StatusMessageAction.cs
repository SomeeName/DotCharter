namespace DotCharter.Services.State;

/// <summary>
///     The one verb a status-rail message may carry (<see cref="ProjectState.SetMessage(string, StatusSeverity, StatusMessageAction)" />):
///     <paramref name="Label" /> is the button's localized text, <paramref name="Run" /> what it does.
/// </summary>
public sealed record StatusMessageAction(string Label, Action Run);
