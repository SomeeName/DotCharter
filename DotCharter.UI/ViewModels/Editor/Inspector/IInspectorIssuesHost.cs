namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     What the Issues tab (<see cref="InspectorIssuesViewModel" />) needs from the
/// inspector that hosts it — three things, and none of them a field.
///     <c>InspectorViewModel</c> implements it and attaches itself with
///     <see cref="InspectorIssuesViewModel.AttachHost" />.
/// </summary>
public interface IInspectorIssuesHost
{
    /// <summary>True while the Issues tab is on screen. The severity groups are bound
    /// by that tab alone, so they are rebuilt only then and go stale otherwise.</summary>
    bool IsIssuesTabSelected { get; }

    /// <summary>A validation pass has a fresh issue list: re-read what the host
    /// derives from it (the selection's findings, the Sound tab's work queue).</summary>
    void OnIssuesRecomputed();

    /// <summary>Runs a selection change the INSPECTOR makes (an issue jump), so the
    /// host does not take it for user intent and switch away from the Issues tab.</summary>
    void SelectProgrammatically(Action select);
}
