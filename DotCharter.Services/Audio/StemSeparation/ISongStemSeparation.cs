namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>How a <see cref="ISongStemSeparation.SeparateAsync" /> run ended.</summary>
public enum SongStemSeparationStatus
{
    /// <summary>The model ran; the stems are on disk, in the durable cache and on the project.</summary>
    Separated,

    /// <summary>The durable cache already held this audio's stems: they were loaded, nothing was separated.</summary>
    LoadedFromCache,

    /// <summary>The user cancelled the run or the model download.</summary>
    Cancelled,

    /// <summary>The user said no to a prompt on the way (the model download, or overwriting stems).</summary>
    Declined,

    /// <summary>The run started and failed; <see cref="SongStemSeparationOutcome.Reason" /> says why.</summary>
    Failed,

    /// <summary>Nothing could run (no model, no audio); <see cref="SongStemSeparationOutcome.Reason" /> says why.</summary>
    Unavailable,

    /// <summary>Another separation was already running; this request did nothing.</summary>
    AlreadyRunning
}

/// <summary>What a separation request came to, with the user-facing reason when it did not separate.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Reason">Localized, for <see cref="SongStemSeparationStatus.Failed" /> and
/// <see cref="SongStemSeparationStatus.Unavailable" />; empty otherwise.</param>
public sealed record SongStemSeparationOutcome(SongStemSeparationStatus Status, string Reason = "")
{
    /// <summary>Stems for the song are now on disk.</summary>
    public bool ProducedStems => Status is SongStemSeparationStatus.Separated or SongStemSeparationStatus.LoadedFromCache;
}

/// <summary>Whether a separation could start now, and the localized reason when it could not.</summary>
/// <param name="CanSeparate">True when <see cref="ISongStemSeparation.SeparateAsync" /> would run.</param>
/// <param name="Reason">Empty when <paramref name="CanSeparate" />; otherwise what stands in the way.</param>
public sealed record SongStemSeparationAvailability(bool CanSeparate, string Reason)
{
    /// <summary>A separation can start.</summary>
    public static SongStemSeparationAvailability Ready { get; } = new(true, "");

    /// <summary>It cannot, because of <paramref name="reason" />.</summary>
    public static SongStemSeparationAvailability Blocked(string reason) => new(false, reason);
}

/// <summary>
///     The app's ONE stem-separation verb for the open song — Tools ▸ Separate Stems —
/// as a seam a screen can drive. The implementation is the shell's
///     <c>StemSeparationViewModel</c>: cache first (stems are never regenerated), then
///     ONNX, then demucs.cpp, saved to the <c>.dcstems</c> cache after. There is no
///     second separation path; a screen that wants stems calls this.
/// </summary>
public interface ISongStemSeparation
{
    /// <summary>
    ///     Whether <see cref="SeparateAsync" /> could separate <paramref name="audioPath" />
    ///     now: it must be the open song's own audio, and a model must be installed or
    ///     offered for download. Reads state only — never separates, downloads or prompts.
    /// </summary>
    Task<SongStemSeparationAvailability> GetAvailabilityAsync(string audioPath, CancellationToken ct = default);

    /// <summary>
    ///     Separates the open song's audio exactly as Tools ▸ Separate Stems does, with the
    ///     same prompts and the same status-bar task. <paramref name="progress" /> also
    ///     receives the run's 0–1 progress, and <paramref name="ct" /> cancels it alongside
    ///     the status bar's own cancel. Never throws for a cancel or a failed run: the
    ///     outcome says so.
    /// </summary>
    Task<SongStemSeparationOutcome> SeparateAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>
    ///     The separation in flight, whoever started it, or null. Published once the run
    ///     knows its audio and cleared when it ends, so a screen opened mid-run follows
    /// this one rather than asking for a second.
    /// </summary>
    SongStemSeparationRun? Running { get; }
}
