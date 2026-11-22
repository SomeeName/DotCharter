using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The inspector's Annotations tab (inspector design D): charting annotations added at the playhead, kept
///     time-ordered, filtered (open and resolved / open / resolved), selected, edited in a details card (type and text),
///     resolved or reopened, jumped to and removed. Exposed as <c>InspectorViewModel.Annotations</c>.
///     <list type="bullet">
///         <item>The filter decides what the list shows; a selection it hides moves to the first visible row and is
///         never left actionable.</item>
///         <item>"Add at playhead" opens a DRAFT in the details card (type and text) at the playhead's time THEN (the
///         heading's time; a playhead that moves on while the text is typed does not move it); "Add annotation" writes
///         it, Cancel drops it. The composer's text and type are the draft's; another project drops both.</item>
///         <item>The Resolved card lists every resolved annotation (a second collection, whatever the filter shows),
///         each reopened or jumped to from its row.</item>
///         <item>The details' text is committed on Enter or when the box loses focus (one undo node), the type when it is
///         picked.</item>
///     </list>
///     <para>
///         Editing verbs go through <see cref="IHistoryService" />: annotations are persisted chart content (the cloner
///         and <c>ChartProjectRestorer</c> both carry them), so this tab must not mutate the collection or a row
///         directly — the edit would be invisible to Ctrl+Z and never mark the session dirty. The ordered insert lives in
///         <see cref="AddAnnotationAction" /> so redo re-derives the slot.
///     </para>
/// </summary>
public sealed partial class AnnotationsInspectorViewModel : ViewModelBase
{
    /// <summary>The filter's entries, in order.</summary>
    public const int ShowAll = 0;

    public const int ShowOpen = 1;
    public const int ShowResolved = 2;

    private readonly ProjectState _projectState;
    private readonly IHistoryService _historyService;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly ILocalizationService _localization;
    private ObservableCollection<ChartAnnotation>? _watched;
    private double _draftTimeMs;

    [ObservableProperty] private string _newAnnotationText = string.Empty;
    [ObservableProperty] private int _newAnnotationKindIndex;
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedIsResolved), nameof(SelectedHeading))] private ChartAnnotation? _selectedAnnotation;
    [ObservableProperty] private string _selectedText = string.Empty;
    [ObservableProperty] private int _selectedKindIndex;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowsSelection), nameof(ShowsNoSelection))] private bool _isDrafting;
    [ObservableProperty] private string _draftHeading = string.Empty;

    public AnnotationsInspectorViewModel(
        ProjectState projectState,
        IHistoryService historyService,
        ChartEditorViewModel chartEditor,
        ILocalizationService localization)
    {
        _localization = localization;
        _projectState = projectState;
        _historyService = historyService;
        _chartEditor = chartEditor;
        _projectState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectState.ActiveProject)) Watch();
        };
        Watch();
    }

    /// <summary>The annotations the filter lets through, time-ordered.</summary>
    public ObservableCollection<ChartAnnotation> VisibleAnnotations { get; } = [];

    /// <summary>Every resolved annotation, time-ordered: the Resolved card's list, independent of the filter.</summary>
    public ObservableCollection<ChartAnnotation> ResolvedAnnotations { get; } = [];

    /// <summary>The Resolved card has rows.</summary>
    public bool HasResolved => ResolvedAnnotations.Count > 0;

    /// <summary>The project has any annotation (the scope sentence and empty hint show otherwise).</summary>
    public bool HasAnnotations => _watched is { Count: > 0 };

    /// <summary>Annotations exist but the filter hides every one.</summary>
    public bool FilterHidesAll => HasAnnotations && VisibleAnnotations.Count == 0;

    public bool HasSelection => SelectedAnnotation is not null;

    public bool SelectedIsResolved => SelectedAnnotation?.Resolved == true;

    /// <summary>"At 0:14.000": the details card's title.</summary>
    public string SelectedHeading => SelectedAnnotation is { } annotation ? _localization.Format("Notes_At", annotation.TimeLabel) : string.Empty;

    /// <summary>The details card shows the selected annotation (not a draft).</summary>
    public bool ShowsSelection => !IsDrafting && HasSelection;

    /// <summary>The details card has nothing to show.</summary>
    public bool ShowsNoSelection => !IsDrafting && !HasSelection;

    partial void OnFilterIndexChanged(int value) => Rebuild();

    partial void OnSelectedAnnotationChanged(ChartAnnotation? oldValue, ChartAnnotation? newValue)
    {
        oldValue?.PropertyChanged -= OnSelectedPropertyChanged;
        newValue?.PropertyChanged += OnSelectedPropertyChanged;
        ReadSelected();
        OnPropertyChanged(nameof(ShowsSelection));
        OnPropertyChanged(nameof(ShowsNoSelection));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ToggleSelectedResolvedCommand.NotifyCanExecuteChanged();
        JumpToSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedKindIndexChanged(int value)
    {
        if (SelectedAnnotation is not { } annotation || IsReading) return;
        var kind = (ChartAnnotationKind)Math.Clamp(value, 0, 2);
        if (annotation.Kind != kind) _historyService.Do(new SetAnnotationKindAction(annotation, kind));
    }

    private bool IsReading { get; set; }

    private void ReadSelected()
    {
        IsReading = true;
        try
        {
            SelectedText = SelectedAnnotation?.Text ?? string.Empty;
            SelectedKindIndex = (int)(SelectedAnnotation?.Kind ?? ChartAnnotationKind.Note);
        }
        finally
        {
            IsReading = false;
        }

        OnPropertyChanged(nameof(SelectedIsResolved));
    }

    private void OnSelectedPropertyChanged(object? sender, PropertyChangedEventArgs e) => ReadSelected();

    // ── the list ────────────────────────────────────────────────────────────

    private void Watch()
    {
        var annotations = _projectState.ActiveProject?.Annotations;
        if (ReferenceEquals(annotations, _watched)) return;
        if (_watched is not null)
        {
            _watched.CollectionChanged -= OnAnnotationsChanged;
            foreach (var annotation in _watched) annotation.PropertyChanged -= OnAnnotationChanged;
        }

        _watched = annotations;
        if (_watched is not null)
        {
            _watched.CollectionChanged += OnAnnotationsChanged;
            foreach (var annotation in _watched) annotation.PropertyChanged += OnAnnotationChanged;
        }

        IsDrafting = false;
        NewAnnotationText = string.Empty;
        Rebuild();
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var annotation in e.OldItems?.OfType<ChartAnnotation>() ?? []) annotation.PropertyChanged -= OnAnnotationChanged;
        foreach (var annotation in e.NewItems?.OfType<ChartAnnotation>() ?? []) annotation.PropertyChanged += OnAnnotationChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && _watched is not null)
        {
            foreach (var annotation in _watched)
            {
                annotation.PropertyChanged -= OnAnnotationChanged;
                annotation.PropertyChanged += OnAnnotationChanged;
            }
        }

        Rebuild();
    }

    private void OnAnnotationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ChartAnnotation.Resolved)) return;
        if (FilterIndex != ShowAll) Rebuild();
        else RebuildResolved();
    }

    /// <summary>Re-reads the list through the filter; a selection the filter hides moves to the first visible row.</summary>
    private void Rebuild()
    {
        var shown = (_watched ?? []).Where(Passes).ToList();
        if (!VisibleAnnotations.SequenceEqual(shown))
        {
            var keep = SelectedAnnotation;
            VisibleAnnotations.Clear();
            foreach (var annotation in shown) VisibleAnnotations.Add(annotation);
            SelectedAnnotation = keep;
        }

        if (SelectedAnnotation is null || !shown.Contains(SelectedAnnotation)) SelectedAnnotation = shown.FirstOrDefault();
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(FilterHidesAll));
        RebuildResolved();
    }

    /// <summary>Re-reads the Resolved card's list; replaced only when it would read differently.</summary>
    private void RebuildResolved()
    {
        var resolved = (_watched ?? []).Where(a => a.Resolved).ToList();
        if (ResolvedAnnotations.SequenceEqual(resolved)) return;
        ResolvedAnnotations.Clear();
        foreach (var annotation in resolved) ResolvedAnnotations.Add(annotation);
        OnPropertyChanged(nameof(HasResolved));
    }

    private bool Passes(ChartAnnotation annotation) => FilterIndex switch
    {
        ShowOpen => !annotation.Resolved,
        ShowResolved => annotation.Resolved,
        _ => true
    };

    // ── the draft ───────────────────────────────────────────────────────────

    /// <summary>"Add at playhead": opens a draft at the playhead in the details card.</summary>
    [RelayCommand]
    private void StartDraft()
    {
        _draftTimeMs = _projectState.CurrentTimeMs;
        DraftHeading = _localization.Format("Notes_NewAt", ElapsedTimeLabel.Format(_draftTimeMs, 3));
        IsDrafting = true;
    }

    [RelayCommand]
    private void CancelDraft()
    {
        IsDrafting = false;
        NewAnnotationText = string.Empty;
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
            // the draft's time, the one its heading shows; with no draft open (Enter in a bare composer), the playhead's.
            TimeMs = IsDrafting ? _draftTimeMs : _projectState.CurrentTimeMs,
            Text = NewAnnotationText.Trim(),
            Kind = (ChartAnnotationKind)Math.Clamp(NewAnnotationKindIndex, 0, 2)
        };

        _historyService.Do(new AddAnnotationAction(project.Annotations, annotation));

        NewAnnotationText = string.Empty;
        IsDrafting = false;
        if (Passes(annotation)) SelectedAnnotation = annotation;
    }

    // ── the selected annotation ─────────────────────────────────────────────

    /// <summary>Writes the details' text (Enter or focus leaving the box); an empty text puts the old one back.</summary>
    [RelayCommand]
    public void CommitSelectedText()
    {
        if (SelectedAnnotation is not { } annotation) return;
        var text = SelectedText.Trim();
        if (text.Length == 0)
        {
            ReadSelected();
            return;
        }

        if (!string.Equals(text, annotation.Text, StringComparison.Ordinal))
            _historyService.Do(new SetAnnotationTextAction(annotation, text));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleSelectedResolved() => ToggleAnnotationResolved(SelectedAnnotation);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected() => RemoveAnnotation(SelectedAnnotation);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void JumpToSelected() => JumpToAnnotation(SelectedAnnotation);

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
    private void ToggleAnnotationResolved(ChartAnnotation? annotation)
    {
        if (annotation == null || _projectState.ActiveProject?.Annotations.Contains(annotation) != true) return;
        _historyService.Do(new SetAnnotationResolvedAction(annotation, !annotation.Resolved));
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
