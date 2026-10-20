namespace DotCharter.UI.ViewModels.Dialogs;

/// <summary>One numbered row of the toolbar undo/redo hover preview.</summary>
/// <param name="Step">The 1-based position in the chain.</param>
/// <param name="Description">The humanized action.</param>
/// <param name="Time">When it happened, local wall clock.</param>
/// <param name="IsCurrent">
///     True for the FIRST row — the step the next press acts on, which
///     `Play Menus.dc.html` draws lit with a glowing dot. It is carried on the
///     item rather than derived in the markup because a binding into a
///     collection BY INDEX resolves against an empty list on every refresh
///     (the repo-wide rule pinned by <c>UI/IssueDotSeverityTests</c>).
/// </param>
public sealed record HistoryPreviewItem(int Step, string Description, string Time, bool IsCurrent = false);
