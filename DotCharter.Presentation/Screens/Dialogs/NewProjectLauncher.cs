using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>File ▸ New project: the dialog, then — once it has closed, so the native folder picker
///     is never under the overlay — <see cref="NewProjectCreator" /> on its answer.</summary>
public sealed class NewProjectLauncher(IDialogs dialogs, NewProjectCreator creator)
{
    public async Task OpenAsync()
    {
        if (await dialogs.ShowAsync<INewProjectScreen, NewProjectDetails?>() is { } details)
            await creator.CreateAsync(details);
    }
}
