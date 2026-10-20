using System.Globalization;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     Injectable "bake the import audio" step for the new-project flow.
///     Mirrors the wizard's metadata-extractor delegate so tests can substitute
///     a fake without the ffmpeg CLI.
/// </summary>
public delegate ImportAudioResult PrepareImportAudio(
    string sourcePath, string projectDir, double leadingSeconds, double trailingSeconds);

/// <summary>
///     Outcome of preparing a project's audio on creation.
/// </summary>
/// <param name="WorkingAudioFileName">
///     The project's working/playback audio file name inside the project folder —
///     <c>song.ogg</c> when the leading/trailing silence was physically baked in,
///     otherwise the verbatim <c>song.&lt;ext&gt;</c> copy of the source.
/// </param>
/// <param name="OriginalFileName">
///     The byte-exact source preserved beside the working audio in its source
///     format (EOF's <c>original.&lt;ext&gt;</c> convention) — non-null only when
///     silence was baked, since the fallback's working audio already IS the
///     verbatim source.
/// </param>
/// <param name="SilenceBaked">
///     True when the silence was physically encoded into <see cref="WorkingAudioFileName" />.
///     False for the verbatim-copy fallback (no ffmpeg / encode failed / no silence
///     requested), where the caller keeps the old behaviour of shifting only the beat grid.
/// </param>
public sealed record ImportAudioResult(
    string WorkingAudioFileName, string? OriginalFileName, bool SilenceBaked);

/// <summary>
///     Produces a new project's working audio from a picked source file. When
///     leading/trailing silence is requested and ffmpeg is available, it bakes the
///     silence into a <c>song.ogg</c> (Vorbis) working copy and preserves the
///     byte-exact source as <c>original.&lt;ext&gt;</c> — the EOF-exact model. When
///     ffmpeg is missing or the encode fails (or no silence is requested), it falls
///     back to a verbatim <c>song.&lt;ext&gt;</c> copy and reports
///     <see cref="ImportAudioResult.SilenceBaked" /> false so the caller keeps
///     today's timeline-shift-only behaviour.
/// </summary>
/// <remarks>
///     The pure decision helpers (<see cref="BuildSilenceFilter" />,
///     <see cref="BuildEncodeArgs" />) are separated from the process invocation so
///     they can be unit-tested without the CLI; <see cref="Prepare" /> takes an
///     injectable ffmpeg resolver and encode runner for the same reason.
/// </remarks>
public static class ImportAudioPreparer
{
    /// <summary>The working/playback audio file name when silence is baked in.
    /// Vorbis-in-ogg matches the EOF companion audio the save path already emits.</summary>
    public const string WorkingOggFileName = "song.ogg";

    /// <summary>A full song re-encode, so bounded generously (matches the EOF
    /// companion transcode). Running on the create path, never a hot path.</summary>
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Builds the ffmpeg <c>-af</c> filter graph that prepends
    ///     <paramref name="leadingSeconds" /> and appends
    ///     <paramref name="trailingSeconds" /> of silence, or null when neither
    ///     side needs padding (⇒ the caller does a verbatim copy, no encode).
    ///     Pure — no process, no I/O.
    /// </summary>
    /// <remarks>
    ///     Leading silence uses <c>adelay=&lt;ms&gt;:all=1</c>: <c>all=1</c> applies
    ///     the delay to every channel regardless of count, so it is correct for mono
    ///     as well as stereo (a per-channel <c>adelay=ms|ms</c> would drop or
    ///     mis-map channels on a mono source). Trailing silence uses
    ///     <c>apad=pad_dur=&lt;seconds&gt;</c>.
    /// </remarks>
    public static string? BuildSilenceFilter(double leadingSeconds, double trailingSeconds)
    {
        var parts = new List<string>(2);

        if (leadingSeconds > 0)
        {
            var leadMs = (long)Math.Round(leadingSeconds * 1000.0, MidpointRounding.AwayFromZero);
            if (leadMs > 0)
                parts.Add($"adelay={leadMs.ToString(CultureInfo.InvariantCulture)}:all=1");
        }

        if (trailingSeconds > 0)
            parts.Add($"apad=pad_dur={trailingSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    /// <summary>
    ///     The full ffmpeg argument vector that bakes <paramref name="filter" />
    ///     into a Vorbis-in-ogg working copy at <paramref name="targetOggPath" />.
    ///     Pure. <c>-q:a 6</c> is a transparent-enough VBR for a charting reference
    ///     track; <c>-y</c> overwrites (the project folder is fresh).
    /// </summary>
    public static IReadOnlyList<string> BuildEncodeArgs(
        string sourcePath, string targetOggPath, string filter)
        =>
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", sourcePath,
            .. FfmpegAudioOnly.Args,
            "-af", filter,
            "-c:a", "libvorbis", "-q:a", "6",
            targetOggPath
        ];

    /// <summary>
    ///     Prepares the project's working audio. See the type summary for the
    ///     bake-vs-fallback contract.
    /// </summary>
    /// <param name="sourcePath">The picked source audio file.</param>
    /// <param name="projectDir">The (already created) project folder to write into.</param>
    /// <param name="leadingSeconds">Leading silence to bake, in seconds (≤0 ⇒ none).</param>
    /// <param name="trailingSeconds">Trailing silence to bake, in seconds (≤0 ⇒ none).</param>
    /// <param name="configuredFfmpegPath">An explicit ffmpeg path (config), or null to auto-resolve.</param>
    /// <param name="resolveFfmpeg">Overrides ffmpeg resolution (tests); defaults to <see cref="MediaToolResolver" />.</param>
    /// <param name="runEncode">Overrides the encode process (tests): (ffmpegPath, args) ⇒ succeeded. Defaults to <see cref="MediaProcess" />.</param>
    /// <param name="logWarning">Optional sink for fallback/degradation messages.</param>
    public static ImportAudioResult Prepare(
        string sourcePath,
        string projectDir,
        double leadingSeconds,
        double trailingSeconds,
        string? configuredFfmpegPath = null,
        Func<string?>? resolveFfmpeg = null,
        Func<string, IReadOnlyList<string>, bool>? runEncode = null,
        Action<string>? logWarning = null)
    {
        var ext = Path.GetExtension(sourcePath);
        var fallbackWorkingName = "song" + ext;

        ImportAudioResult Fallback(string? warning)
        {
            if (warning is not null) logWarning?.Invoke(warning);
            File.Copy(sourcePath, Path.Combine(projectDir, fallbackWorkingName), overwrite: true);
            return new ImportAudioResult(fallbackWorkingName, OriginalFileName: null, SilenceBaked: false);
        }

        // No silence requested (padding off, or both amounts zero) ⇒ verbatim copy.
        var filter = BuildSilenceFilter(leadingSeconds, trailingSeconds);
        if (filter is null) return Fallback(warning: null);

        var ffmpeg = (resolveFfmpeg ?? (() => MediaToolResolver.Resolve(
            "ffmpeg", AppContext.BaseDirectory, configuredFfmpegPath,
            MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows())))();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return Fallback(
                "Silence bake skipped: ffmpeg was not found. Copied audio verbatim and " +
                "shifted only the beat grid (install ffmpeg via Settings -> Components to bake silence).");
        }

        var target = Path.Combine(projectDir, WorkingOggFileName);
        var args = BuildEncodeArgs(sourcePath, target, filter);

        bool ok;
        try
        {
            ok = (runEncode ?? DefaultRunEncode)(ffmpeg, args);
        }
        catch (Exception ex)
        {
            ok = false;
            logWarning?.Invoke($"Silence bake failed to start ffmpeg: {ex.Message}");
        }

        if (!ok || !File.Exists(target) || new FileInfo(target).Length == 0)
        {
            // best-effort: a stray partial ogg is harmless next to the verbatim copy
            BestEffortDelete.File(target);
            return Fallback(
                "Silence bake failed; copied audio verbatim and shifted only the beat grid.");
        }

        // Baked: keep the byte-exact source beside the padded working audio.
        var originalName = "original" + ext;
        try
        {
            File.Copy(sourcePath, Path.Combine(projectDir, originalName), overwrite: true);
        }
        catch (Exception ex)
        {
            // The original is a convenience preserve — a copy failure must not
            // undo a good bake. Keep the baked working audio; report no original.
            logWarning?.Invoke($"Baked silence but could not preserve the original source: {ex.Message}");
            originalName = null;
        }

        return new ImportAudioResult(WorkingOggFileName, originalName, SilenceBaked: true);
    }

    private static bool DefaultRunEncode(string ffmpeg, IReadOnlyList<string> args)
        => MediaProcess.Run(ffmpeg, args, EncodeTimeout).Succeeded;
}
