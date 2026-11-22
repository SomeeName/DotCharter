using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     #305: a stock bass file lists string0..string5 and no <c>strings</c> attribute (EOF always
///     writes six). It is a four-string bass: read as a six-string one, the export split its
///     low-E chord notes as the extended bass's low B and a save/load changed the chart.
/// </summary>
public sealed class RsXmlStockBassTests
{
    private static string Bass(string tuningAttrs, string extraNote) => $"""
        <song version="8">
          <title>Stock</title>
          <arrangement>Bass</arrangement>
          <tuning {tuningAttrs} string0="0" string1="0" string2="0" string3="0" string4="0" string5="0" />
          <chordTemplates count="1">
            <chordTemplate chordName="E5" displayName="E5" finger0="1" fret0="2" finger1="3" fret1="4" />
          </chordTemplates>
          <ebeats count="3">
            <ebeat time="0.000" measure="1" />
            <ebeat time="10.000" measure="-1" />
            <ebeat time="20.000" measure="-1" />
          </ebeats>
          <levels count="1">
            <level difficulty="0">
              <notes count="1">
                <note time="10.000" string="{extraNote}" fret="3" sustain="0.000" />
              </notes>
              <chords count="1">
                <chord time="12.000" chordId="0" strum="down">
                  <chordNote time="12.000" string="0" fret="2" sustain="0.000" />
                  <chordNote time="12.000" string="1" fret="4" sustain="0.000" />
                </chord>
              </chords>
              <anchors count="1"><anchor time="10.000" fret="2" width="4.000" /></anchors>
              <handShapes count="1"><handShape chordId="0" startTime="12.000" endTime="12.500" /></handShapes>
            </level>
          </levels>
        </song>
        """;

    [Fact]
    public void A_stock_bass_without_a_strings_attribute_is_a_four_string_bass_that_round_trips()
    {
        var reader = new RsXmlSongReader();
        var loaded = reader.ReadDocument(XDocument.Parse(Bass("", "2")), "bass.xml");
        var arr = loaded.Arrangements[0];
        Assert.Equal(4, arr.StringCount);
        Assert.Equal(4, arr.TuningPitches.Count);

        var reloaded = reader.ReadDocument(new RsXmlSongWriter().Write(loaded, null), "again.xml").Arrangements[0];

        Assert.Equal(4, reloaded.StringCount);
        Assert.Equal(arr.Levels[0].Notes.Count, reloaded.Levels[0].Notes.Count);
        Assert.Equal(arr.Levels[0].Chords.Count, reloaded.Levels[0].Chords.Count);
        Assert.Equal(2, Assert.Single(reloaded.Levels[0].Chords).ChordNotes.Count);
    }

    [Fact]
    public void A_bass_file_that_uses_string_four_or_states_its_strings_keeps_its_count()
    {
        var reader = new RsXmlSongReader();
        Assert.Equal(6, reader.ReadDocument(XDocument.Parse(Bass("", "4")), "b.xml").Arrangements[0].StringCount);
        Assert.Equal(6, reader.ReadDocument(XDocument.Parse(Bass("strings=\"6\"", "2")), "b.xml").Arrangements[0].StringCount);
    }
}
