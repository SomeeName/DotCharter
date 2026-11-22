using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// The Editor on Fire round trip on the Inspector's Issues tab: one group per kind of loss, each naming its places;
///     the first few of each are listed, and Show all lists every one.
/// </summary>
public interface IEofRoundTripSection : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>
    ///     Open. The round trip is computed — off the UI thread — the first time the section opens,
    ///     and again after each recheck while it is open; nothing is computed while it is closed.
    /// </summary>
    bool IsExpanded { get; set; }

    /// <summary>The round trip is being computed; the groups below are empty while it is.</summary>
    bool IsBusy { get; }

    /// <summary>"Nothing is lost", "2 dropped · 9 approximated", or why it could not be checked.</summary>
    string Summary { get; }

    bool HasGroups { get; }

    IReadOnlyList<IEofLossGroup> Groups { get; }

    /// <summary>List every place instead of the first few of each kind.</summary>
    bool ShowAll { get; set; }

    /// <summary>A group lists fewer places than it has, so Show all has something to do.</summary>
    bool CanShowAll { get; }

    /// <summary>Opens every place, of every kind, as plain text in the text-document dialog, which copies it.</summary>
    ICommand OpenListCommand { get; }
}

/// <summary>Every place one kind of loss happened.</summary>
public interface IEofLossGroup
{
    /// <summary>What was lost: "Tone never used by a tone change".</summary>
    string Title { get; }

    /// <summary>"Dropped (2)" or "Approximated (9)".</summary>
    string Count { get; }

    /// <summary>Left out of the file, not merely approximated.</summary>
    bool IsDropped { get; }

    IReadOnlyList<IEofLossPlace> Items { get; }

    /// <summary>"4 more"; empty when every place is listed.</summary>
    string MoreNote { get; }

    bool HasMore { get; }
}

/// <summary>One place: where it happened, and the value that names the item.</summary>
public interface IEofLossPlace
{
    /// <summary>The arrangement, or "Beat map" / "Vocals"; it trims before <see cref="Where" /> does.</summary>
    string Subject { get; }

    /// <summary>"M12.3 · 00:41.25"; empty for a loss that is about the whole arrangement.</summary>
    string Where { get; }

    /// <summary>The phrase, tone, lyric or value: "clean", "1.3 → 1.25"; may be empty.</summary>
    string Detail { get; }

    bool HasTime { get; }

    /// <summary>Seeks the editor behind the dialog, switching arrangement first.</summary>
    ICommand JumpCommand { get; }
}
