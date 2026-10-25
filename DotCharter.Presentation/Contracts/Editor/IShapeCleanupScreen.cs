using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
///     Clean up shapes: the arrangement's shape list tidied in one pass, offered from the Shapes
///     panel and after an import. Three groups, each a list of proposals that start ticked or
///     undecided:
///     <list type="bullet">
///         <item>true duplicates — the same frets, fingers and flags — merged into the shape that keeps the name;</item>
///         <item>unused shapes, removed;</item>
///         <item>near-duplicates — the same frets with other fingers, or another name — shown side by side for a decision.</item>
///     </list>
///     Apply carries out every ticked proposal as ONE undo step.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when it applied.
/// </summary>
public interface IShapeCleanupScreen : IDialogScreen<bool>
{
    /// <summary>"3 duplicate groups · 4 unused · 2 to review".</summary>
    string Summary { get; }

    /// <summary>True when there is nothing to clean up.</summary>
    bool IsTidy { get; }

    /// <summary>"Duplicates (3)".</summary>
    string DuplicatesHeader { get; }

    IReadOnlyList<IShapeCleanupMerge> Duplicates { get; }

    bool HasDuplicates { get; }

    /// <summary>"Unused (4)".</summary>
    string UnusedHeader { get; }

    IReadOnlyList<IShapeCleanupRemoval> Unused { get; }

    bool HasUnused { get; }

    /// <summary>"Review (2)".</summary>
    string NearDuplicatesHeader { get; }

    IReadOnlyList<IShapeNearDuplicate> NearDuplicates { get; }

    bool HasNearDuplicates { get; }

    /// <summary>"Apply 9 changes"; counts only what is ticked or decided.</summary>
    string ApplyLabel { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>A group of identical shapes merged into the one that keeps the name.</summary>
public interface IShapeCleanupMerge : INotifyPropertyChanged
{
    /// <summary>"Am ×3".</summary>
    string Title { get; }

    /// <summary>"Keeps “Am”; 22 chords and 4 hand shapes move onto it".</summary>
    string Detail { get; }

    ChordTemplate Diagram { get; }

    Chord DiagramChord { get; }

    int StringCount { get; }

    bool IsIncluded { get; set; }
}

/// <summary>An unused shape that Apply removes.</summary>
public interface IShapeCleanupRemoval : INotifyPropertyChanged
{
    /// <summary>The name, or the fret pattern when the shape has none.</summary>
    string Name { get; }

    ChordTemplate Diagram { get; }

    Chord DiagramChord { get; }

    int StringCount { get; }

    bool IsIncluded { get; set; }
}

/// <summary>Two shapes that are nearly the same, shown side by side.</summary>
public interface IShapeNearDuplicate : INotifyPropertyChanged
{
    /// <summary>"Same frets, other fingers on the D and G strings".</summary>
    string DifferenceText { get; }

    /// <summary>"Am · 22 uses".</summary>
    string FirstLabel { get; }

    ChordTemplate FirstDiagram { get; }

    Chord FirstChord { get; }

    /// <summary>"Am · 2 uses".</summary>
    string SecondLabel { get; }

    ChordTemplate SecondDiagram { get; }

    Chord SecondChord { get; }

    int StringCount { get; }

    /// <summary>Keep both, keep the first, keep the second; exactly one is selected, Keep both at first.</summary>
    IReadOnlyList<IShapeCleanupChoice> Choices { get; }
}

/// <summary>One answer to a near-duplicate.</summary>
public interface IShapeCleanupChoice : INotifyPropertyChanged
{
    /// <summary>"Keep both", "Use the left one", "Use the right one".</summary>
    string Label { get; }

    bool IsSelected { get; }

    ICommand SelectCommand { get; }
}
