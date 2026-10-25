using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>One duplicate group on the dialog: its tick decides whether Apply merges it.</summary>
internal sealed class ShapeCleanupMergeItem : ObservableObject, IShapeCleanupMerge
{
    private bool _isIncluded = true;

    public ShapeCleanupMergeItem(ShapeCleanupMerge source, string title, string detail, int stringCount)
    {
        Source = source;
        Title = title;
        Detail = detail;
        Diagram = source.Keeper.Clone();
        DiagramChord = ShapeEditViewModel.ChordOf(source.Keeper);
        StringCount = stringCount;
    }

    public ShapeCleanupMerge Source { get; }

    public string Title { get; }

    public string Detail { get; }

    public ChordTemplate Diagram { get; }

    public Chord DiagramChord { get; }

    public int StringCount { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

/// <summary>One unused shape on the dialog: ticked, it is removed.</summary>
internal sealed class ShapeCleanupRemovalItem : ObservableObject, IShapeCleanupRemoval
{
    private bool _isIncluded = true;

    public ShapeCleanupRemovalItem(ChordTemplate source, string name, int stringCount)
    {
        Source = source;
        Name = name;
        Diagram = source.Clone();
        DiagramChord = ShapeEditViewModel.ChordOf(source);
        StringCount = stringCount;
    }

    public ChordTemplate Source { get; }

    public string Name { get; }

    public ChordTemplate Diagram { get; }

    public Chord DiagramChord { get; }

    public int StringCount { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

/// <summary>One near-duplicate pair: the three answers select one at a time, Keep both first.</summary>
internal sealed class ShapeCleanupNearItem : ObservableObject, IShapeNearDuplicate
{
    private readonly List<ShapeCleanupChoiceItem> _choices;

    public ShapeCleanupNearItem(
        ShapeCleanupNearDuplicate source, string difference, string firstLabel, string secondLabel,
        IReadOnlyList<string> choiceLabels, int stringCount)
    {
        Source = source;
        DifferenceText = difference;
        FirstLabel = firstLabel;
        SecondLabel = secondLabel;
        FirstDiagram = source.First.Clone();
        FirstChord = ShapeEditViewModel.ChordOf(source.First);
        SecondDiagram = source.Second.Clone();
        SecondChord = ShapeEditViewModel.ChordOf(source.Second);
        StringCount = stringCount;
        _choices = [.. choiceLabels.Select((label, i) => new ShapeCleanupChoiceItem(label, () => Pick(i)))];
        _choices[0].IsSelected = true;
    }

    /// <summary>Raised when the answer changes, so the dialog recounts Apply.</summary>
    public event Action? Decided;

    public ShapeCleanupNearDuplicate Source { get; }

    public string DifferenceText { get; }

    public string FirstLabel { get; }

    public ChordTemplate FirstDiagram { get; }

    public Chord FirstChord { get; }

    public string SecondLabel { get; }

    public ChordTemplate SecondDiagram { get; }

    public Chord SecondChord { get; }

    public int StringCount { get; }

    public IReadOnlyList<IShapeCleanupChoice> Choices => _choices;

    /// <summary>The answer picked now; the choice order is Keep both, first, second.</summary>
    public ShapeCleanupChoice Choice => (ShapeCleanupChoice)_choices.FindIndex(c => c.IsSelected);

    internal void Pick(int index)
    {
        for (var i = 0; i < _choices.Count; i++) _choices[i].IsSelected = i == index;
        Decided?.Invoke();
    }
}

/// <summary>One answer to a pair.</summary>
internal sealed class ShapeCleanupChoiceItem : ObservableObject, IShapeCleanupChoice
{
    private bool _isSelected;

    public ShapeCleanupChoiceItem(string label, Action pick)
    {
        Label = label;
        SelectCommand = new RelayCommand(pick);
    }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}
