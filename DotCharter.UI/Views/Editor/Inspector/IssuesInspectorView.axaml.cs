using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

// Triage rows are single-click buttons bound to JumpToIssueCommand /
// JumpToExportFindingCommand, so no code-behind interaction handlers remain.
public partial class IssuesInspectorView : UserControl
{
    public IssuesInspectorView() => InitializeComponent();
}
