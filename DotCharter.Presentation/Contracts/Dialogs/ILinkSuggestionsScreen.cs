using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The answer to the import's link summary. <see cref="Skip" /> is the zero value: a dialog
///     closed from outside links nothing.
/// </summary>
public enum LinkSuggestionsChoice
{
    Skip,
    LinkAll,
    Review
}

/// <summary>Opens the summary on what an import's detection found.</summary>
public sealed record LinkSuggestionsRequest(IReadOnlyList<LinkSuggestion> Suggestions)
    : IDialogRequest<LinkSuggestionsRequest, ILinkSuggestionsScreen, LinkSuggestionsChoice>;

/// <summary>
///     After a Guitar Pro import: what repeats and what the parts share, in a few lines, with
///     Link all (one undo step), Review (the linked-measures manager with each suggestion
///     listed) and Skip.
/// </summary>
public interface ILinkSuggestionsScreen : IDialogScreen<LinkSuggestionsChoice>
{
    /// <summary>"6 groups to link, 18 placements."</summary>
    string Headline { get; }

    /// <summary>"Rhythm and Lead share 48 of 60 measures.", "Chorus repeats 3× in each."</summary>
    IReadOnlyList<string> Details { get; }

    ICommand LinkAllCommand { get; }

    ICommand ReviewCommand { get; }

    ICommand SkipCommand { get; }
}
