using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
///     The Phrases panel, an inspector tab: every phrase of the active arrangement from both
///     phrase stores, each with its iterations in time order. A phrase line renames in place and
///     deletes; an iteration line jumps the editor there and copies its content into every other
///     iteration of the phrase. Every edit is one undo step.
/// </summary>
public interface IPhrasesScreen : INotifyPropertyChanged
{
    IReadOnlyList<IPhraseGroupRow> Phrases { get; }

    bool HasPhrases { get; }

    /// <summary>What the last action did, or what the armed Propagate is about to rewrite.</summary>
    string StatusLine { get; }

    /// <summary>What an export adds that the arrangement does not carry (COUNT, END); empty when nothing.</summary>
    string ExportNote { get; }

    /// <summary>Re-reads the arrangement.</summary>
    ICommand RefreshCommand { get; }

    /// <summary>Renames the phrase of the row passed as the parameter to its edited name.</summary>
    ICommand RenamePhraseCommand { get; }

    /// <summary>Puts the row's edited name back to the phrase's name.</summary>
    ICommand CancelRenameCommand { get; }

    /// <summary>Deletes every iteration of the row's phrase.</summary>
    ICommand DeletePhraseCommand { get; }

    /// <summary>Moves the editor to the iteration passed as the parameter.</summary>
    ICommand JumpToCommand { get; }

    /// <summary>
    ///     Copies the iteration passed as the parameter into every other iteration of its phrase;
    ///     the first press arms it, the second rewrites.
    /// </summary>
    ICommand PropagateCommand { get; }
}

/// <summary>One phrase: its name with the inline rename, and its iterations.</summary>
public interface IPhraseGroupRow : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>"2 iterations".</summary>
    string CountLabel { get; }

    IReadOnlyList<IPhraseIterationRow> Iterations { get; }

    /// <summary>What Delete removes, in words: the button's tooltip and accessible name.</summary>
    string DeleteTip { get; }

    /// <summary>The rename box's text.</summary>
    string EditName { get; set; }

    /// <summary>The rename box differs from the name: Apply shows.</summary>
    bool IsRenameDirty { get; }

    /// <summary>The iterations show; the chevron folds them away.</summary>
    bool IsExpanded { get; set; }
}

/// <summary>One iteration of a phrase.</summary>
public interface IPhraseIterationRow : INotifyPropertyChanged
{
    /// <summary>"#2 · bar 5 · 0:20.0": its place in the phrase, its bar and its time.</summary>
    string Label { get; }

    /// <summary>The phrase has other iterations to copy this one into.</summary>
    bool CanPropagate { get; }

    /// <summary>Armed by the first Propagate press; the second rewrites.</summary>
    bool IsConfirming { get; }
}
