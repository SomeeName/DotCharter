using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Services.Update;

/// <summary>
///     What was staged for an in-place update, written beside the install as
///     <c>dotcharter-update-pending.json</c> immediately BEFORE the updater
///     script is launched and deleted by that script on its success path only.
///     <para>
///         It exists so an update that did not finish is recoverable rather than
///         a manual file-copy job. Three ways it can be left behind, and all
///         three must not be silent: the script process failed to start at all,
///         the script gave up waiting for the app to exit, or robocopy failed.
///         In each case the payload is still on disk and still valid — what is
///         missing is somebody to notice.
///     </para>
///     <para>
///         The <see cref="Deletions" /> list is carried here because
///         <c>PayloadDeletions.TakeFrom</c> REMOVES <c>payload-deletions.txt</c>
///         from the staged payload (deliberately — it must never be copied into
///         the install), so a resume that only had the staging tree could not
///         reconstruct it, and every file the new version dropped would survive
///         the no-purge copy forever.
///     </para>
/// </summary>
public sealed record UpdateApplyState
{
    /// <summary>Bumped when the shape changes; an unreadable or newer state is
    /// treated as "no pending update" rather than guessed at.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Directory whose contents get copied over the install.</summary>
    public string PayloadDir { get; init; } = string.Empty;

    /// <summary>Staging root to delete once the copy succeeded.</summary>
    public string StagingRoot { get; init; } = string.Empty;

    /// <summary>Downloaded archive to delete once the copy succeeded.</summary>
    public string ZipPath { get; init; } = string.Empty;

    /// <summary>Install-relative files the new version dropped.</summary>
    public IReadOnlyList<string> Deletions { get; init; } = [];

    /// <summary>When the payload was staged (UTC).</summary>
    public DateTimeOffset StagedUtc { get; init; }

    /// <summary>Version the payload upgrades TO, for display. Best effort.</summary>
    public string TargetVersion { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses a state file. Returns null for anything unreadable,
    /// empty, from a newer schema, or missing the payload directory — a
    /// half-understood state must never drive a copy over the install.</summary>
    public static UpdateApplyState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<UpdateApplyState>(json, Options);
            if (state == null) return null;
            if (state.SchemaVersion is < 1 or > CurrentSchemaVersion) return null;
            if (string.IsNullOrWhiteSpace(state.PayloadDir)) return null;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     A staged update the app found on disk at start-up, with everything the UI
///     needs to offer "finish" or "discard" without re-reading the payload.
/// </summary>
/// <param name="State">The staged payload's own description of itself.</param>
/// <param name="CanResume">
///     True when the payload is still on disk and still passes the same
///     <c>AppPayload.ValidateDirectory</c> check the original apply ran. False
///     means the only honest offer is to discard.
/// </param>
/// <param name="Problem">Why <paramref name="CanResume" /> is false, or null.</param>
/// <param name="LogPath">The updater transcript, if one was written.</param>
/// <param name="LogTail">Last few lines of that transcript, for the UI.</param>
public sealed record PendingUpdateApply(
    UpdateApplyState State,
    bool CanResume,
    string? Problem,
    string? LogPath,
    string LogTail);
