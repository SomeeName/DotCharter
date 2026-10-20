using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The inspector's Notes tab: charting annotations added at the playhead,
///     kept time-ordered, jumped to and removed. Exposed as
///     <c>InspectorViewModel.Annotations</c>; the tab binds through that property.
///     <para>
///         Both verbs go through <see cref="IHistoryService" />: annotations are
///         persisted chart content (the cloner and <c>ChartProjectRestorer</c>
///         both carry them), so this tab must not mutate the collection
///         directly — an add or the one-click ✕ delete would be invisible to Ctrl+Z
///         and never mark the session dirty. The ordered insert lives in
///         <see cref="AddAnnotationAction" /> so redo re-derives the slot.
///     </para>
/// </summary>
public sealed partial class AnnotationsInspectorViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly IHistoryService _historyService;
    private readonly ChartEditorViewModel _chartEditor;

    [ObservableProperty] private string _newAnnotationText = string.Empty;
    [ObservableProperty] private int _newAnnotationKindIndex;

    public AnnotationsInspectorViewModel(
        ProjectState projectState,
        IHistoryService historyService,
        ChartEditorViewModel chartEditor)
    {
        _projectState = projectState;
        _historyService = historyService;
        _chartEditor = chartEditor;
    }

    [RelayCommand]
    private void AddAnnotationAtPlayhead()
    {
        var project = _projectState.ActiveProject;
        if (project == null || string.IsNullOrWhiteSpace(NewAnnotationText))
        {
            return;
        }

        var annotation = new ChartAnnotation
        {
            TimeMs = _projectState.CurrentTimeMs,
            Text = NewAnnotationText.Trim(),
            Kind = (ChartAnnotationKind)Math.Clamp(NewAnnotationKindIndex, 0, 2)
        };

        _historyService.Do(new AddAnnotationAction(project.Annotations, annotation));

        NewAnnotationText = string.Empty;
    }

    [RelayCommand]
    private void RemoveAnnotation(ChartAnnotation? annotation)
    {
        var project = _projectState.ActiveProject;
        if (annotation == null || project == null)
        {
            return;
        }

        _historyService.Do(new RemoveAnnotationAction(project.Annotations, annotation));
    }

    [RelayCommand]
    private void JumpToAnnotation(ChartAnnotation? annotation)
    {
        if (annotation == null)
        {
            return;
        }

        _chartEditor.ExecuteSeek(annotation.TimeMs);
    }
}
