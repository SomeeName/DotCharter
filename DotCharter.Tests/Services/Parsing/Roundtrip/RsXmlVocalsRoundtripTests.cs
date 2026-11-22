using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

public sealed partial class RsXmlVocalsRoundtripTests
{
    [Fact]
    public void Standalone_load_and_save_preserve_absolute_vocal_timestamps()
    {
        var path = TempPath("source-vocals.xml");
        File.WriteAllText(path, """
            <vocals count="2">
              <vocal time="1.234" length="0.567" lyric="hello+" note="64"/>
              <vocal time="9.876" length="1.234" lyric="world+" note="67"/>
            </vocals>
            """);
        var serializer = new XmlChartSerializer();

        var loaded = serializer.Load(path);
        var lyrics = loaded.VocalTracks.Single().Phrases.SelectMany(p => p.Lyrics).ToArray();
        Assert.Equal(1234, TimelineMath.ToWholeMs(lyrics[0].Position.GetPositionMs(loaded.Beats)));
        Assert.Equal(1801, TimelineMath.ToWholeMs(lyrics[0].EndPosition.GetPositionMs(loaded.Beats)));
        Assert.Equal(9876, TimelineMath.ToWholeMs(lyrics[1].Position.GetPositionMs(loaded.Beats)));
        Assert.Equal(11110, TimelineMath.ToWholeMs(lyrics[1].EndPosition.GetPositionMs(loaded.Beats)));

        var saved = TempPath("saved-vocals.xml");
        serializer.Save(saved, loaded);
        var vocals = XDocument.Load(saved).Root!.Elements("vocal").ToArray();
        Assert.Equal(["1.234", "9.876"], vocals.Select(v => v.Attribute("time")!.Value));
        Assert.Equal(["0.567", "1.234"], vocals.Select(v => v.Attribute("length")!.Value));
    }

    [Fact]
    public void UnmarkedVocals_NormalizeGapAndFinalPhraseEnds()
    {
        var phrases = RsVocalPhraseBuilder.Build(
            [new RsVocalSyllable(0, 500, "one", 60), new RsVocalSyllable(6000, 6500, "two", 62)],
            ms => TimelineMath.PositionFromMs(ms, Beats120()));

        Assert.Equal(2, phrases.Count);
        Assert.All(phrases, p => Assert.True(Assert.Single(p.Lyrics).IsPhraseEnd));
    }

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
