using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace DotCharter.Tests.Services.Audio.Lyrics.Probes;

/// <summary>One syllable as the charter synced it: its text, the sheet marks and its span in seconds.</summary>
public sealed record LyricTruthSyllable(string Text, bool Continues, bool EndsLine, double Start, double End);

/// <summary>One beat of the grid a song was charted on.</summary>
public sealed record LyricCorpusBeat(double Seconds, bool IsMeasure);

/// <summary>
///     What the corpus builder reads for one song: the hand-synced syllables, the beat
///     grid and the mix they were synced against. Written as <c>*.lyric-truth.json</c> by
///     whatever can read the song (the romaji exporter here, the package reader's own
///     exporter for the stock English arrangements).
/// </summary>
public sealed record LyricTruthSource(
    string Title, string Language, string AudioPath, LyricCorpusBeat[] Beats, LyricTruthSyllable[] Syllables);

/// <summary>
///     One song of the built corpus. Its audio lies beside the manifest as
///     <c>&lt;slug&gt;/mix.f32</c> and <c>&lt;slug&gt;/vocals.f32</c>: raw 16 kHz mono floats,
///     decoded once, so a rerun involves no decoder and no separator.
/// </summary>
public sealed record LyricCorpusSong(
    string Slug, string Title, string Language, LyricCorpusBeat[] Beats, LyricTruthSyllable[] Syllables)
{
    /// <summary>What a charter would paste: the probe's input is the product's real input.</summary>
    [JsonIgnore]
    public string PasteText => LyricProbeCorpus.ToPasteText(Syllables);

    /// <summary>The grid as the chart holds it.</summary>
    public List<BeatMarker> BeatMarkers()
    {
        var beats = new List<BeatMarker>(Beats.Length);
        for (var i = 0; i < Beats.Length; i++)
        {
            var next = i + 1 < Beats.Length ? Beats[i + 1].Seconds : Beats[i].Seconds + 0.5;
            beats.Add(new BeatMarker
            {
                Time = Beats[i].Seconds * 1000.0,
                IsMeasure = Beats[i].IsMeasure,
                Bpm = 60.0 / Math.Max(next - Beats[i].Seconds, 1e-3)
            });
        }

        return beats;
    }
}

/// <summary>
///     The lyric probe corpus on disk (#237). Building it costs a separation per song;
///     everything the probes ask afterwards (onsets, ends, a threshold sweep) re-reads the
///     cached audio. Every location comes from an environment variable, so the probes skip
///     on any machine without the corpus.
/// </summary>
public static class LyricProbeCorpus
{
    /// <summary>The built corpus: <c>corpus.json</c> plus one folder of audio per song.</summary>
    public const string CorpusVariable = "DOTCHARTER_LYRIC_CORPUS";

    /// <summary>A folder of <c>*.lyric-truth.json</c> sources the builder reads.</summary>
    public const string SourcesVariable = "DOTCHARTER_LYRIC_SOURCES";

    /// <summary>The lyric model root (holding <c>lyric-align</c> and <c>lyric-align-en</c>); unset uses the app's.</summary>
    public const string ModelsVariable = "DOTCHARTER_LYRIC_MODELS";

    /// <summary>Where the builder keeps its stem model; unset uses a <c>stem-models</c> folder in the corpus.</summary>
    public const string StemModelsVariable = "DOTCHARTER_LYRIC_STEM_MODELS";

    /// <summary>A folder the probes write their markdown report to; unset writes to the test output only.</summary>
    public const string ReportVariable = "DOTCHARTER_LYRIC_PROBE_OUT";

    /// <summary>The stem model the corpus is separated with: public, and the product's CPU default.</summary>
    public const string StemModelId = "htdemucs-6s-onnx";

    private const string ManifestName = "corpus.json";
    private const string SourceSuffix = ".lyric-truth.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The value of <paramref name="variable" />, or null when it is unset.</summary>
    public static string? Variable(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

    public static List<LyricCorpusSong> Load(string root)
    {
        var path = Path.Combine(root, ManifestName);
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<LyricCorpusSong>>(stream) ?? [];
    }

    public static void Save(string root, IReadOnlyList<LyricCorpusSong> songs) =>
        File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(songs, Json));

    public static string AudioPath(string root, string slug, string source) => Path.Combine(root, slug, source + ".f32");

    public static float[] ReadAudio(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    public static void WriteAudio(string path, float[] samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }

    public static List<LyricTruthSource> ReadSources(string folder) =>
    [
        .. Directory.EnumerateFiles(folder, "*" + SourceSuffix)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => JsonSerializer.Deserialize<LyricTruthSource>(File.ReadAllText(path))!)
    ];

    public static void WriteSource(string folder, LyricTruthSource source)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Slugify(source.Title) + SourceSuffix), JsonSerializer.Serialize(source, Json));
    }

    /// <summary>The sheet a charter would paste for <paramref name="syllables" />.</summary>
    public static string ToPasteText(IEnumerable<LyricTruthSyllable> syllables)
    {
        var sb = new StringBuilder();
        foreach (var s in syllables)
        {
            sb.Append(s.Text);
            if (s.EndsLine) sb.Append('\n');
            else if (s.Continues) sb.Append('-');
            else sb.Append(' ');
        }

        return sb.ToString();
    }

    public static string Slugify(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>
    ///     A charted vocal arrangement's syllables. The word-continuation hyphen and the
    ///     phrase-end plus live inside the lyric string; the end is <c>time + length</c>.
    /// </summary>
    public static List<LyricTruthSyllable> ReadVocalXml(string path)
    {
        var result = new List<LyricTruthSyllable>();
        foreach (var element in XDocument.Load(path).Root?.Elements("vocal") ?? [])
        {
            var raw = element.Attribute("lyric")?.Value ?? "";
            var time = Number(element, "time");
            var length = Number(element, "length");
            var endsLine = raw.EndsWith('+');
            raw = raw.TrimEnd('+');
            var continues = raw.EndsWith('-');
            raw = raw.TrimEnd('-');
            if (raw.Length > 0) result.Add(new LyricTruthSyllable(raw, continues, endsLine, time, time + length));
        }

        return [.. result.OrderBy(s => s.Start)];
    }

    /// <summary>The <c>ebeat</c> grid of a charted arrangement; a <c>measure</c> of -1 is a plain beat.</summary>
    public static List<LyricCorpusBeat> ReadBeatXml(string path) =>
    [
        .. XDocument.Load(path).Root?.Element("ebeats")?.Elements("ebeat")
            .Select(e => new LyricCorpusBeat(Number(e, "time"), Number(e, "measure") >= 0)) ?? []
    ];

    /// <summary>Kana, kanji and hangul: outside the roman-alphabet aligner's contract.</summary>
    public static bool IsCjk(string text) =>
        text.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '가' and <= '힯');

    private static double Number(XElement element, string name) =>
        double.Parse(element.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
}
