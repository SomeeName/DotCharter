namespace DotCharter.Services.Update;

/// <summary>
/// Checks GitHub releases for a newer version, downloads the win-x64 zip
/// asset and stages + applies it in place via a detached updater script
/// (see <see cref="UpdaterScriptBuilder"/>). The caller is responsible for
/// exiting the application after <see cref="ApplyDownloadedUpdate"/>; the
/// updater waits for the process to exit before copying files.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Version of the running application (from the entry assembly).</summary>
    Version CurrentVersion { get; }

    /// <summary>Release identity including prerelease identifiers, without build metadata.</summary>
    string CurrentVersionText => UpdateVersionText.Format(CurrentVersion);

    /// <summary>
    /// Queries the latest release. Never throws for network / parse
    /// problems — those surface as <see cref="UpdateCheckStatus.Failed"/>;
    /// a private feed without a sign-in is <see cref="UpdateCheckStatus.SignInRequired"/>.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update zip to the writable updates directory, reporting
    /// progress in [0..1], and verifies the archive opens. Returns the path
    /// to the downloaded zip. Throws on failure (surfaced by the caller).
    /// </summary>
    Task<string> DownloadUpdateAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the official <c>SHA256SUMS.txt</c> for one release version, or
    /// null when it cannot be reached (offline, a tag that was never published,
    /// a release predating published digests). Never throws for network faults.
    /// <para>
    ///     A downloaded asset is extracted over the live install by a detached
    ///     script, so these digests are the only thing that can say the bytes are
    ///     the build they claim to be. Null is the degraded state — unverified but
    ///     applicable — not a failure: refusing there would break updating from
    ///     behind a captive portal and from every release older than 0.41.
    /// </para>
    /// </summary>
    Task<string?> TryFetchReleaseChecksumsAsync(Version version, CancellationToken cancellationToken = default);

    /// <summary>Whether an in-place apply is possible for the running install.</summary>
    UpdateApplyBlockReason GetApplyBlockReason();

    /// <summary>
    /// Extracts the zip to &lt;install&gt;\update-staging, writes the updater
    /// .cmd next to the exe and launches it detached. The updater waits for
    /// this process to exit, copies staging over the install dir, restarts
    /// DotCharter.exe and removes staging + the zip + itself. The caller
    /// must close the application right after this returns.
    /// <para>
    ///     <b>…and must NOT close it when this throws.</b> A
    ///     <see cref="UpdaterLaunchException" /> means the payload is staged but
    ///     nothing is going to copy it: exiting then is exactly the failure this
    ///     contract exists to prevent.
    /// </para>
    /// </summary>
    /// <exception cref="UpdaterLaunchException">The updater could not be started.</exception>
    void ApplyDownloadedUpdate(string zipPath);

    /// <summary>
    /// A payload staged by an earlier run that never got applied, or null.
    /// Cheap enough to call on the start-up path (a few file probes).
    /// </summary>
    PendingUpdateApply? GetPendingUpdateApply();

    /// <summary>
    /// Re-launches the updater for an already-staged payload, regenerating the
    /// script against the CURRENT process id. Same caller contract as
    /// <see cref="ApplyDownloadedUpdate" />: close the app on success, keep it
    /// alive on failure.
    /// </summary>
    void ResumePendingUpdateApply();

    /// <summary>Deletes the staged payload, the script and the record. The
    /// updater transcript is kept.</summary>
    void DiscardPendingUpdateApply();
}
