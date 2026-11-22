using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>File ▸ New project: the dialog, then — once it has closed, so the native folder picker
///     is never under the overlay — <see cref="NewProjectCreator" /> on its answer.</summary>
public sealed class NewProjectLauncher(
    IDialogs dialogs,
    NewProjectCreator creator,
    ProjectState? state = null,
    ReferencePitchOffer? referencePitchOffer = null)
{
    public async Task OpenAsync()
    {
        if (await dialogs.ShowAsync<INewProjectScreen, NewProjectDetails?>() is not { } details) return;
        var before = state?.ActiveProject;
        await creator.CreateAsync(details);
        // The audio arrived with the project: the same A440 offer as File > Attach audio.
        var created = state != null && !string.IsNullOrEmpty(details.AudioFilePath) && !ReferenceEquals(before, state.ActiveProject);
        if (created && referencePitchOffer != null) await referencePitchOffer.OfferAsync();
    }
}
