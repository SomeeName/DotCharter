using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;
using System.Text;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// <see cref="IStemEncoder"/> backed by ffmpeg. When ffmpeg is on PATH (or in
/// the app's <c>tools/</c> folder) stems are stored as Opus — lossy but the best
/// size/quality trade-off, exactly as requested. When ffmpeg is absent the
/// encoder degrades cleanly to WAV (a plain file copy), and the codec actually
/// used is recorded in the cache manifest so the archive stays portable.
/// </summary>
/// <remarks>
/// Opus is used ONLY for the practice/mixer playback path. It is never fed to
/// live note detection (<c>LiveCqtVerifier</c> / <c>LiveNoteMatcher</c>), which
/// reads the lossless live input capture — so the lossy round-trip here does
/// not affect detection correctness.
/// </remarks>
public sealed class FfmpegStemEncoder : IStemEncoder
{
    /// <summary>Opus decodes at 48 kHz natively; see <see cref="DecodeToWavAsync"/>.</summary>
    public const string OpusNativeSampleRate = "48000";

    /// <summary>Bitrate for Opus stems. 160 kb/s stereo is transparent for
    /// practice playback while keeping a full stem set tiny. The <c>.dcsong</c>
    /// package asks for its own rate through the constructor
    /// (<c>DcSongFormat.OpusBitrateKbps</c>).</summary>
    public const int DefaultOpusBitrateKbps = 160;

    private readonly IAppLogger? _logger;
    private readonly Func<string?> _ffmpegResolver;
    // Injectable process runner: (exe, args) => success. Defaults to a real
    // ffmpeg process; tests substitute a fake.
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<bool>> _runner;
    private readonly Lazy<string?> _ffmpegPath;
    private readonly string _opusBitrate;

    /// <param name="logger">Optional log sink.</param>
    /// <param name="ffmpegResolver">Overrides tool resolution entirely (tests).</param>
    /// <param name="runner">Overrides the process runner (tests).</param>
    /// <param name="configuredFfmpegPath">
    /// Reads <c>AppConfig.FfmpegPath</c>. Threading it in is not cosmetic: this
    /// encoder writes the NEVER-REGENERATED stem cache, so the ffmpeg it picks
    /// decides that archive's codec for good.
    /// </param>
    /// <param name="opusBitrateKbps">The Opus bitrate in kb/s; the stem cache keeps the default.</param>
    public FfmpegStemEncoder(
        IAppLogger? logger = null,
        Func<string?>? ffmpegResolver = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<bool>>? runner = null,
        Func<string?>? configuredFfmpegPath = null,
        int opusBitrateKbps = DefaultOpusBitrateKbps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(opusBitrateKbps, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opusBitrateKbps, 510);
        _logger = logger;
        OpusBitrateKbps = opusBitrateKbps;
        _opusBitrate = opusBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
        _ffmpegResolver = ffmpegResolver ?? (() => ResolveFfmpeg(configuredFfmpegPath?.Invoke()));
        _runner = runner ?? RunProcessAsync;
        _ffmpegPath = new Lazy<string?>(() => _ffmpegResolver(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The Opus bitrate this encoder writes, in kb/s.</summary>
    public int OpusBitrateKbps { get; }

    public bool IsExternalEncoderAvailable => _ffmpegPath.Value != null;

    public StemCodec PreferredCodec => IsExternalEncoderAvailable ? StemCodec.Opus : StemCodec.Wav;

    public async Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec codec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            throw new FileNotFoundException("Stem WAV to encode was not found.", wavPath);

        // WAV requested, or no external encoder available: just copy the WAV in.
        if (codec == StemCodec.Wav || !IsExternalEncoderAvailable)
        {
            var wavDest = destPathWithoutExtension + ".wav";
            CopyReplace(wavPath, wavDest);
            return wavDest;
        }

        var ffmpeg = _ffmpegPath.Value!;
        var ext = codec.FileExtension();
        var dest = destPathWithoutExtension + "." + ext;
        // The audio stream only: a .dcsong's lossless mode hands this a user's own
        // FLAC/WAV, which may carry cover art as a picture stream.
        string[] args = codec switch
        {
            StemCodec.Opus =>
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", wavPath,
                .. FfmpegAudioOnly.Args,
                "-c:a", "libopus", "-b:a", _opusBitrate,
                dest,
            ],
            StemCodec.Flac =>
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", wavPath,
                .. FfmpegAudioOnly.Args,
                "-c:a", "flac",
                dest,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unhandled codec."),
        };

        var ok = await _runner(ffmpeg, args, ct).ConfigureAwait(false);
        if (ok && File.Exists(dest) && new FileInfo(dest).Length > 0)
            return dest;

        // Encoder ran but produced nothing usable — fall back to WAV so we
        // never lose the stem. The manifest will still say WAV because the
        // store records the codec of the returned path.
        _logger?.Warning(AppLogCategory.Audio,
            $"ffmpeg failed to encode stem to {codec}; storing WAV instead. Source='{wavPath}'.");
        var fallback = destPathWithoutExtension + ".wav";
        CopyReplace(wavPath, fallback);
        return fallback;
    }

    public async Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(encodedPath) || !File.Exists(encodedPath))
            throw new FileNotFoundException("Encoded stem to decode was not found.", encodedPath);

        if (codec == StemCodec.Wav)
        {
            // "No decoding needed" is still a file COPY, and a copy fails for
            // exactly the environmental reasons the Opus branch below is protected
            // against: a full disk, an ACL, or the destination WAV still open in the
            // JUCE mixer from the previous session. Left unwrapped these surfaced as
            // a bare IOException, which the cache coordinator's catch-all read as
            // corruption and answered by DELETING the never-regenerate archive. A
            // WAV .dcstems is precisely what a fresh install without ffmpeg
            // produces, so the no-ffmpeg configuration had no protection at all.
            try
            {
                CopyReplace(encodedPath, destWavPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new StemDecodeUnavailableException(
                    $"Cached stem '{encodedPath}' could not be copied to '{destWavPath}': {ex.Message}", ex);
            }

            return destWavPath;
        }

        // Environmental failures throw the typed exception so the cache
        // coordinator can distinguish "tool missing/failed" (keep the durable
        // archive) from genuine archive corruption (delete it).
        if (!IsExternalEncoderAvailable)
        {
            throw new StemDecodeUnavailableException(
                $"Cache stem is stored as {codec} but ffmpeg is not available to decode it. " +
                "Install ffmpeg (on PATH or in the app 'tools' folder) to play cached stems.");
        }

        var ffmpeg = _ffmpegPath.Value!;
        // Opus is 48 kHz by definition; the OpusHead only REMEMBERS the input rate
        // (44.1 kHz for a CD-rate song), and ffmpeg honours that memory by
        // downsampling the decode to 44.1 kHz. The engine then upsampled every
        // stem back to its 48 kHz device with the load-time windowed sinc — measured
        // at 6.1 s per 183 s stem versus 51 ms for a 48 kHz WAV, six
        // stems in a row. Asking for 48 kHz is not a resample: it is the decoder's
        // native output, and the engine's `willUpsample` is then false.
        var args = codec == StemCodec.Opus
            ? new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", encodedPath,
                "-ar", OpusNativeSampleRate,
                destWavPath,
            }
            :
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", encodedPath,
                destWavPath,
            ];
        var ok = await _runner(ffmpeg, args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(destWavPath) || new FileInfo(destWavPath).Length <= 44)
            throw new StemDecodeUnavailableException($"ffmpeg failed to decode cached stem '{encodedPath}'.");
        return destWavPath;
    }

    private static void CopyReplace(string src, string dest)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(src, dest, overwrite: true);
    }

    /// <summary>
    /// The ONE documented resolution order — configured path → exe dir → exe
    /// dir/<c>tools</c> → PATH → dev-tree <c>tools/redist/win-x64</c> — shared
    /// with every other ffmpeg consumer in the app.
    /// <para>
    /// This is deliberately not a hand-rolled fourth spelling, which diverges three ways,
    /// and each divergence has teeth. (1) It would ignore <c>AppConfig.FfmpegPath</c>
    /// entirely, so a user who pointed Settings at a specific ffmpeg would get their
    /// stems from a different one — or none. (2) It would loop NAMES outer /
    /// directories inner, so the whole of PATH would be searched for
    /// <c>ffmpeg.exe</c> before <c>tools/ffmpeg</c> was tried, inverting the
    /// documented "a bundled copy beats an ambient PATH one". (3) It would have no
    /// dev-tree fallback, so a <c>bin/Debug</c> run with no PATH ffmpeg silently
    /// writes the cache as WAV — and because the stem cache is NEVER
    /// regenerated, that song stays bloated WAV forever while every other
    /// ffmpeg consumer in the same process found the tool.
    /// </para>
    /// </summary>
    private static string? ResolveFfmpeg(string? configuredPath) => MediaToolResolver.Resolve(
        "ffmpeg", AppContext.BaseDirectory, configuredPath,
        MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows());

    /// <summary>
    /// Runs an ffmpeg invocation and reports whether it exited cleanly.
    /// <para>
    /// Three things here are load-bearing and each one was a real defect.
    /// (1) <b>Cancellation KILLS the child.</b> <c>using var proc</c> disposes only
    /// the managed wrapper, so a cancelled encode would leave ffmpeg running and
    /// still writing into the destination the caller had already abandoned —
    /// cancelling a 6-stem cache save could strand six orphans.
    /// (2) <b>The pipe drains use <see cref="CancellationToken.None"/>.</b> Draining
    /// with the caller's token cancelled the READS at the same instant, so a
    /// still-writing ffmpeg filled its stdout/stderr pipe and blocked forever with
    /// nobody left to reap it — the orphan was also unkillable-by-exit.
    /// (3) <b>The drains are awaited</b> (after exit, or after the kill) instead of
    /// being fire-and-forget, so their exceptions are observed rather than surfacing
    /// later as unobserved-task faults.
    /// </para>
    /// </summary>
    internal static async Task<bool> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        Process? proc = null;
        Task<string>? stderr = null;
        Task<string>? stdout = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return false;
            // Below-normal priority so a batch of stem encodes never fights the
            // UI or audio thread for CPU.
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not fatal */ }
            stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally
        {
            // Killing closes the pipes, so the drains always complete; awaiting
            // them here observes both outcomes before the wrapper is disposed.
            try { if (stderr != null) await stderr.ConfigureAwait(false); }
            catch { /* the child died mid-read; nothing to report */ }
            try { if (stdout != null) await stdout.ConfigureAwait(false); }
            catch { /* ditto */ }
            proc?.Dispose();
        }
    }
}
