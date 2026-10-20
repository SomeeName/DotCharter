using System.Text.Json;
using DotCharter.Services.Audio.StemSeparation.Cache;
using JuceSharp.AudioIo;

namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>The two derived stems' files.</summary>
public sealed record GuitarRolePair(string LeadPath, string RhythmPath);

/// <summary>What a derivation found, and the files when it produced them.</summary>
public sealed record GuitarRoleDerivation(GuitarRoleAssessment Assessment, GuitarRolePair? Pair);

/// <summary>The seam the stem mixer drives Guitar roles through; <see cref="GuitarRoleStemService" />
/// is the one implementation. Every member blocks on disk — call it from a worker.</summary>
public interface IGuitarRoleStems
{
    /// <inheritdoc cref="GuitarRoleStemService.Assess" />
    GuitarRoleAssessment Assess(string guitarStemPath, CancellationToken ct = default);

    /// <inheritdoc cref="GuitarRoleStemService.TryGetDerived" />
    GuitarRolePair? TryGetDerived(string guitarStemPath);

    /// <inheritdoc cref="GuitarRoleStemService.Derive" />
    GuitarRoleDerivation Derive(string guitarStemPath, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
///     Derives Lead (centre) and Rhythm (sides) from a separated GUITAR stem and
///     keeps them beside it.
///     <para>
///         <b>Never a second separation.</b> The input is the guitar stem exactly as
///         the cache delivered it, from whichever model produced it; the split is a
///         few seconds of offline DSP (<see cref="CentreSideSplitter" />). Its output
///         is a REGENERABLE artefact, like the waveform cache and unlike the
///         <c>.dcstems</c> archive: it is keyed by the guitar stem's content
///         signature (<see cref="StemAudioSignature" />) in a sidecar, reused while
///         that matches, and re-derived from the guitar stem — never from the song —
///         when it does not (a re-separation, a deleted folder).
///     </para>
///     <para>
///         <b>Honest.</b> A stem whose image cannot be split by position is refused
///         with its reason (<see cref="GuitarRoleAssessment" />) and no files are
///         written for it: the user is never handed the same guitar twice.
///     </para>
///     <para>
///         Playback only, like every stem: nothing under <c>Audio/LivePlay/</c> may
///         read these (linted by <c>LiveDetectionAudioSourceTests</c>).
///     </para>
/// </summary>
public sealed class GuitarRoleStemService(IAppLogger? logger = null) : IGuitarRoleStems
{
    /// <summary>Bumped when the split's output changes, so older files are re-derived.</summary>
    public const int AlgorithmVersion = 1;

    private const int BlockFrames = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _deriveGate = new();

    /// <summary>
    ///     What the stem's image allows, as cheaply as it can be known: the sidecar
    ///     when it describes THIS stem, otherwise one streaming pass of level and
    ///     correlation (no transform, nothing written). Never throws; an unreadable
    ///     stem is a verdict.
    /// </summary>
    public GuitarRoleAssessment Assess(string guitarStemPath, CancellationToken ct = default)
    {
        try
        {
            var signature = StemAudioSignature.ComputeFile(guitarStemPath);
            if (signature.IsEmpty) return Unreadable();
            if (ReadSidecar(guitarStemPath, signature) is { } known) return known.Assessment;
            return MeasureImage(guitarStemPath, ct).Assessment;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, $"Guitar roles: could not read '{guitarStemPath}': {ex.Message}");
            return Unreadable();
        }
    }

    /// <summary>
    ///     The derived pair for this guitar stem when it is on disk and current, else
    ///     null. Reads a sidecar, a sampled signature and two file sizes — cheap
    ///     enough for every mixer rebuild, and never on the UI thread.
    /// </summary>
    public GuitarRolePair? TryGetDerived(string guitarStemPath)
    {
        try
        {
            var signature = StemAudioSignature.ComputeFile(guitarStemPath);
            return signature.IsEmpty ? null : ReadSidecar(guitarStemPath, signature)?.Pair;
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, $"Guitar roles: could not check '{guitarStemPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Splits the guitar stem, or reuses a current split. Blocking — run it on a
    ///     worker. Writes the two WAVs and the sidecar only for a splittable image;
    ///     a refused image leaves just the sidecar saying why.
    /// </summary>
    public GuitarRoleDerivation Derive(string guitarStemPath, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        lock (_deriveGate)
        {
            var signature = StemAudioSignature.ComputeFile(guitarStemPath);
            if (signature.IsEmpty) return new GuitarRoleDerivation(Unreadable(), null);

            var known = ReadSidecar(guitarStemPath, signature);
            if (known is { Pair: not null } or { Assessment.Verdict: not GuitarRoleVerdict.Plausible })
                return known;

            try
            {
                var image = MeasureImage(guitarStemPath, ct);
                if (!image.Assessment.CanSplit)
                {
                    WriteSidecar(guitarStemPath, signature, image.Assessment, image.Channels, null);
                    return new GuitarRoleDerivation(image.Assessment, null);
                }

                return Split(guitarStemPath, signature, image.Assessment.Stats, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Error(AppLogCategory.Audio, $"Guitar roles: the split of '{guitarStemPath}' failed.", ex);
                return new GuitarRoleDerivation(Unreadable(), null);
            }
        }
    }

    private GuitarRoleDerivation Split(string guitarStemPath, StemAudioSignature signature, StereoImageStats stats,
        IProgress<double>? progress, CancellationToken ct)
    {
        var folder = GuitarRoleStems.FolderFor(guitarStemPath);
        Directory.CreateDirectory(folder);
        var prefix = signature.Sha256Hex[..Math.Min(12, signature.Sha256Hex.Length)];
        var leadPath = Path.Combine(folder, prefix + "-lead.wav");
        var rhythmPath = Path.Combine(folder, prefix + "-rhythm.wav");
        var leadTemp = leadPath + ".part";
        var rhythmTemp = rhythmPath + ".part";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double leadShare;
        int channels;
        try
        {
            using (var reader = StereoWavStreamReader.Open(guitarStemPath))
            {
                channels = reader.Channels;
                var format = reader.IsFloat ? WavSampleFormat.Float32
                    : reader.BitsPerSample == 16 ? WavSampleFormat.Pcm16
                    : WavSampleFormat.Pcm24;
                using var lead = new WavWriter(leadTemp, reader.SampleRate, 2, format);
                using var rhythm = new WavWriter(rhythmTemp, reader.SampleRate, 2, format);
                var splitter = new CentreSideSplitter();
                var inL = new float[BlockFrames];
                var inR = new float[BlockFrames];
                var outs = new[] { new float[BlockFrames], new float[BlockFrames], new float[BlockFrames], new float[BlockFrames] };
                var interleaved = new float[BlockFrames * 2];
                long done = 0;
                int n;
                while ((n = reader.Read(inL, inR)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    splitter.Push(inL.AsSpan(0, n), inR.AsSpan(0, n));
                    Drain(splitter, outs, interleaved, lead, rhythm);
                    done += n;
                    if (reader.Frames > 0) progress?.Report((double)done / reader.Frames);
                }

                splitter.Complete();
                Drain(splitter, outs, interleaved, lead, rhythm);
                leadShare = splitter.LeadShare;
            }

            var assessment = GuitarRoleAssessment.FromSplit(stats, channels, leadShare);
            logger?.Info(AppLogCategory.Audio,
                $"Guitar roles: '{Path.GetFileName(guitarStemPath)}' correlation {stats.Correlation:0.000}, " +
                $"side share {stats.SideShare:0.000}, lead share {leadShare:0.000} -> {assessment.Verdict} " +
                $"in {clock.Elapsed.TotalSeconds:0.0} s.");

            if (assessment.Verdict != GuitarRoleVerdict.Splittable)
            {
                TryDelete(leadTemp);
                TryDelete(rhythmTemp);
                WriteSidecar(guitarStemPath, signature, assessment, channels, null);
                return new GuitarRoleDerivation(assessment, null);
            }

            File.Move(leadTemp, leadPath, overwrite: true);
            File.Move(rhythmTemp, rhythmPath, overwrite: true);
            var pair = new GuitarRolePair(leadPath, rhythmPath);
            // The sidecar is written LAST: it is what says the pair is complete.
            WriteSidecar(guitarStemPath, signature, assessment, channels, pair);
            RemoveStaleSplits(folder, prefix);
            return new GuitarRoleDerivation(assessment, pair);
        }
        catch
        {
            TryDelete(leadTemp);
            TryDelete(rhythmTemp);
            throw;
        }
    }

    private static void Drain(CentreSideSplitter splitter, float[][] outs, float[] interleaved,
        WavWriter lead, WavWriter rhythm)
    {
        int n;
        while ((n = splitter.Read(outs[0], outs[1], outs[2], outs[3])) > 0)
        {
            Interleave(outs[0], outs[1], n, interleaved);
            lead.Write(interleaved.AsSpan(0, 2 * n));
            Interleave(outs[2], outs[3], n, interleaved);
            rhythm.Write(interleaved.AsSpan(0, 2 * n));
        }
    }

    private static void Interleave(float[] l, float[] r, int n, float[] into)
    {
        for (var i = 0; i < n; i++)
        {
            into[2 * i] = l[i];
            into[2 * i + 1] = r[i];
        }
    }

    private static (GuitarRoleAssessment Assessment, int Channels) MeasureImage(string path, CancellationToken ct)
    {
        using var reader = StereoWavStreamReader.Open(path);
        var meter = new StereoImageMeter();
        var l = new float[BlockFrames];
        var r = new float[BlockFrames];
        int n;
        while ((n = reader.Read(l, r)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            meter.Add(l.AsSpan(0, n), r.AsSpan(0, n));
        }

        return (GuitarRoleAssessment.FromImage(meter.Result(), reader.Channels), reader.Channels);
    }

    private static GuitarRoleAssessment Unreadable() =>
        new(GuitarRoleVerdict.Unreadable, default, null);

    private GuitarRoleDerivation? ReadSidecar(string guitarStemPath, StemAudioSignature signature)
    {
        var path = Path.Combine(GuitarRoleStems.FolderFor(guitarStemPath), GuitarRoleStems.SidecarName);
        if (!File.Exists(path)) return null;
        Sidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<Sidecar>(AtomicFile.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.Warning(AppLogCategory.Audio, $"Guitar roles: ignoring an unreadable '{path}': {ex.Message}");
            return null;
        }

        if (sidecar == null || sidecar.Version != AlgorithmVersion || sidecar.GuitarBytes != signature.Bytes ||
            !string.Equals(sidecar.GuitarHash, signature.Sha256Hex, StringComparison.Ordinal) ||
            !Enum.TryParse<GuitarRoleVerdict>(sidecar.Verdict, out var verdict))
        {
            return null;
        }

        var stats = new StereoImageStats(sidecar.Correlation, sidecar.SideShare, sidecar.RmsDbfs, sidecar.Frames);
        var assessment = new GuitarRoleAssessment(verdict, stats, sidecar.LeadShare);
        if (verdict != GuitarRoleVerdict.Splittable) return new GuitarRoleDerivation(assessment, null);

        var folder = Path.GetDirectoryName(path)!;
        var lead = Path.Combine(folder, sidecar.Lead ?? string.Empty);
        var rhythm = Path.Combine(folder, sidecar.Rhythm ?? string.Empty);
        if (!HasSize(lead, sidecar.LeadBytes) || !HasSize(rhythm, sidecar.RhythmBytes))
            return new GuitarRoleDerivation(assessment with { Verdict = GuitarRoleVerdict.Plausible }, null);
        return new GuitarRoleDerivation(assessment, new GuitarRolePair(lead, rhythm));
    }

    private static bool HasSize(string path, long bytes)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == bytes;
    }

    private static void WriteSidecar(string guitarStemPath, StemAudioSignature signature,
        GuitarRoleAssessment assessment, int channels, GuitarRolePair? pair)
    {
        var folder = GuitarRoleStems.FolderFor(guitarStemPath);
        Directory.CreateDirectory(folder);
        var sidecar = new Sidecar
        {
            Version = AlgorithmVersion,
            GuitarBytes = signature.Bytes,
            GuitarHash = signature.Sha256Hex,
            Verdict = assessment.Verdict.ToString(),
            Channels = channels,
            Correlation = assessment.Stats.Correlation,
            SideShare = assessment.Stats.SideShare,
            RmsDbfs = double.IsFinite(assessment.Stats.RmsDbfs) ? assessment.Stats.RmsDbfs : -999,
            Frames = assessment.Stats.Frames,
            LeadShare = assessment.LeadShare,
            Lead = pair == null ? null : Path.GetFileName(pair.LeadPath),
            Rhythm = pair == null ? null : Path.GetFileName(pair.RhythmPath),
            LeadBytes = pair == null ? 0 : new FileInfo(pair.LeadPath).Length,
            RhythmBytes = pair == null ? 0 : new FileInfo(pair.RhythmPath).Length,
        };
        AtomicFile.WriteAllText(Path.Combine(folder, GuitarRoleStems.SidecarName),
            JsonSerializer.Serialize(sidecar, JsonOptions));
    }

    /// <summary>Best effort: a split for an older guitar stem the engine may still hold
    /// open is left for the next derivation to remove.</summary>
    private static void RemoveStaleSplits(string folder, string keepPrefix)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.wav"))
        {
            if (Path.GetFileName(file).StartsWith(keepPrefix + "-", StringComparison.Ordinal)) continue;
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* held open; the next derivation retries */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    private sealed class Sidecar
    {
        public int Version { get; set; }
        public long GuitarBytes { get; set; }
        public string GuitarHash { get; set; } = string.Empty;
        public string Verdict { get; set; } = string.Empty;
        public int Channels { get; set; }
        public double Correlation { get; set; }
        public double SideShare { get; set; }
        public double RmsDbfs { get; set; }
        public long Frames { get; set; }
        public double? LeadShare { get; set; }
        public string? Lead { get; set; }
        public string? Rhythm { get; set; }
        public long LeadBytes { get; set; }
        public long RhythmBytes { get; set; }
    }
}
