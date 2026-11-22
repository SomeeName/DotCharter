using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>The Issues tab's sections — the card the pager shows (design D).</summary>
public enum IssuesSection
{
    /// <summary>The active arrangement's findings, the selected finding's fixes and the all-clear state.</summary>
    Findings,

    /// <summary>The project's verdict, a row per arrangement, the audio check and the export check.</summary>
    Project,

    /// <summary>What an Editor on Fire export of this chart would approximate or drop.</summary>
    EditorOnFire
}

/// <summary>
///     Which section of the Issues tab is on screen, and what each card's page button has to say. The
///     doors into the tab (the status rail, Tools ▸ Chart quality report, the Editor on Fire export's
///     "Show", the Sound tab) open a section through <see cref="Open" />. The round trip is computed
///     exactly while its section is open or its card is on screen anyway (a wide pane shows every card,
///     <see cref="ShowEditorOnFire" />): <see cref="IEofRoundTripSection.IsExpanded" /> follows both.
/// </summary>
public sealed partial class IssuesSectionsViewModel : ObservableObject
{
    private readonly ExportReadinessViewModel _readiness;
    private IssuesSection _section;
    private bool _editorOnFireShown;
    private bool _syncing;

    public IssuesSectionsViewModel(ExportReadinessViewModel readiness)
    {
        _readiness = readiness;
        readiness.ProjectArrangementSelected = () => Open(IssuesSection.Findings);
        readiness.PropertyChanged += (_, _) => NotifyBadges();
        readiness.EofRoundTrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEofRoundTripSection.IsExpanded) && !_syncing)
            {
                // an export asked to show its losses, or a project switch closed the check
                if (readiness.EofRoundTrip.IsExpanded) Open(IssuesSection.EditorOnFire);
                else if (_section == IssuesSection.EditorOnFire) Open(IssuesSection.Findings);
                else if (_editorOnFireShown) SyncRoundTrip(); // the card is still on screen: compute it for the new project
            }

            NotifyBadges();
        };
    }

    public IssuesSection Section => _section;

    public bool IsFindings => _section == IssuesSection.Findings;

    public bool IsProject => _section == IssuesSection.Project;

    public bool IsEditorOnFire => _section == IssuesSection.EditorOnFire;

    /// <summary>The Whole project entry has something to say: a finding in some arrangement, a project that is not ready, or an export finding.</summary>
    public bool ProjectNeedsAttention => _readiness.ProjectNeedsAttention;

    /// <summary>The round trip was computed and something would be dropped.</summary>
    public bool EditorOnFireDrops => _readiness.EofRoundTrip.Groups.Any(g => g.IsDropped);

    /// <summary>Opens a section.</summary>
    public void Open(IssuesSection section)
    {
        _section = section;
        SyncRoundTrip();
        NotifyLayout();
    }

    /// <summary>
    ///     The Editor on Fire card is on screen without being the opened section (the pane is wide enough to show
    ///     every card), or leaves the screen: the round trip is computed while it is on screen, and opens no section.
    /// </summary>
    public void ShowEditorOnFire(bool shown)
    {
        if (_editorOnFireShown == shown) return;
        _editorOnFireShown = shown;
        SyncRoundTrip();
    }

    private void SyncRoundTrip()
    {
        _syncing = true;
        try { _readiness.EofRoundTrip.IsExpanded = _section == IssuesSection.EditorOnFire || _editorOnFireShown; }
        finally { _syncing = false; }
    }

    /// <summary>
    ///     A click on a page button. Re-announces the flags even when the section did not change: a
    ///     click on the lit entry unchecks it locally, and its OneWay binding only re-reads on a
    ///     notification.
    /// </summary>
    [RelayCommand]
    private void Choose(IssuesSection section) => Open(section);

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(IsFindings));
        OnPropertyChanged(nameof(IsProject));
        OnPropertyChanged(nameof(IsEditorOnFire));
    }

    private void NotifyBadges()
    {
        OnPropertyChanged(nameof(ProjectNeedsAttention));
        OnPropertyChanged(nameof(EditorOnFireDrops));
    }
}
