using System.Diagnostics;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     After a Guitar Pro import that added arrangements: finds what can be linked (the score's own
///     repeats first, then repeated content) and, when anything can, asks with
///     <see cref="ILinkSuggestionsScreen" />. Link all is one undo step; Review opens the
///     linked-measures manager with the suggestions listed; Skip links nothing.
/// </summary>
public sealed class GpLinkSuggestionOffer(IDialogs dialogs, GpImportApplier applier, ChartEditDialogServices edit)
{
    /// <summary>Returns the number of groups Link all created.</summary>
    public async Task<int> OfferAsync()
    {
        if (applier.TakeLastImport() is not { } score || edit.State.ActiveProject is not { } project) return 0;

        var clock = Stopwatch.StartNew();
        var rule = LinkSuggestionFinder.RuleFor(edit.Config.LoadAppConfig());
        var suggestions = LinkSuggestionFinder.Find(project, score.Arrangements, score.SourceBars ?? [], rule,
            CancellationToken.None, message => edit.Logger.Warning(AppLogCategory.Import, message));
        edit.Logger.Info(AppLogCategory.Import,
            $"Link suggestions after the Guitar Pro import: {suggestions.Count} groups ({suggestions.Count(s => s.FromRepeats)} from repeats), " +
            $"repeatTier={score.SourceBars != null}, {clock.ElapsedMilliseconds} ms.");
        if (suggestions.Count == 0) return 0;

        switch (await dialogs.ShowAsync(new LinkSuggestionsRequest(suggestions)))
        {
            case LinkSuggestionsChoice.LinkAll:
                return ReferenceEquals(edit.State.ActiveProject, project) ? Link(edit, project, suggestions) : 0;
            case LinkSuggestionsChoice.Review:
                await dialogs.ShowAsync(new LinkGroupsRequest(suggestions));
                return 0;
            default:
                edit.Logger.Info(AppLogCategory.Import, "Link suggestions skipped.");
                return 0;
        }
    }

    /// <summary>Links <paramref name="suggestions" /> as one undo step and says so on the status line.</summary>
    internal static int Link(ChartEditDialogServices edit, ChartProject project, IEnumerable<LinkSuggestion> suggestions)
    {
        var rule = LinkSuggestionFinder.RuleFor(edit.Config.LoadAppConfig());
        var before = project.LinkGroups.Count;
        var (action, skipped) = LinkSuggestionFinder.LinkAll(project, [.. suggestions], rule);
        foreach (var skip in skipped)
        {
            edit.Logger.Info(AppLogCategory.Editing,
                $"Link all skipped \"{skip.Suggestion.Name}\" at measure {skip.Mismatch.Measure + 1}: {skip.Mismatch.Kind} " +
                $"{skip.Mismatch.Difference?.Kind} {skip.Mismatch.Difference?.Reference} -> {skip.Mismatch.Difference?.Other} {skip.Mismatch.Refusal}.");
        }

        var skippedText = skipped.Count == 0
            ? ""
            : " " + edit.Localization.Plural("Status_LinkSuggestionsSkippedOne", "Status_LinkSuggestionsSkipped", skipped.Count);
        if (action == null || !edit.History.Do(action))
        {
            edit.State.SetMessage(edit.Localization["Status_LinkSuggestionsNone"] + skippedText, StatusSeverity.Warning);
            return 0;
        }

        var linked = project.LinkGroups.Count - before;
        edit.State.SetMessage(edit.Localization.Plural("Status_LinkSuggestionsLinkedOne", "Status_LinkSuggestionsLinked", linked) + skippedText,
            skipped.Count == 0 ? StatusSeverity.Success : StatusSeverity.Warning);
        edit.Logger.Info(AppLogCategory.Editing, $"Linked {linked} suggested groups as one undo step; skipped {skipped.Count}.");
        return linked;
    }
}
