using DotCharter.Localization;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Update;

/// <summary>What one update check told the user, and whether an update is now remembered.</summary>
public sealed record UpdateCheckOutcome(string StatusText, bool HasPendingUpdate);

/// <summary>
///     The app's "is there an update" verbs, shared by the Settings ▸ Updates
///     screen and the Help menu: a manual check, the re-open of a remembered
///     find, the non-modal startup check, and the recovery of a payload an
///     earlier run staged but never applied.
///     <para>
///         One owner for the "remembered update" rule: a check that comes back
///         UP TO DATE forgets the remembered update, because both doors into
///         the dialog re-open on its mere presence without checking again — so
///         leaving it set offered the user a download of the version they were
///         already running. A FAILED check leaves it alone: a network failure is
///         not evidence that the update went away.
///     </para>
/// </summary>
public sealed class UpdateCheckFlow
{
    /// <summary>AppContext switch the test host sets so booting the app never fires a network call.</summary>
    public const string DisableStartupCheckSwitch = "DotCharter.DisableStartupUpdateCheck";

    private readonly IAppUpdateService _service;
    private readonly IUpdatePrompt _prompt;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _ui;
    private readonly ProjectState _projectState;
    private readonly IApplicationCloser? _closer;

    public UpdateCheckFlow(
        IAppUpdateService service,
        IUpdatePrompt prompt,
        IConfigurationService config,
        ILocalizationService localization,
        IAppLogger logger,
        IUiDispatcher ui,
        ProjectState projectState,
        IApplicationCloser? closer = null)
    {
        _service = service;
        _prompt = prompt;
        _config = config;
        _localization = localization;
        _logger = logger;
        _ui = ui;
        _projectState = projectState;
        _closer = closer;
    }

    public string CurrentVersionText => _service.CurrentVersionText;

    public bool HasPendingUpdate => _prompt.PendingUpdate is not null;

    /// <summary>Checks now. Opens the prompt on a find; forgets a stale find on up-to-date.</summary>
    public async Task<UpdateCheckOutcome> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        var result = await _service.CheckForUpdateAsync(cancellationToken);
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Update != null:
                _prompt.Open(result.Update);
                return new UpdateCheckOutcome(_localization.Format("Update_Available", result.Update.TagName), true);
            case UpdateCheckStatus.UpToDate:
                _prompt.ClearPending();
                return new UpdateCheckOutcome(_localization["Update_UpToDate"], false);
            case UpdateCheckStatus.SignInRequired:
                return new UpdateCheckOutcome(_localization["Update_SignInRequired"], HasPendingUpdate);
            default:
                return new UpdateCheckOutcome(
                    _localization.Format("Update_CheckFailed", result.Error ?? "?"), HasPendingUpdate);
        }
    }

    /// <summary>Re-opens the remembered find. False when there is none.</summary>
    public bool ShowPendingUpdate()
    {
        if (_prompt.PendingUpdate is not { } update) return false;
        _prompt.Open(update);
        return true;
    }

    /// <summary>
    ///     The Help-menu door: re-open what the startup check found, else check
    ///     now and report the answer on the status line — the menu has no other
    ///     place to say "you are up to date".
    /// </summary>
    public async Task CheckOrShowAsync(CancellationToken cancellationToken = default)
    {
        if (ShowPendingUpdate()) return;
        var outcome = await CheckNowAsync(cancellationToken);
        if (!outcome.HasPendingUpdate) _projectState.SetMessage(outcome.StatusText);
    }

    /// <summary>A payload staged by an earlier run that never got copied, or null.</summary>
    public PendingUpdateApply? ProbePendingApply()
    {
        try
        {
            return _service.GetPendingUpdateApply();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, "Could not probe for a staged update.", ex);
            return null;
        }
    }

    /// <summary>Finishes the staged apply: the updater is relaunched against this process, then the app closes.</summary>
    /// <returns>Null when the app is closing, otherwise the localized failure to show.</returns>
    public string? FinishPendingApply()
    {
        if (_closer is null)
        {
            // Relaunching the updater without being able to close the app would
            // leave it copying over a running process; refuse before applying.
            _logger.Error(AppLogCategory.Application,
                "Interrupted update: no application closer is wired, so the staged payload was left alone.");
            return _localization.Format("Update_UpdaterLaunchFailed", "no application closer");
        }

        return UpdateApplyLauncher.ApplyAndCloseOrReport(
            _service.ResumePendingUpdateApply, _closer.Close, _localization, _logger, "Interrupted update");
    }

    /// <summary>Throws the staged payload away; the updater transcript is kept for diagnosis.</summary>
    public void DiscardPendingApply()
    {
        try
        {
            _service.DiscardPendingUpdateApply();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, "Discarding the staged update failed.", ex);
        }
    }

    /// <summary>
    ///     The non-modal startup check, fire-and-forget: after a short grace
    ///     period it surfaces a staged-but-unapplied payload, then — if the
    ///     setting allows — checks GitHub and PRIMES the prompt rather than
    ///     opening it. Disabled by <see cref="DisableStartupCheckSwitch" />.
    /// </summary>
    public void StartStartupCheck()
    {
        if (AppContext.TryGetSwitch(DisableStartupCheckSwitch, out var disabled) && disabled) return;
        _ = RunStartupCheckAsync();
    }

    private async Task RunStartupCheckAsync()
    {
        try
        {
            // Give startup (audio init, project restore) room to breathe.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Before the network check and regardless of the auto-check setting:
            // a payload staged by an earlier run is an unfinished job on this
            // disk, not a "check for updates" question.
            if (_service.GetPendingUpdateApply() != null)
            {
                _ui.Post(() => _projectState.SetMessage(
                    _localization["Update_InterruptedStatus"], StatusSeverity.Warning));
            }

            if (!_config.LoadAppConfig().UpdateCheckOnStartup) return;

            var result = await _service.CheckForUpdateAsync().ConfigureAwait(false);
            if (result.Status == UpdateCheckStatus.SignInRequired)
            {
                // A private feed without a sign-in must not look like "up to date".
                _ui.Post(() => _projectState.SetMessage(
                    _localization["Update_SignInRequired"], StatusSeverity.Warning));
                return;
            }

            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update == null) return;

            var update = result.Update;
            _ui.Post(() =>
            {
                _prompt.Prime(update);
                _projectState.SetMessage(_localization.Format("Update_AvailableStatus", update.TagName));
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, "Startup update check failed.", ex);
        }
    }
}
