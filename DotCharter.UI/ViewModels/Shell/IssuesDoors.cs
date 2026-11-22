using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The doors into the Inspector's Issues tab, one per section it holds: the status rail's counters open
///     Findings, Tools ▸ Chart quality report and the Rocksmith export's "Show in Issues" open Whole project,
///     the Editor on Fire export's "Show" opens Editor on Fire. Each brings the chart editor up first.
/// </summary>
public sealed class IssuesDoors(InspectorViewModel inspector, Action showChartEditor)
{
    public Action Findings => () => Open(IssuesSection.Findings);

    public Action Project => () => Open(IssuesSection.Project);

    public Action EditorOnFire => () => Open(IssuesSection.EditorOnFire);

    private void Open(IssuesSection section)
    {
        showChartEditor();
        inspector.ShowIssuesSection(section);
        inspector.ExportReadiness.RefreshChartReport();
    }
}
