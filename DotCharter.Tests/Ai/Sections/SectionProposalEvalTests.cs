using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Sections;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Tests.Ai.Eval;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     <b>Gated by <c>DOTCHARTER_AUDIO_EVAL=1</c>; it skips otherwise.</b> The one
///     question a synthetic splice cannot answer: over a REAL record, with a REAL
///     separation, how many of the charter's own tone changes does the audio scan find?
///     <para>
///         The charter's <c>&lt;tone time=…&gt;</c> markers are the only ground truth
///         there is for "where does the tone change" — a human decided each one while
///         listening to the record. Recall against them, inside the same ±2 s window
///         the merge rule uses, is therefore the honest measure of the segmenter, and
///         it is reported rather than asserted: a threshold here would be a number
///         invented today and defended for ever.
///     </para>
///     <para>
///         It scans the head of ONE song (the first
///         <see cref="DefaultWindowSeconds" /> seconds, as a cached excerpt) because a
///         separation is minutes and the stem cache keys on file CONTENT — so a second
///         run of this costs no separation at all.
///     </para>
/// </summary>
public sealed class SectionProposalEvalTests
{
    /// <summary>Must be exactly <c>1</c>; anything else skips.</summary>
    public const string EnableVariable = "DOTCHARTER_AUDIO_EVAL";

    /// <summary>How much of the song to scan, in seconds.</summary>
    public const string SecondsVariable = "DOTCHARTER_AUDIO_EVAL_SECONDS";

    /// <summary>Long enough to contain several charted changes, short enough that the
    /// separation is a couple of minutes rather than ten.</summary>
    public const double DefaultWindowSeconds = 150.0;

    private static readonly TimeSpan Agreement = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AudioBoundariesAgainstTheChartsOwnToneChanges()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"{EnableVariable} is not 1 — this eval separates a real song's stems and takes " +
                        "minutes on the first run, so it is opt-in.");
        }

        var ct = TestContext.Current.CancellationToken;
        var root = ToneEvalCorpus.ResolveCorpusRoot();
        Assert.SkipWhen(root is null, ToneEvalCorpus.MissingCorpus);

        var window = double.TryParse(
            Environment.GetEnvironmentVariable(SecondsVariable), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : DefaultWindowSeconds;

        var song = FindSong(root!, window);
        Assert.SkipWhen(song is null, "no corpus song has two charted tone changes in its first " +
                                      $"{window:0} s with playable audio beside it.");

        var models = new StemModelManager(new EvalConfiguration());
        var separation = new CompositeStemSeparationService(
            [new OnnxStemSeparationService(models, null), new JuceStemSeparationService(null, models)],
            null, new InFlightStemSeparationTracker());
        Assert.SkipUnless(await separation.IsAvailableAsync(ct) || CacheIsWarm(),
            "no stem-separation backend is available and the cache is cold: " +
            await separation.DescribeBackendAsync(ct));

        var repoRoot = TestPaths.RepoRoot();
        var excerpt = EnsureExcerpt(
            song!.AudioPath, Path.Combine(repoRoot, "artifacts", "audio-eval", "excerpts"), window);

        var proposer = new ToneSectionProposer(
            new StemIsolator(separation, new StemCacheStore(), FakeLocalization.English()), new ToneSimilarity(),
            FakeLocalization.English());

        var proposals = await proposer.ProposeAsync(
            excerpt, ToneStems.Guitar, [], null, ct);

        // Every boundary the audio proposed, excluding the synthesised opener at 0.
        var found = proposals.Skip(1).Select(p => p.Section.From).ToList();

        // Chart boundaries the scan could possibly have found: inside the excerpt and
        // not in the first or last window, where there is no second window to compare
        // against. Scoring the ones it cannot reach would be scoring arithmetic.
        var reachable = song.Boundaries
            .Where(b => b > ToneSection.MinDuration.TotalSeconds &&
                        b < window - ToneSection.MinDuration.TotalSeconds)
            .ToList();
        var hits = reachable.Count(b => found.Any(f => Math.Abs(f.TotalSeconds - b) <= Agreement.TotalSeconds));

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"song            : {song.Title}");
        report.AppendLine(CultureInfo.InvariantCulture, $"arrangement     : {song.Arrangement}");
        report.AppendLine(CultureInfo.InvariantCulture, $"window          : {window:0} s");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"chart changes   : {song.Boundaries.Count} total, {reachable.Count} reachable");
        report.AppendLine(CultureInfo.InvariantCulture, $"audio boundaries: {found.Count}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"recall (+-{Agreement.TotalSeconds:0} s) : {hits}/{reachable.Count}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"chart times     : {string.Join(", ", reachable.Select(b => b.ToString("0.0", CultureInfo.InvariantCulture)))}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"audio times     : {string.Join(", ", found.Select(f => f.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)))}");
        foreach (var p in proposals)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {p.Section.From.TotalSeconds,6:0.0}-{p.Section.To.TotalSeconds,6:0.0}  " +
                $"{p.Origin,-11} {p.Confidence:0.00}  {p.Section.Label}");
        }

        var outputRoot = Path.Combine(repoRoot, "artifacts", "section-eval");
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot,
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        await File.WriteAllTextAsync(path, report.ToString(), ct);

        // The measurement is the point; the only assertion is that the run produced a
        // segmentation at all. A recall threshold here would be a number invented
        // today and defended for ever.
        Assert.NotEmpty(proposals);
        Assert.True(reachable.Count > 0, report.ToString());
    }

    private sealed record CorpusSong(string Title, string AudioPath, string Arrangement, IReadOnlyList<double> Boundaries);

    /// <summary>
    ///     The first corpus folder with playable audio and at least two charted tone
    ///     changes inside the window. The filename filters are the ones
    ///     <c>AudioEvalCorpus</c> already uses: showlight, debug and tone2014 files are
    ///     not arrangements.
    /// </summary>
    private static CorpusSong? FindSong(string root, double window)
    {
        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var audio = FindAudio(folder);
            if (audio is null) continue;

            foreach (var file in Directory.EnumerateFiles(folder, "*.xml")
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("showlight", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains(".debug.", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("tone2014", StringComparison.OrdinalIgnoreCase)) continue;

                XDocument document;
                try { document = XDocument.Load(file); }
                catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { continue; }

                var times = document.Root?.Element("tones")?.Elements("tone")
                    .Select(e => double.TryParse(e.Attribute("time")?.Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var t) ? t : -1)
                    .Where(t => t >= 0 && t <= window)
                    .OrderBy(t => t)
                    .ToList() ?? [];

                if (times.Count < 2) continue;

                return new CorpusSong(
                    document.Root?.Element("title")?.Value ?? Path.GetFileName(folder), audio, name, times);
            }
        }

        return null;
    }

    private static string? FindAudio(string folder)
    {
        var wav = Directory.EnumerateFiles(folder, "*.wav")
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (wav is not null) return wav.FullName;

        return Directory.EnumerateFiles(folder, "*.ogg")
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Writes (once) the head of the song, channels preserved. The stem cache
    /// keys on CONTENT, so the excerpt gets its own durable entry and a second run
    /// separates nothing.</summary>
    private static string EnsureExcerpt(string songAudioPath, string directory, double seconds)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            $"sections-{Path.GetFileNameWithoutExtension(songAudioPath)}-{seconds:0}.wav");
        if (File.Exists(path)) return path;

        var buffer = songAudioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            ? WavIo.Read(songAudioPath)
            : JuceAudioDecoder.Decode(songAudioPath);
        var channels = Math.Max(1, buffer.Channels);
        var frames = Math.Clamp((int)(seconds * buffer.SampleRate), 0, buffer.FrameCount);

        var samples = new float[frames * channels];
        Array.Copy(buffer.Interleaved, 0, samples, 0, samples.Length);
        WavIo.WriteFloat32(path, new AudioBuffer(samples, buffer.SampleRate, channels));
        return path;
    }

    private static bool CacheIsWarm()
    {
        var store = new StemCacheStore();
        return Directory.Exists(store.Root) &&
               Directory.EnumerateFiles(store.Root, "*.dcstems", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    ///     A configuration that names the stem model and PERSISTS NOTHING. The real
    ///     one would reach into the owner's <c>appconfig.json</c> from a test run;
    ///     an eval must not rewrite the machine it measures.
    /// </summary>
    private sealed class EvalConfiguration : IConfigurationService
    {
        private readonly AppConfig _config = new() { HtDemucsActiveModelId = "htdemucs-6s-onnx" };

        public AppConfig LoadAppConfig() => _config;

        public void SaveAppConfig(AppConfig config) { }

        public void Update(Action<AppConfig> mutate) => mutate(_config);

        public void Flush() { }

        public KeybindConfig LoadKeybinds() => new();

        public void SaveKeybinds(KeybindConfig config) { }

        public void InvalidateCache() { }
    }
}
