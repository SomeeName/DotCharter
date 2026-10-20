using System.Xml.Linq;
using DotCharter.Tests.Ai.Eval;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The symbolic eval's corpus, plus the two things it does not carry: the song's
///     AUDIO and the tone's charted SPAN.
///     <para>
///         <see cref="ToneEvalCorpus" /> elects one tone per song and hands back the
///         converted rig — the ground truth — but a <see cref="ToneEvalEntry" /> has no
///         audio path and no time span, because nothing symbolic needed them. This
///         walks the same corpus root for the project WAV next to each
///         <c>.rs2dlc</c> and reads the arrangement XML's <c>&lt;tones&gt;</c> markers
///         to find where the elected tone is actually PLAYED.
///     </para>
///     <para>
///         <b>The span matters more than it looks.</b> Scoring a "Solo" tone against
///         the first thirty seconds of a song measures the intro's rhythm tone against
///         a lead rig and calls the difference a ceiling. Where the markers are
///         missing or the name does not match, the row falls back to the first
///         <see cref="MaxSpanSeconds" /> seconds and SAYS so in its
///         <see cref="AudioEvalRow.SpanSource" />, so the report can separate the two
///         populations instead of averaging them.
///     </para>
/// </summary>
public static class AudioEvalCorpus
{
    /// <summary>How much audio a row scores. Long enough that the frame gate has
    /// something to average and a delay or reverb has room to be heard; short enough
    /// that separating 35 of them is minutes rather than a weekend.</summary>
    public const double MaxSpanSeconds = 30.0;

    /// <summary>A span shorter than this is not worth scoring, and the row falls back
    /// to the head of the song.</summary>
    public const double MinSpanSeconds = 8.0;

    /// <summary>One song: the truth rig, where its audio is, and which slice of it the
    /// elected tone is played over.</summary>
    public sealed record AudioEvalRow(
        ToneEvalEntry Entry, string SongAudioPath, TimeSpan From, TimeSpan To, string SpanSource)
    {
        /// <summary>A stable, filesystem-safe name for this row's excerpt.</summary>
        public string ExcerptName
        {
            get
            {
                var raw = $"{Entry.SongId}-{Entry.ToneName}-{From.TotalSeconds:F0}";
                return string.Concat(raw.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            }
        }
    }

    /// <summary>
    ///     A corpus entry that could NOT be turned into a row, and why.
    ///     <para>
    ///         <b>It is a reported failure and never an exception.</b> A run that has
    ///         already separated twenty stems must not be destroyed by the
    ///         twenty-first song's package being ambiguous — but a row that silently
    ///         disappears is how the tone-JSON resolver hid its own collisions for
    ///         two releases.
    ///     </para>
    /// </summary>
    /// <param name="SongId">The entry's <see cref="ToneEvalEntry.SongId" />.</param>
    /// <param name="Song">"Artist — Title", for the report.</param>
    /// <param name="ToneName">The elected tone's name.</param>
    /// <param name="Reason">What went wrong, in one line.</param>
    public sealed record CorpusProblem(string SongId, string Song, string ToneName, string Reason);

    /// <summary>
    ///     What <see cref="LoadDetailed(int)" /> found: the rows, the entries it had to drop,
    ///     and how many DISTINCT audio files the rows actually reference.
    /// </summary>
    /// <param name="Rows">Every entry that resolved to exactly one folder with audio.</param>
    /// <param name="Problems">Every entry that did not.</param>
    public sealed record CorpusLoad(
        IReadOnlyList<AudioEvalRow> Rows, IReadOnlyList<CorpusProblem> Problems)
    {
        /// <summary>
        ///     How many distinct audio FILES the rows reference.
        ///     <para>
        ///         <b>This is the population size, and it is not
        ///         <c>Rows.Count</c>.</b> Until 2026-09-13 the resolver matched an
        ///         entry to a folder by its tone JSON, so twelve charted rows scored
        ///         about five distinct recordings and every mean this lane published
        ///         was pseudo-replicated. The number is printed once per run so a
        ///         reader can never again take the row count for the sample size.
        ///     </para>
        /// </summary>
        public int DistinctAudioFiles => Rows
            .Select(r => r.SongAudioPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        /// <summary>Distinct (audio file, span) pairs — the count that matters when two
        /// rows of one song are scored over different spans.</summary>
        public int DistinctAudioSpans => Rows
            .Select(r => r.SongAudioPath.ToLowerInvariant() + "|" +
                         r.From.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                         r.To.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>
    ///     Every corpus song for which both a truth rig and playable audio exist, in
    ///     the symbolic corpus's own order.
    /// </summary>
    public static IReadOnlyList<AudioEvalRow> Load(int limit = 60) => LoadDetailed(limit).Rows;

    /// <summary>
    ///     <see cref="Load" /> with the dropped entries kept instead of swallowed.
    ///     <para>
    ///         A row is built only when the entry's song resolves to EXACTLY ONE corpus
    ///         folder, by title + artist (<see cref="ToneEvalCorpus.TryReadPackageSongId" />
    ///         — the same identity the symbolic corpus keys on). Zero folders and two
    ///         folders are both reported, because "two packages of this song are on
    ///         disk and I picked one" is exactly the silent choice that made the old
    ///         resolver's collisions invisible.
    ///     </para>
    /// </summary>
    /// <param name="limit">Hard ceiling on songs.</param>
    /// <returns>The rows, the problems, and the distinct-audio counts.</returns>
    public static CorpusLoad LoadDetailed(int limit = 60)
    {
        var root = ToneEvalCorpus.ResolveCorpusRoot();
        return root is null ? new CorpusLoad([], []) : LoadDetailed(root, limit);
    }

    /// <summary>
    ///     <see cref="LoadDetailed(int)" /> against an EXPLICIT corpus root, so a test
    ///     can drive a synthetic corpus without touching the process-global
    ///     <see cref="ToneEvalCorpus.CorpusRootVariable" />.
    /// </summary>
    /// <param name="root">The corpus root to walk.</param>
    /// <param name="limit">Hard ceiling on songs.</param>
    /// <returns>The rows, the problems, and the distinct-audio counts.</returns>
    public static CorpusLoad LoadDetailed(string root, int limit = 60)
    {
        ArgumentNullException.ThrowIfNull(root);

        var entries = ToneEvalCorpus.LoadLocalFrom(root, limit);
        var index = BuildFolderIndex(root, entries.Any(e => e.Source == "psarc"));

        var rows = new List<AudioEvalRow>();
        var problems = new List<CorpusProblem>();
        foreach (var entry in entries)
        {
            var song = entry.Artist + " — " + entry.SongTitle;
            var folders = index.TryGetValue(entry.SongId, out var found) ? found : [];

            if (folders.Count == 0)
            {
                problems.Add(new CorpusProblem(entry.SongId, song, entry.ToneName,
                    "no corpus folder carries a package with this title + artist"));
                continue;
            }

            if (folders.Count > 1)
            {
                problems.Add(new CorpusProblem(entry.SongId, song, entry.ToneName,
                    $"AMBIGUOUS: {folders.Count} folders carry this title + artist " +
                    $"({string.Join("; ", folders.Select(Path.GetFileName))}) — " +
                    "the row is dropped rather than scoring one of them at random"));
                continue;
            }

            var folder = folders[0];
            var audio = FindSongAudio(folder);
            if (audio is null)
            {
                problems.Add(new CorpusProblem(entry.SongId, song, entry.ToneName,
                    "no non-preview .wav or .ogg beside the package in " + Path.GetFileName(folder)));
                continue;
            }

            var (from, to, source) = FindSpan(folder, entry.ToneName);
            rows.Add(new AudioEvalRow(entry, audio, from, to, source));
        }

        // The one thing a per-entry check cannot see: two DIFFERENT songs that
        // nevertheless landed on one audio file. That is the exact shape of the bug
        // this resolver replaced, so it is asserted rather than assumed.
        foreach (var group in rows
                     .GroupBy(r => r.SongAudioPath, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Select(r => r.Entry.SongId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            problems.Add(new CorpusProblem("(collision)", Path.GetFileName(group.Key), "—",
                "COLLISION: " + string.Join(", ", group.Select(r => r.Entry.SongId).Distinct(StringComparer.Ordinal)) +
                " all resolved to one audio file"));
        }

        return new CorpusLoad(rows, problems);
    }

    /// <summary>
    ///     songId → every distinct FOLDER under the root carrying a package with that
    ///     identity.
    /// </summary>
    /// <param name="root">The corpus root.</param>
    /// <param name="includePsarc">Whether to pay for decoding <c>.psarc</c> manifests.
    /// Only worth it when some entry actually came from one; the local corpus is
    /// <c>.rs2dlc</c> throughout.</param>
    private static Dictionary<string, List<string>> BuildFolderIndex(string root, bool includePsarc)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var patterns = includePsarc ? new[] { "*.rs2dlc", "*.psarc" } : ["*.rs2dlc"];

        foreach (var pattern in patterns)
        {
            foreach (var file in EnumerateSafely(root, pattern))
            {
                if (!ToneEvalCorpus.TryReadPackageSongId(file, out var songId)) continue;
                var folder = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(folder)) continue;

                if (!index.TryGetValue(songId, out var folders))
                    index[songId] = folders = [];
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
            }
        }

        return index;
    }

    private static IReadOnlyList<string> EnumerateSafely(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    ///     Writes (once) a mono-preserving stereo excerpt of the row's span and returns
    ///     its path.
    ///     <para>
    ///         <b>Separating the excerpt rather than the song is what makes this eval
    ///         runnable.</b> htdemucs processes in segments, so a 30-second clip
    ///         separates to the same quality as the 30 seconds inside a four-minute
    ///         song — but 35 excerpts is 17 minutes of audio against two and a half
    ///         hours. The stem cache keys on the file's CONTENT, so each excerpt gets
    ///         its own durable entry and a second eval run separates nothing.
    ///     </para>
    /// </summary>
    public static string EnsureExcerpt(AudioEvalRow row, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, row.ExcerptName + ".wav");
        if (File.Exists(path)) return path;

        var buffer = Decode(row.SongAudioPath);
        var channels = Math.Max(1, buffer.Channels);
        var rate = buffer.SampleRate;
        var firstFrame = Math.Clamp((int)(row.From.TotalSeconds * rate), 0, buffer.FrameCount);
        var lastFrame = Math.Clamp((int)(row.To.TotalSeconds * rate), firstFrame, buffer.FrameCount);

        var frames = lastFrame - firstFrame;
        var samples = new float[frames * channels];
        Array.Copy(buffer.Interleaved, firstFrame * channels, samples, 0, samples.Length);

        WavIo.WriteFloat32(path, new AudioBuffer(samples, rate, channels));
        return path;
    }

    private static AudioBuffer Decode(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            ? WavIo.Read(path)
            : JuceAudioDecoder.Decode(path);

    private static string? FindSongAudio(string folder)
    {
        var best = Directory.EnumerateFiles(folder, "*.wav")
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (best is not null) return best.FullName;

        return Directory.EnumerateFiles(folder, "*.ogg")
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    ///     The first span the named tone is charted over, from any guitar arrangement
    ///     XML in the folder. <c>&lt;tonebase&gt;</c> covers the song up to the first
    ///     <c>&lt;tone time=… name=…/&gt;</c>; each marker starts a span that runs to
    ///     the next one.
    /// </summary>
    private static (TimeSpan From, TimeSpan To, string Source) FindSpan(string folder, string toneName)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("showlight", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains(".debug.", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("tone2014", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("BASS", StringComparison.OrdinalIgnoreCase)) continue;

            XDocument document;
            try { document = XDocument.Load(file); }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { continue; }

            var root = document.Root;
            if (root is null) continue;

            var changes = root.Element("tones")?.Elements("tone")
                .Select(e => (
                    Time: double.TryParse(e.Attribute("time")?.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : -1,
                    Name: e.Attribute("name")?.Value ?? string.Empty))
                .Where(c => c.Time >= 0)
                .OrderBy(c => c.Time)
                .ToList() ?? [];

            var songLength = double.TryParse(root.Element("songLength")?.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var length) ? length : double.MaxValue;

            for (var i = 0; i < changes.Count; i++)
            {
                if (!string.Equals(changes[i].Name, toneName, StringComparison.OrdinalIgnoreCase)) continue;

                var start = changes[i].Time;
                var end = i + 1 < changes.Count ? changes[i + 1].Time : songLength;
                if (end - start < MinSpanSeconds) continue;

                return (TimeSpan.FromSeconds(start),
                    TimeSpan.FromSeconds(Math.Min(end, start + MaxSpanSeconds)),
                    "charted:" + name);
            }

            // The base tone is charted too — it just has no marker of its own.
            var baseTone = root.Element("tonebase")?.Value;
            if (string.Equals(baseTone, toneName, StringComparison.OrdinalIgnoreCase))
            {
                var end = changes.Count > 0 ? changes[0].Time : Math.Min(songLength, MaxSpanSeconds);
                if (end >= MinSpanSeconds)
                    return (TimeSpan.Zero, TimeSpan.FromSeconds(Math.Min(end, MaxSpanSeconds)), "tonebase:" + name);
            }
        }

        return (TimeSpan.Zero, TimeSpan.FromSeconds(MaxSpanSeconds), "head-of-song");
    }
}
