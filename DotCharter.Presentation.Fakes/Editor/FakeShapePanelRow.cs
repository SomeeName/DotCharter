using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>What a fake row reports wrong with itself, and the caption of its one fix.</summary>
internal sealed record FakeShapeIssue(string Text, string Fix);

/// <summary>One fake Shapes row: the name box and the No name switch really edit, the merge verbs ask the panel.</summary>
public sealed class FakeShapePanelRow : ObservableObject, IShapePanelRow
{
    private bool _canMerge;
    private bool _canMergeHere;
    private bool _isMergeSource;
    private bool _isMergeTarget;
    private bool _isNoName;
    private bool _isSelected;
    private string _nameText;

    internal FakeShapePanelRow(FakeShape shape, int chords, int handShapes, FakeShapeIssue? issue, FakeShapePanelScreen panel)
    {
        _nameText = shape.Name;
        _isNoName = shape.Name.Length == 0;
        Title = FakeShapeGrips.Title(shape);
        FretsText = FakeShapeGrips.FretsText(shape);
        NamePlaceholder = FretsText;
        Diagram = shape.ToTemplate();
        DiagramChord = shape.ToChord();
        StringCount = shape.Frets.Length;
        Uses = chords + handShapes;
        IsUnused = Uses == 0;
        UsesText = IsUnused ? "Unused"
            : handShapes == 0 ? $"{chords} chords" : $"{chords} chords · {handShapes} hand shapes";
        IssueText = issue?.Text ?? "";
        FixLabel = issue?.Fix ?? "";
        CommitNameCommand = new RelayCommand(() => { });
        FixCommand = new RelayCommand(() => { });
        EditCommand = new RelayCommand(() => { });
        PlaceCommand = new RelayCommand(() => { });
        SelectUsesCommand = new RelayCommand(() => { });
        NextUseCommand = new RelayCommand(() => { });
        SplitSelectionCommand = new RelayCommand(() => { });
        BeginMergeCommand = new RelayCommand(() => panel.BeginMerge(this));
        MergeHereCommand = new RelayCommand(() => panel.MergeInto(this));
        BeginDragCommand = new RelayCommand(() => panel.BeginMerge(this));
        DragOverCommand = new RelayCommand<bool>(_ => { });
        DropCommand = new RelayCommand(() => panel.MergeInto(this));
        EndDragCommand = new RelayCommand(() => { });
        DeleteCommand = new RelayCommand(() => panel.Delete(this));
        Chords = chords;
    }

    internal int Chords { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) SelectionChanged?.Invoke();
        }
    }

    /// <summary>Tells the panel to restate its bulk bar.</summary>
    internal event Action? SelectionChanged;

    /// <summary>The name or the fret pattern, for the panel's sentences.</summary>
    internal string Title { get; }

    internal int Uses { get; }

    public string NameText
    {
        get => _nameText;
        set => SetProperty(ref _nameText, value);
    }

    public string NamePlaceholder { get; }

    public ICommand CommitNameCommand { get; }

    public bool CanTick => true;

    public bool CanEdit => true;

    public bool CanMerge
    {
        get => _canMerge;
        internal set => SetProperty(ref _canMerge, value);
    }

    public bool CanMergeHere
    {
        get => _canMergeHere;
        internal set => SetProperty(ref _canMergeHere, value);
    }

    public bool IsNoName
    {
        get => _isNoName;
        set
        {
            if (!SetProperty(ref _isNoName, value)) return;
            if (value) NameText = "";
        }
    }

    public string FretsText { get; }

    public ChordTemplate Diagram { get; }

    public Chord DiagramChord { get; }

    public int StringCount { get; }

    public string UsesText { get; }

    public bool IsUnused { get; }

    public bool HasIssue => IssueText.Length > 0;

    public string IssueText { get; }

    public string FixLabel { get; }

    public ICommand FixCommand { get; }

    public bool IsMergeSource
    {
        get => _isMergeSource;
        internal set => SetProperty(ref _isMergeSource, value);
    }

    public bool IsMergeTarget
    {
        get => _isMergeTarget;
        internal set => SetProperty(ref _isMergeTarget, value);
    }

    public ICommand EditCommand { get; }

    public ICommand PlaceCommand { get; }

    public ICommand SelectUsesCommand { get; }

    public ICommand NextUseCommand { get; }

    public bool CanSplitSelection { get; internal set; }

    public ICommand SplitSelectionCommand { get; }

    public ICommand BeginMergeCommand { get; }

    public ICommand MergeHereCommand { get; }

    public ICommand BeginDragCommand { get; }

    public ICommand DragOverCommand { get; }

    public ICommand DropCommand { get; }

    public ICommand EndDragCommand { get; }

    public ICommand DeleteCommand { get; }
}
