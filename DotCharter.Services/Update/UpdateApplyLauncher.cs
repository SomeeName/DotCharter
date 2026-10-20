using DotCharter.Localization;

namespace DotCharter.Services.Update;

/// <summary>
///     The one place that turns "apply a staged update" into "and therefore close
///     the app".
///     <para>
///         <b>Why it is a shared helper and not two inline try/catches.</b> The
///         ordering is the whole safety property: the app may only close once the
///         updater process is confirmed STARTED. Closing first — or closing in a
///         <c>finally</c>, or after swallowing the launch exception — produces the
///         shipped bug exactly: the app exits, nothing copies the payload,
///         nothing relaunches, and the user is left to copy files by hand. The
///         property has to hold at every call site (the download dialog and the
///         finish-interrupted button), so the ordering lives here and is
///         unit-tested with a closer that records whether it ran.
///     </para>
/// </summary>
public static class UpdateApplyLauncher
{
    /// <summary>
    ///     Runs <paramref name="apply" /> and, ONLY if it returned without
    ///     throwing, calls <paramref name="closeApp" />.
    /// </summary>
    /// <returns>Null when the updater started (the app is closing), otherwise the
    /// localized message to show the user — the session stays alive.</returns>
    public static string? ApplyAndCloseOrReport(
        Action apply,
        Action closeApp,
        ILocalizationService localization,
        IAppLogger logger,
        string context)
    {
        try
        {
            apply();
        }
        catch (UpdaterLaunchException ex)
        {
            // The payload is staged and valid; only the launch failed. Say so
            // plainly and leave the staging tree alone — the next start offers to
            // finish it.
            logger.Error(AppLogCategory.Application, $"{context}: the updater did not start.", ex);
            return localization.Format("Update_UpdaterLaunchFailed", ex.Message);
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Application, $"{context}: applying the update failed.", ex);
            return localization.Format("Update_DownloadFailed", ex.Message);
        }

        closeApp();
        return null;
    }
}
