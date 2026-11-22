using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Services.Editing.Tempo.RecordingAlignment;

/// <summary>What the alignment listened to: the instrument stems already on disk, or the full mix.</summary>
public enum RecordingAlignmentSource
{
    Mix,
    Stems
}

/// <summary>
/// The recording an align-to-recording run listens to. Declared project stems take precedence;
///     an empty list permits discovery beside the song. The pitched instruments — guitar, bass,
///     other, piano — are summed only when every stem matches the mix's duration, which
///     takes the drums and the voice out of the onset envelope; otherwise the mix. It never starts a
///     separation. Opus stems are accepted here as they are for lyric sync: an onset envelope is not
///     live note detection, and refusing them would make the stems path work only in the session that
///     separated.
/// </summary>
public static class RecordingAlignmentAudio
{
    private static readonly string[] InstrumentStems = ["guitar", "bass", "other", "piano"];

    private static readonly string[] Extensions = [".wav", ".flac", ".opus", ".ogg", ".mp3", ".m4a", ".aac"];

    /// <summary>Uses declared instrument roles and the mixer's path resolver. Only an empty
    /// project list permits the legacy folder discovery.</summary>
    public static IReadOnlyList<string> LocateInstrumentStems(string mixPath, IReadOnlyList<Stem> declared,
        string? projectPath, string? audioFileName, IAppLogger? logger = null)
    {
        if (declared.Count == 0) return LocateInstrumentStems(mixPath);
        var projectDir = Path.GetDirectoryName(projectPath ?? "") ?? "";
        var legacyDir = ProjectAudioLocator.ResolveOutputDirectory(projectPath, audioFileName) ?? "";
        var found = new List<string>();
        foreach (var stem in declared)
        {
            if (!InstrumentStems.Contains(stem.Name.Trim(), StringComparer.OrdinalIgnoreCase)) continue;
            var path = StemFolderProbe.ResolveStemPath(projectDir, legacyDir, stem.Path);
            if (!File.Exists(path)) continue;
            if (stem.Offset != 0)
            {
                logger?.Warning(AppLogCategory.Audio, StemTimeBase.DescribeOffset(path, stem.Offset));
                return [];
            }

            found.Add(path);
        }

        return found;
    }

    /// <summary>The instrument stems beside <paramref name="mixPath" />, best format per instrument; empty when none.</summary>
    public static IReadOnlyList<string> LocateInstrumentStems(string? mixPath)
    {
        if (string.IsNullOrWhiteSpace(mixPath)) return [];
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(mixPath));
            if (string.IsNullOrEmpty(directory)) return [];
            var stems = Path.Combine(directory, VocalStemLocator.StemsFolderName);
            return Directory.Exists(stems) ? PickInstrumentStems(Directory.EnumerateFiles(stems)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Pure: one file per instrument name (exact, case-insensitive), the earliest extension in
    ///     the preference list winning, in <see cref="InstrumentStems" /> order.</summary>
    public static IReadOnlyList<string> PickInstrumentStems(IEnumerable<string> files)
    {
        var best = new Dictionary<string, (string Path, int Rank)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();
            if (!InstrumentStems.Contains(name)) continue;
            var rank = Array.FindIndex(Extensions, e => string.Equals(e, Path.GetExtension(file), StringComparison.OrdinalIgnoreCase));
            if (rank < 0) continue;
            if (!best.TryGetValue(name, out var current) || rank < current.Rank) best[name] = (file, rank);
        }

        return InstrumentStems.Where(best.ContainsKey).Select(n => best[n].Path).ToArray();
    }

    /// <summary>Decodes the stems summed (or the mix) to mono floats; falls back to the mix if a stem will not decode.</summary>
    internal static (float[] Mono, int SampleRate, RecordingAlignmentSource Source) Load(string mixPath, CancellationToken ct,
        IReadOnlyList<string>? stemPaths = null, IAppLogger? logger = null,
        Func<string, PcmAudio>? loadAudio = null)
    {
        loadAudio ??= path => PcmAudioLoader.Load(path, []);
        ct.ThrowIfCancellationRequested();
        var (mix, mixRate) = Decode(mixPath, loadAudio);
        var stems = stemPaths ?? LocateInstrumentStems(mixPath);
        if (stems.Count > 0)
        {
            try
            {
                var (sum, rate) = SumStems(stems, ct, path =>
                {
                    var (mono, stemRate) = Decode(path, loadAudio);
                    var stemSeconds = (double)mono.Length / stemRate;
                    var mixSeconds = (double)mix.Length / mixRate;
                    if (!StemTimeBase.Matches(stemSeconds, mixSeconds))
                        throw new InvalidDataException(StemTimeBase.DescribeMismatch(path, stemSeconds, mixPath, mixSeconds));
                    return (mono, stemRate);
                });
                return (sum, rate, RecordingAlignmentSource.Stems);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.Warning(AppLogCategory.Audio, $"Align to recording: {ex.Message}", ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        return (mix, mixRate, RecordingAlignmentSource.Mix);
    }

    /// <summary>Decodes every stem to mono and sums them; throws when they disagree on the sample rate.</summary>
    internal static (float[] Mono, int SampleRate) SumStems(IReadOnlyList<string> stems, CancellationToken ct,
        Func<string, (float[] Mono, int SampleRate)>? decode = null)
    {
        decode ??= Decode;
        float[]? sum = null;
        var rate = 0;
        foreach (var stem in stems)
        {
            ct.ThrowIfCancellationRequested();
            var (mono, stemRate) = decode(stem);
            if (sum == null)
            {
                sum = mono;
                rate = stemRate;
                continue;
            }

            if (stemRate != rate) throw new InvalidDataException("Stems disagree on the sample rate.");
            if (mono.Length > sum.Length) Array.Resize(ref sum, mono.Length);
            for (var i = 0; i < mono.Length; i++) sum[i] += mono[i];
        }

        return sum == null ? throw new InvalidDataException("No stems.") : (sum, rate);
    }

    /// <summary>The full mix as mono floats; Estimate BPM listens to this, never to the stems.</summary>
    internal static (float[] Mono, int SampleRate) DecodeMix(string path) => Decode(path);

    private static (float[] Mono, int SampleRate) Decode(string path)
        => Decode(path, file => PcmAudioLoader.Load(file, []));

    private static (float[] Mono, int SampleRate) Decode(string path, Func<string, PcmAudio> loadAudio)
    {
        var pcm = loadAudio(path);
        var channels = Math.Max(1, pcm.Channels);
        var frames = pcm.Samples.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += pcm.Samples[f * channels + c];
            mono[f] = sum / (channels * 32768f);
        }

        return (mono, pcm.SampleRate);
    }
}
