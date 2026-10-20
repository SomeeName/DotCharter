using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Logging;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     <see cref="IUpdatePrompt" /> over <see cref="IDialogs" />: it REMEMBERS the
///     update the last check found (the dialog's view model lives only while it
///     is open) and opens <see cref="IUpdateAvailableScreen" /> for it. A second
///     open while one is showing is ignored.
/// </summary>
public sealed class UpdatePrompt(IDialogs dialogs, IAppLogger logger) : IUpdatePrompt
{
    private bool _showing;

    public UpdateInfo? PendingUpdate { get; private set; }

    public void Prime(UpdateInfo update) => PendingUpdate = update;

    public void Open(UpdateInfo update)
    {
        PendingUpdate = update;
        if (_showing) return;
        ShowAsync(update).Observe(logger, "update available");
    }

    public void ClearPending() => PendingUpdate = null;

    private async Task ShowAsync(UpdateInfo update)
    {
        _showing = true;
        try
        {
            await dialogs.ShowAsync(new UpdateAvailableRequest(update));
        }
        finally
        {
            _showing = false;
        }
    }
}
