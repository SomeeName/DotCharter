using System.Text.RegularExpressions;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Export.Lyrics;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Lyrics;

/// <summary>
///     The two standalone writers: Rocksmith vocals XML and enhanced LRC, both
///     straight from seconds.
///     <para>
///         <b>The XML case is a real round trip.</b> It is written by
///         <see cref="RsVocalsSecondsWriter" /> and read back by the app's OWN
///         importer, so a convention that drifted between the two writers would
///         fail here rather than in somebody's chart.
///     </para>
///     <para>
///         <b>The line-oriented assertions are not style.</b> Editor on Fire's
///         importer reads this file line by line, not with an XML parser: it needs
///         the literal <c>&lt;vocals count</c> (that IS its format detection), one
///         <c>&lt;vocal/&gt;</c> per line, and all four attributes on every entry
///         or the import aborts outright. RsCharter needs <c>time</c>/<c>length</c>
///         at exactly three decimals — its converter parses the string with the dot
///         deleted, so <c>"12.34"</c> would read as 1,234 ms.
///     </para>
/// </summary>
public sealed class LyricSyncWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-lyricsync-writers").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>"Hel-lo world" then "bye": two phrases, one word split in two.</summary>
    private static List<LyricSyncSyllable> Sample() =>
    [
        new(0, "Hel", ContinuesWord: true, EndsLine: false, 0, 12.345, 12.600, 0.40, false),
        new(1, "lo", ContinuesWord: false, EndsLine: false, 0, 12.600, 13.000, 0.30, false),
        new(2, "world", ContinuesWord: false, EndsLine: true, 0, 13.100, 13.600, 0.20, false),
        new(3, "bye", ContinuesWord: false, EndsLine: true, 1, 20.000, 20.500, 0.10, false),
    ];

    // ───────────────────────────── Rocksmith XML ─────────────────────────────

    /// <summary>
    ///     Written, then read back by the app's own RS-XML importer: same count,
    ///     same text, same word/phrase structure, same milliseconds.
    /// </summary>
    [Fact]
    public void TheVocalsXmlRoundTripsThroughTheAppsOwnImporter()
    {
        var path = Path.Combine(_dir, "song_vocals.xml");
        RsVocalsSecondsWriter.WriteTo(path, Sample());

        // A beat grid to position against — the reader needs one to produce
        // anything but position zero, and 120 BPM makes the arithmetic exact.
        List<BeatMarker> beats =
            [.. Enumerable.Range(0, 128).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

        var host = new ChartProject();
        host.Beats.Clear();
        foreach (var beat in beats) host.Beats.Add(beat);

        var plan = RsXmlImporter.Prepare(path, beats);
        Assert.Equal(RsXmlImportKind.Vocals, plan.Kind);
        var imported = new RsXmlImporter().Apply(host, plan);
        Assert.True(imported.AddedVocalPhrases > 0);

        var lyrics = host.VocalTracks.SelectMany(t => t.Phrases).SelectMany(p => p.Lyrics)
            .OrderBy(l => l.Position).ToList();

        Assert.Equal(4, lyrics.Count);

        // The '-' and '+' are structure: the reader strips them into the flags
        // (#324), so the editor never shows them as text.
        Assert.Equal("Hel", lyrics[0].Text);
        Assert.True(lyrics[0].IsWordPart);
        Assert.Equal("lo", lyrics[1].Text);
        Assert.False(lyrics[1].IsWordPart);
        Assert.Equal("world", lyrics[2].Text);
        Assert.True(lyrics[2].IsPhraseEnd);
        Assert.Equal("bye", lyrics[3].Text);
        Assert.True(lyrics[3].IsPhraseEnd);

        // …and the times survive the round trip to the millisecond. 12.345 s is off
        // the 1/192-beat grid on this 120 BPM map; the import used to snap it to
        // 12,343.75 ms, and now keeps a finer position (#325).
        Assert.Equal(12_345, TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lyrics[0].Position, beats)));
        Assert.Equal(20_000, TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lyrics[3].Position, beats)));
    }

    /// <summary>
    ///     The three shapes Editor on Fire's line-by-line importer depends on, and
    ///     the three-decimal rule RsCharter's time converter depends on.
    /// </summary>
    [Fact]
    public void TheVocalsXmlHasTheShapeBothImportersNeed()
    {
        var path = Path.Combine(_dir, "shape_vocals.xml");
        RsVocalsSecondsWriter.WriteTo(path, Sample());
        var lines = File.ReadAllLines(path);

        // Format DETECTION in EOF is the literal "<vocals count".
        Assert.Contains(lines, l => l.Contains("<vocals count", StringComparison.Ordinal));

        var entries = lines.Where(l => l.Contains("<vocal ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, entries.Count);

        foreach (var entry in entries)
        {
            // All four attributes, or EOF's import aborts.
            Assert.Contains("time=", entry, StringComparison.Ordinal);
            Assert.Contains("note=", entry, StringComparison.Ordinal);
            Assert.Contains("length=", entry, StringComparison.Ordinal);
            Assert.Contains("lyric=", entry, StringComparison.Ordinal);

            // Exactly three decimals on both times.
            foreach (Match m in Regex.Matches(entry, @"(?:time|length)=""(?<v>[^""]*)"""))
                Assert.Matches(@"^-?\d+\.\d{3}$", m.Groups["v"].Value);
        }
    }

    /// <summary>
    ///     Rounding, not truncation. Truncating biases every onset early by up to a
    ///     millisecond in the SAME direction for the whole song, which is a
    ///     systematic drag rather than noise.
    /// </summary>
    [Fact]
    public void SecondsAreRoundedToMillisecondsRatherThanTruncated()
    {
        var document = RsVocalsSecondsWriter.Write(
            [new LyricSyncSyllable(0, "a", false, true, 0, 1.2346, 1.5004, 0.5, false)]);

        var vocal = document.Root!.Elements("vocal").Single();
        Assert.Equal("1.235", vocal.Attribute("time")!.Value);
        Assert.Equal("0.265", vocal.Attribute("length")!.Value);
    }

    /// <summary>A syllable may never run into the next one's onset.</summary>
    [Fact]
    public void AnOverlongSyllableIsClippedAtTheNextOnset()
    {
        var document = RsVocalsSecondsWriter.Write(
        [
            new LyricSyncSyllable(0, "a", false, false, 0, 1.000, 9.000, 0.5, false),
            new LyricSyncSyllable(1, "b", false, true, 0, 1.500, 2.000, 0.5, false),
        ]);

        var first = document.Root!.Elements("vocal").First();
        Assert.Equal("0.500", first.Attribute("length")!.Value);
    }

    // ──────────────────────────────── LRC ────────────────────────────────────

    /// <summary>
    ///     Enhanced LRC: one line per phrase, a <c>[mm:ss.xx]</c> line stamp at the
    ///     first onset and a <c>&lt;mm:ss.xx&gt;</c> before every syllable, with
    ///     <c>-</c> joining syllables inside a word and a space starting a new one.
    /// </summary>
    [Fact]
    public void TheLrcIsOneEnhancedLinePerPhrase()
    {
        var lrc = LrcSecondsWriter.Write(Sample(), title: "Song", artist: "Band");
        var lines = lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("[ti:Song]", lines[0]);
        Assert.Equal("[ar:Band]", lines[1]);
        Assert.Equal("[00:12.35]<00:12.35>Hel-<00:12.60>lo <00:13.10>world", lines[2]);
        Assert.Equal("[00:20.00]<00:20.00>bye", lines[3]);
    }

    /// <summary>
    ///     Every stamp is <c>mm:ss.xx</c> — two digits of centiseconds, a DOT
    ///     before them. Three digits would be read as milliseconds by some parsers
    ///     and as centiseconds by others, and RsCharter's would put a <c>.500</c>
    ///     stamp 5 seconds late.
    /// </summary>
    [Fact]
    public void EveryLrcStampIsCentisecondPrecision()
    {
        var lrc = LrcSecondsWriter.Write(Sample());

        var stamps = Regex.Matches(lrc, @"[\[<](?<s>[0-9:.]+)[\]>]").Select(m => m.Groups["s"].Value).ToList();
        Assert.NotEmpty(stamps);
        Assert.All(stamps, s => Assert.Matches(@"^\d{2}:\d{2}\.\d{2}$", s));
    }

    /// <summary>
    ///     A trailing separator after the last syllable of a line would re-import as
    ///     a word part that joins across the phrase break.
    /// </summary>
    [Fact]
    public void NoLineEndsInASeparator()
    {
        var lrc = LrcSecondsWriter.Write(Sample());

        foreach (var line in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.False(line.EndsWith('-'), line);
            Assert.False(line.EndsWith(' '), line);
        }
    }

    /// <summary>
    ///     A sheet whose last line carries no explicit end still has to be flushed —
    ///     otherwise the final phrase is silently dropped.
    /// </summary>
    [Fact]
    public void ATrailingPhraseWithNoEndMarkerIsStillWritten()
    {
        var lrc = LrcSecondsWriter.Write(
        [
            new LyricSyncSyllable(0, "a", false, true, 0, 1.0, 1.2, 0.5, false),
            new LyricSyncSyllable(1, "b", false, false, 1, 2.0, 2.2, 0.5, false),
        ]);

        Assert.Equal(2, lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("<00:02.00>b", lrc, StringComparison.Ordinal);
    }
}
