using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     A chart states its tuning TWICE — <c>Arrangement.TuningPitches</c> holds
///     per-string OFFSETS and <c>Arrangement.Tuning</c> holds the note LETTERS —
///     and <c>RsXmlSongReader.ReadTuning</c> filled only the offsets. Every other
///     importer syncs both (<c>GpArrangementParser</c>, <c>GpLegacyImporter</c>,
///     <c>SongsterrConverter</c>, <c>RscpArrangementParser</c>,
///     the package importer); this reader was the outlier, so an
///     <c>.xml</c>-imported chart kept the <see cref="Arrangement"/> DEFAULT
///     <c>{E,A,D,G,B,e}</c> whatever the file said.
///     <para>
///         The letters are not decoration: the lane gutter labels, the tuner, the
///         tuning chip, <c>ChordNameSuggester</c> and the sheet export all read
///         them, and <c>TuningOffsetResolution</c> documents them as the second of
///         the two statements that must agree. Worse for a BASS: the default array
///         is six entries long, so a 4-string import carried two phantom strings'
///         worth of letters beside a 4-entry offsets vector.
///     </para>
/// </summary>
public sealed class RsXmlTuningNameReadTests
{
    [Fact]
    public void DropDTuning_NamesTheLowStringD()
    {
        var arr = ReadArrangement(SongXml("Lead", 6, [-2, 0, 0, 0, 0, 0]));

        Assert.Equal(["D", "A", "D", "G", "B", "E"], arr.Tuning);
    }

    [Fact]
    public void EFlatStandard_NamesEveryStringDown()
    {
        var arr = ReadArrangement(SongXml("Rhythm", 6, [-1, -1, -1, -1, -1, -1]));

        Assert.Equal(["Eb", "G#", "C#", "F#", "Bb", "Eb"], arr.Tuning);
    }

    /// <summary>The letter count has to follow the STRING count, or a 4-string
    /// bass carries two strings the arrangement does not have.</summary>
    [Fact]
    public void AFourStringBass_GetsFourLetters()
    {
        var arr = ReadArrangement(SongXml("Bass", 4, [0, 0, 0, 0]));

        Assert.Equal(4, arr.StringCount);
        Assert.Equal(["E", "A", "D", "G"], arr.Tuning);
    }

    /// <summary>Seven strings are referenced against B-E-A-D-G-B-E, not against
    /// the 6-string table with E repeated.</summary>
    [Fact]
    public void ASevenStringGuitar_GetsSevenLettersOffTheSevenStringReference()
    {
        var arr = ReadArrangement(SongXml("Lead", 7, [0, 0, 0, 0, 0, 0, 0]));

        Assert.Equal(7, arr.StringCount);
        Assert.Equal(["B", "E", "A", "D", "G", "B", "E"], arr.Tuning);
    }

    /// <summary>Standard tuning still reads as standard — the fix must not
    /// perturb the common case.</summary>
    [Fact]
    public void StandardTuning_IsUnchanged()
    {
        var arr = ReadArrangement(SongXml("Lead", 6, [0, 0, 0, 0, 0, 0]));

        Assert.Equal(["E", "A", "D", "G", "B", "E"], arr.Tuning);
    }

    // ---- helpers ------------------------------------------------------------

    private static Arrangement ReadArrangement(string xml) =>
        new RsXmlSongReader().ReadDocument(XDocument.Parse(xml), "tuning_lead.xml").Arrangements[0];

    private static string SongXml(string arrangement, int stringCount, int[] offsets)
    {
        var tuning = new StringBuilder("<tuning");
        for (var i = 0; i < stringCount; i++) tuning.Append($" string{i}=\"{offsets[i]}\"");
        tuning.Append($" strings=\"{stringCount}\" />");

        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <song version="8">
                  <title>Tuning</title>
                  <arrangement>{arrangement}</arrangement>
                  <offset>0.000</offset>
                  <startBeat>0.000</startBeat>
                  <songLength>60.000</songLength>
                  {tuning}
                  <ebeats count="2">
                    <ebeat time="0.000" measure="1" />
                    <ebeat time="0.500" measure="-1" />
                  </ebeats>
                  <levels count="0" />
                </song>
                """;
    }
}
