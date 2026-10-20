using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The startup crash check: the recovery prompt opens only when leftover
///     autosave snapshots prove the previous session died mid-edit.
/// </summary>
public sealed class RecoveryLauncher(ICrashRecoveryService crashRecovery, IDialogs dialogs, IAppLogger logger)
{
    public void OpenIfPending()
    {
        try
        {
            var sessions = crashRecovery.FindRecoverableSessions();
            if (sessions.Count == 0) return;
            dialogs.ShowAsync(new RecoveryRequest(sessions)).Observe(logger, "crash recovery");
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Project, "Recovery dialog startup check failed.", ex);
        }
    }
}
