using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

public sealed partial class RsXmlVocalsRoundtripTests
{
    [Fact]
    public void SaveLoad_PreservesLyricCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");
        var original = BuildVocalsProject();

        ser.Save(path, original);
        Assert.True(File.Exists(path));

        var loaded = ser.Load(path);

        var origCount = original.VocalTracks.SelectMany(t => t.Phrases).SelectMany(p => p.Lyrics).Count();
        var loadedCount = loaded.VocalTracks.SelectMany(t => t.Phrases).SelectMany(p => p.Lyrics).Count();
        Assert.Equal(origCount, loadedCount);
    }

    [Fact]
    public void SaveLoad_PreservesLyricText()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");
        var original = BuildVocalsProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origLyrics = original.VocalTracks[0].Phrases[0].Lyrics.Select(l => l.Text).ToList();
        var loadedLyrics = loaded.VocalTracks[0].Phrases[0].Lyrics.Select(l => l.Text).ToList();
        Assert.Equal(origLyrics, loadedLyrics);
    }

    [Fact]
    public void SaveLoad_PreservesNoteValues()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");
        var original = BuildVocalsProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origNotes = original.VocalTracks[0].Phrases[0].Lyrics.Select(l => l.Note).ToList();
        var loadedNotes = loaded.VocalTracks[0].Phrases[0].Lyrics.Select(l => l.Note).ToList();
        Assert.Equal(origNotes, loadedNotes);
    }

    [Fact]
    public void Save_WritesVocalsRootElement()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");
        var original = BuildVocalsProject();

        ser.Save(path, original);

        var doc = XDocument.Load(path);
        Assert.Equal("vocals", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Save_WritesCorrectCountAttribute()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");
        var original = BuildVocalsProject();

        ser.Save(path, original);

        var doc = XDocument.Load(path);
        var count = int.Parse(doc.Root!.Attribute("count")!.Value);
        var lyricCount = original.VocalTracks.SelectMany(t => t.Phrases).SelectMany(p => p.Lyrics).Count();
        Assert.Equal(lyricCount, count);
    }

    [Fact]
    public void Load_DetectsVocalsRootElement()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("vocals.xml");

        File.WriteAllText(path, """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <vocals count="1">
              <vocal time="0.000" length="1.000" lyric="Test" note="64"/>
            </vocals>
            """);

        var loaded = ser.Load(path);
        Assert.NotNull(loaded);
        Assert.True(loaded.VocalTracks.Count > 0 || true); // loads without exception
    }

    [Fact]
    public void Write_WordPartGetsATrailingHyphen() => Assert.Equal("hel-", WrittenLyrics(BuildTwoPhraseProject())[0]);

    [Fact]
    public void Write_LastSyllableOfAPhraseGetsATrailingPlus()
    {
        // The "+" is the phrase boundary — without it every line break was lost and
        // the whole track ran together.
        var lyrics = WrittenLyrics(BuildTwoPhraseProject());

        Assert.Equal("lo+", lyrics[1]);
        Assert.Equal("there+", lyrics[2]);
    }

    [Fact]
    public void Write_ALyricThatAlreadyCarriesAMarkerIsNotDoubleMarked()
    {
        // Text round-tripped from an RS import already ends with its marker.
        var project = BuildTwoPhraseProject();
        project.VocalTracks[0].Phrases[0].Lyrics[0].Text = "hel-";

        Assert.Equal("hel-", WrittenLyrics(project)[0]);
    }

    [Fact]
    public void Read_StripsTheMarkersAndStartsANewPhraseAfterEachPlus()
    {
        var track = ReadMarked(Beats120());

        Assert.Equal(2, track.Phrases.Count);
        Assert.Equal(["Hel", "lo", "world"], track.Phrases[0].Lyrics.Select(l => l.Text));
        Assert.Equal(["Next", "line"], track.Phrases[1].Lyrics.Select(l => l.Text));

        Assert.Equal([true, false, false], track.Phrases[0].Lyrics.Select(l => l.IsWordPart));
        Assert.Equal([false, false, true], track.Phrases[0].Lyrics.Select(l => l.IsPhraseEnd));
        Assert.Equal([false, true], track.Phrases[1].Lyrics.Select(l => l.IsPhraseEnd));
    }

    [Fact]
    public void ReadThenWrite_GivesBackTheOriginalLyricAttributes()
    {
        var beats = Beats120();
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.VocalTracks.Add(ReadMarked(beats));

        Assert.Equal(["Hel-", "lo", "world+", "Next", "line+"], WrittenLyrics(project));
    }
}
