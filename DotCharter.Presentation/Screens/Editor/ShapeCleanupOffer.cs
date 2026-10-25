using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>After an import: opens Clean up shapes only when it would change something; a tidy chart gets nothing.</summary>
public sealed class ShapeCleanupOffer(IDialogs dialogs, ChartEditDialogServices edit)
{
    public async Task<bool> OfferAsync(string source)
    {
        if (edit.Editor.ActiveArrangement is not { } arrangement) return false;

        var found = ShapeCleanupFinder.Find(arrangement);
        edit.Logger.Info(AppLogCategory.Import,
            $"Shape clean-up after the {source}: {found.Merges.Count} duplicate groups, {found.Unused.Count} unused, {found.NearDuplicates.Count} to review.");
        if (found.IsTidy) return false;

        await dialogs.ShowAsync<IShapeCleanupScreen, bool>();
        return true;
    }
}
