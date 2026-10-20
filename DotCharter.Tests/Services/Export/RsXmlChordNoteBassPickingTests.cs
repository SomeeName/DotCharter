using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Slap and pop are per-STRING techniques on a strum, and every other part
///     of the pipeline treats them as such: <c>RsXmlSongReader</c> reads
///     <c>slap</c>/<c>pluck</c> off a <c>&lt;chordNote&gt;</c>,
///     <c>SngFromDomainMapper.CreateMaskForChordNote</c> writes the
///     <c>Slap</c>/<c>Pluck</c> mask bits, <c>RsChordDensity</c> refuses to
///     compact a chord carrying one, and <c>EofNoteBuilder</c> exports it. Only
///     <c>RsXmlSongWriter.WriteChordNote</c> did not emit them — so a slapped or
///     popped bass strum survived a save into the <c>.rscp</c> and the psarc but
///     vanished from the RS-XML DLC Builder reads, and came back plain on the
///     next open of that XML.
/// </summary>
public sealed class RsXmlChordNoteBassPickingTests
{
    private static List<BeatMarker> Beats(int count = 32)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120.0, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        return beats;
    }

    /// <summary>One bass strum: the low string slapped, the high string popped —
    /// the ordinary spelling of a slap-and-pop bass figure.</summary>
    private static Arrangement SlapAndPop(IList<BeatMarker> beats)
    {
        var arrangement = new Arrangement
        { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = 4 };
        for (var i = 0; i < 4; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "E5", Frets = { [0] = 0, [3] = 9 } });
        arrangement.Phrases.AddRange(["COUNT", "riff", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(24), 2));

        var level = new ChartLevel { DifficultyIndex = 0 };
        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 0, Fret = 0, BassPicking = BassPickingType.Slap, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 3, Fret = 9, BassPicking = BassPickingType.Pop, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 7));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static List<XElement> ChordNotes(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        return new RsXmlSongWriter().Write(project).Root!
            .Element("levels")!.Element("level")!.Element("chords")!.Element("chord")!
            .Elements("chordNote")
            .OrderBy(e => (int)e.Attribute("string")!)
            .ToList();
    }

    [Fact]
    public void ASlappedAndPoppedStrum_KeepsBothTechniquesInTheXml()
    {
        var beats = Beats();
        var chordNotes = ChordNotes(SlapAndPop(beats), beats);

        Assert.Equal(2, chordNotes.Count);
        Assert.Equal("1", chordNotes[0].Attribute("slap")?.Value);
        Assert.Null(chordNotes[0].Attribute("pluck"));
        Assert.Equal("1", chordNotes[1].Attribute("pluck")?.Value);
        Assert.Null(chordNotes[1].Attribute("slap"));
    }

    /// <summary>The claim that cannot rot: the two artifacts built from ONE chart
    /// have to say the same thing, whichever of them changes.</summary>
    [Fact]
    public void TheSngAndTheRsXml_AgreeOnWhichStringsAreSlappedOrPopped()
    {
        var beats = Beats();
        var arrangement = SlapAndPop(beats);

        var chordNotes = ChordNotes(arrangement, beats);
        var xmlSlap = chordNotes.Where(e => e.Attribute("slap") != null)
            .Select(e => (int)e.Attribute("string")!).ToList();
        var xmlPluck = chordNotes.Where(e => e.Attribute("pluck") != null)
            .Select(e => (int)e.Attribute("string")!).ToList();

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 30_000).Sng;
        var record = sng.ChordNotes.Single();
        var sngSlap = Enumerable.Range(0, 6)
            .Where(s => (record.Mask[s] & RsNoteMask.Slap) != 0).ToList();
        var sngPluck = Enumerable.Range(0, 6)
            .Where(s => (record.Mask[s] & RsNoteMask.Pluck) != 0).ToList();

        Assert.NotEmpty(sngSlap);
        Assert.NotEmpty(sngPluck);
        Assert.Equal(sngSlap, xmlSlap);
        Assert.Equal(sngPluck, xmlPluck);
    }

    /// <summary>A plain strum must stay byte-identical — the attributes are
    /// written only when the technique is actually there.</summary>
    [Fact]
    public void APlainStrum_WritesNeitherAttribute()
    {
        var beats = Beats();
        var arrangement = SlapAndPop(beats);
        foreach (var cn in arrangement.Levels[0].Chords[0].ChordNotes)
            cn.BassPicking = BassPickingType.None;

        foreach (var chordNote in ChordNotes(arrangement, beats))
        {
            Assert.Null(chordNote.Attribute("slap"));
            Assert.Null(chordNote.Attribute("pluck"));
        }
    }
}
