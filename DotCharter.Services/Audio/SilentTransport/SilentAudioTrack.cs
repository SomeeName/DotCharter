namespace DotCharter.Services.Audio.SilentTransport;

/// <summary>
/// Provides a cached, silent WAV of a project's length so an audio-less chart
/// (e.g. an RS1 chart-only import whose audio lives in a game archive we don't
/// have) can still drive the transport: playhead, scrubbing, count-in and Play
/// Mode all work when this is fed through the normal audio-load path.
///
/// <para>
/// The caller loads the returned file into the engine WITHOUT setting the song's
/// audio identity (<c>CurrentAudioFileName</c>) — so stem separation and other
/// audio-dependent features stay disabled, exactly as for a truly audio-less
/// chart. The pure length-derivation + fallback decision live here so they are
/// unit-testable; the file itself is cached under the app cache root, one file
/// per required length, regenerated when a project needs a longer one.
/// </para>
/// </summary>
public sealed class SilentAudioTrack
{
    /// <summary>Floor so even a very short chart still has a scrubable transport.</summary>
    public const double MinLengthMs = 15_000;

    /// <summary>A little runout past the last note.</summary>
    public const double TailMs = 2_000;

    /// <summary>Round the length up to this grid so small chart edits reuse the cache.</summary>
    public const double BucketMs = 15_000;

    /// <summary>Silence: the rate is irrelevant to correctness, so keep the file small.</summary>
    private const int SampleRateHz = 22_050;

    private readonly IAppLogger? _logger;
    private readonly string _cacheDir;
    private readonly object _gate = new();

    public SilentAudioTrack(IAppLogger? logger = null, string? cacheDirectoryOverride = null)
    {
        _logger = logger;
        _cacheDir = !string.IsNullOrWhiteSpace(cacheDirectoryOverride)
            ? cacheDirectoryOverride
            : Path.Combine(
                UserDataFolders.LocalAppData,
                "DotCharter", "SilentAudioCache");
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>
    /// The content end of a project in milliseconds: the later of the last beat
    /// and the last note/chord onset. Mirrors the editor's own project-length
    /// derivation. Pure.
    /// </summary>
    public static double DeriveProjectLengthMs(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var beatEnd = project.Beats.Count > 0 ? project.Beats[^1].Time : 0;
        var arrangementEnd = project.Arrangements
            .SelectMany(arrangement => arrangement.Levels)
            .SelectMany(level => level.Notes.Select(note => note.Position.GetPositionMs(project.Beats))
                .Concat(level.Chords.Select(chord => chord.Position.GetPositionMs(project.Beats))))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(beatEnd, arrangementEnd);
    }

    /// <summary>
    /// The silent-transport length to generate for a given project content end:
    /// content + tail, floored to <see cref="MinLengthMs"/> and rounded up to the
    /// <see cref="BucketMs"/> grid. Pure.
    /// </summary>
    public static double PlanLengthMs(double projectEndMs)
    {
        var wanted = Math.Max(projectEndMs, 0) + TailMs;
        wanted = Math.Max(wanted, MinLengthMs);
        return Math.Ceiling(wanted / BucketMs) * BucketMs;
    }

    /// <summary>
    /// Whether an audio-less chart should get a silent transport: only when the
    /// project has actual content (so a truly empty / no-project state keeps the
    /// free-engine behaviour) and no real audio resolved. Pure.
    /// </summary>
    public static bool ShouldUseFallback(bool hasResolvableAudio, double projectEndMs)
        => !hasResolvableAudio && projectEndMs > 0;

    /// <summary>
    /// Whether a silent transport already in the engine has been OUTGROWN by the
    /// chart it is standing in for. Pure.
    /// <para>
    /// The length is derived from the project's content once, at load — and a
    /// chart-only project is exactly the kind that gets extended afterwards
    /// (paste a section past the end, generate more beats, import a longer
    /// arrangement into the same session). Past the silent file's end there is
    /// no transport at all: the playhead stops, the seek clamps, and Play Mode
    /// cannot reach the notes the editor is drawing. The bucket grid is what
    /// keeps this from thrashing — the length only ever grows in
    /// <see cref="BucketMs"/> steps, so ordinary editing inside the current
    /// bucket answers false.
    /// </para>
    /// </summary>
    /// <param name="loadedLengthMs">The length of the silent transport currently
    /// loaded, or 0 when the engine is not on one (real audio, or nothing).</param>
    /// <param name="projectEndMs">The project's content end now
    /// (<see cref="DeriveProjectLengthMs"/>).</param>
    public static bool NeedsLongerTrack(double loadedLengthMs, double projectEndMs)
        => loadedLengthMs > 0 && PlanLengthMs(projectEndMs) > loadedLengthMs;

    /// <summary>
    /// Returns a cached silent WAV whose length is at least
    /// <paramref name="lengthMs"/> (planned via <see cref="PlanLengthMs"/>),
    /// generating it if necessary. Thread-safe.
    /// </summary>
    public string GetOrCreateSilentWav(double lengthMs)
    {
        var planned = PlanLengthMs(lengthMs);
        var seconds = (int)Math.Round(planned / 1000.0);
        var path = Path.Combine(_cacheDir, $"silence-{seconds}s.wav");

        lock (_gate)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 44)
                return path;

            Directory.CreateDirectory(_cacheDir);
            var tmp = path + ".partial";
            try
            {
                WriteSilentWav(tmp, seconds);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _logger?.Info(AppLogCategory.Audio,
                    $"Generated {seconds}s silent transport WAV '{path}' ({new FileInfo(path).Length / 1024} KB).");
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }
            return path;
        }
    }

    private static void WriteSilentWav(string path, int seconds)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        var totalSamples = (long)SampleRateHz * seconds;
        var dataBytes = checked((uint)(totalSamples * channels * (bitsPerSample / 8)));
        var byteRate = (uint)(SampleRateHz * channels * (bitsPerSample / 8));

        using var fs = File.Create(path, 1 << 20, FileOptions.SequentialScan);
        using (var w = new BinaryWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16u);
            w.Write((ushort)1); // PCM
            w.Write((ushort)channels);
            w.Write((uint)SampleRateHz);
            w.Write(byteRate);
            w.Write((ushort)(channels * (bitsPerSample / 8)));
            w.Write((ushort)bitsPerSample);
            w.Write("data"u8);
            w.Write(dataBytes);
        }

        // Stream the zeroed PCM in chunks so a long clip never allocates a big buffer.
        var chunk = new byte[1 << 16];
        var remaining = (long)dataBytes;
        while (remaining > 0)
        {
            var n = (int)Math.Min(chunk.Length, remaining);
            fs.Write(chunk, 0, n);
            remaining -= n;
        }
    }
}
