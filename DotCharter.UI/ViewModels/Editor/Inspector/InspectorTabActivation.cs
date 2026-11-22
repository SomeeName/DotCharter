namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     What the inspector does when its tab strip moves: the tab being left hands over what it still
///     holds, the tab being opened catches up on what changed while it was hidden. Every surface here
///     rebuilds on activation only, never per edit, so a hidden tab costs nothing. Built by
///     <see cref="InspectorViewModel" /> over its own sub-view-models.
/// </summary>
internal sealed class InspectorTabActivation(
    LyricsInspectorViewModel lyrics,
    InspectorIssuesViewModel issues,
    ExportReadinessViewModel exportReadiness,
    SoundQueueViewModel queue,
    PhrasesInspectorViewModel phrases,
    PhraseCatalogViewModel? riffLibrary)
{
    public void Activated(int tab)
    {
        // A hidden tab keeps its box focused: leaving Lyrics commits its draft.
        if (tab != InspectorViewModel.LyricsTabIndex) lyrics.CommitPendingEdit();
        // The review list re-reads only while the Lyrics tab shows; opening it catches up once.
        lyrics.Syllables.IsShown = tab == InspectorViewModel.LyricsTabIndex;
        // The FEHLER groups are rebuilt on activation when an edit left them
        // stale while the tab was hidden (see RebuildIssueGroups).
        if (tab == InspectorViewModel.IssuesTabIndex)
        {
            issues.EnsureIssueGroupsCurrent();
            exportReadiness.RefreshChartReport();
        }

        // Same rule for the work queue, which only this tab binds.
        if (tab == InspectorViewModel.SoundTabIndex) queue.EnsureCurrent();
        // The phrase browser (a Song tab section) rebuilds on activation only
        // (never per tick), and only when its phrases moved: an unchanged list keeps its
        // laid-out cards.
        if (tab == InspectorViewModel.SongTabIndex) phrases.EnsureCurrent();
        // Same rule for the riff library, and it is load-bearing: the catalog
        // reads every stored riff off disk, which is why its own constructor
        // defers the first load to a Background post. Loading it when the tab is
        // actually opened keeps that cost off the boot path.
        if (tab == InspectorViewModel.RiffLibraryTabIndex) riffLibrary?.EnsureLoaded();
    }
}
