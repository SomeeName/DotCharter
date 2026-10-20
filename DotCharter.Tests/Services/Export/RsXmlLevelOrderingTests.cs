using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     RS-XML is a TIME-ORDERED format, and a <c>ChartLevel</c>'s collections are
///     NOT sorted mid-session: <c>HighwayEditingService</c> runs
///     <c>ChartSpacingEnforcer</c> with <c>runCompatibilityCleanup: false</c>,
///     the branch that skips <c>SortByPosition</c>, and
///     <c>RefretService.RefretRange</c> used to splice regenerated anchors onto
///     the END of the list. <c>SngFromDomainMapper.ConvertLevel</c> has always
///     sorted its entities, anchors and hand shapes; <c>RsXmlSongWriter</c> wrote
///     the raw collection order, so the two artifacts built from one chart could
///     describe different things.
///     <para>
///         The anchors are the sharp case, because the disorder is not only an
///         ordering fault there: <c>BuildAnchorExports</c> collapses CONSECUTIVE
///         rows that restate the same fret AND width, so on an unsorted list it
///         compares the wrong pair — dropping an anchor the hand really needs and
///         keeping a duplicate — and asks the phrase-boundary question about the
///         wrong interval.
///     </para>
/// </summary>
public sealed class RsXmlLevelOrderingTests
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

    /// <summary>An arrangement whose level collections are in the order a
    /// mid-session edit leaves them: later content first.</summary>
    private static Arrangement OutOfOrderLevel()
    {
        var arrangement = new Arrangement
        { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "A5", Frets = { [1] = 7, [2] = 9, [3] = 9 } });
        arrangement.Phrases.AddRange(["COUNT", "riff"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };

        foreach (var beat in new[] { 12, 4, 8 })
        {
            level.Notes.Add(new GuitarNote
            { Position = new FractionalPosition(beat), StringIndex = 0, Fret = 5 });
        }

        foreach (var beat in new[] { 14, 6 })
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 9 });
            level.Chords.Add(chord);
        }

        foreach (var beat in new[] { 14, 6 })
        {
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(beat),
                EndPosition = new FractionalPosition(beat + 1),
                TemplateId = 0
            });
        }

        // The refret splice: the regenerated anchor lands after the ones it
        // belongs BEFORE. In this order the consecutive-pair rule compares
        // (12, fret 5) against (4, fret 5) and collapses one of them.
        level.Anchors.Add(new FretAnchor(new FractionalPosition(4), 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(12), 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 9));

        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static XElement Level(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return new RsXmlSongWriter().Write(project).Root!.Element("levels")!.Element("level")!;
    }

    /// <summary>RS-XML writes times as SECONDS with three decimals; read them
    /// back as milliseconds so the assertions read like the chart.</summary>
    private static int Ms(XElement el, string attribute) => (int)Math.Round(
        double.Parse(el.Attribute(attribute)!.Value, System.Globalization.CultureInfo.InvariantCulture) * 1000.0);

    private static List<int> Times(XElement level, string list, string element, string attribute = "time")
        => level.Element(list)!.Elements(element).Select(e => Ms(e, attribute)).ToList();

    [Fact]
    public void EveryLevelList_IsWrittenInAscendingTimeOrder()
    {
        var beats = Beats();
        var level = Level(OutOfOrderLevel(), beats);

        Assert.Equal([2000, 4000, 6000], Times(level, "notes", "note"));
        Assert.Equal([3000, 7000], Times(level, "chords", "chord"));
        Assert.Equal([3000, 7000], Times(level, "handShapes", "handShape", "startTime"));

        var anchorTimes = Times(level, "anchors", "anchor");
        Assert.Equal(anchorTimes.Order().ToList(), anchorTimes);
    }

    /// <summary>The anchor collapse is a CONSECUTIVE-pair rule, so the order it
    /// runs on decides which rows survive. Sorted, none of these three collapse:
    /// 5 / 9 / 5 alternates, and the 9 sits between the two 5s.</summary>
    [Fact]
    public void TheAnchorCollapse_JudgesNeighboursInTimeOrder()
    {
        var beats = Beats();
        var level = Level(OutOfOrderLevel(), beats);
        var anchors = level.Element("anchors")!.Elements("anchor")
            .Select(e => (Ms(e, "time"), (int)e.Attribute("fret")!)).ToList();

        Assert.Equal([(2000, 5), (4000, 9), (6000, 5)], anchors);
    }

    /// <summary>The SNG the GAME reads sorts already — so the claim that cannot
    /// rot is that the two artifacts built from one chart agree.</summary>
    [Fact]
    public void TheSngAndTheRsXml_PlaceTheSameAnchorsAtTheSameTimes()
    {
        var beats = Beats();
        var arrangement = OutOfOrderLevel();

        var xmlAnchors = LevelAnchors(Level(arrangement, beats));
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 30_000).Sng;
        var sngAnchors = sng.Levels[0].Anchors
            .Select(a => ((int)Math.Round(a.StartTime * 1000f), (int)a.Fret)).ToList();

        Assert.Equal(sngAnchors, xmlAnchors);
        return;

        static List<(int, int)> LevelAnchors(XElement level) => level.Element("anchors")!.Elements("anchor")
            .Select(e => (Ms(e, "time"), (int)e.Attribute("fret")!)).ToList();
    }
}
