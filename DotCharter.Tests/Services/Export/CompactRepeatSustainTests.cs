using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A chord that ships WITHOUT chord notes has no sustain to state, and the
///     SNG has to say so.
///     <para>
///         <c>&lt;chord&gt;</c> carries no sustain attribute — the length lives
///         entirely on the <c>&lt;chordNote&gt;</c> children, which a compact
///         repeat (and <c>Chord.ForceNoNotes</c>) deliberately drops. So the
///         RS-XML we write beside the psarc says nothing, and DLC Builder
///         building from that same XML computes 0:
///         <c>XmlToSngNote.fs</c> reads <c>chord.ChordNotes[0].Sustain</c> inside
///         <c>if chord.HasChordNotes</c> and yields <c>0.f</c> otherwise.
///     </para>
///     <para>
///         <c>SngFromDomainMapper</c> carried the dropped children's length into
///         the SNG anyway, so a psarc built here and one DLC Builder builds from
///         our own XML disagreed about how long the same strum rings — and the
///         one built here set <c>NoteMask.Sustain</c> on a chord the game draws
///         as a bare repeat placeholder. The compact-repeat DECISION was already
///         shared between the two writers; its consequence was not.
///     </para>
/// </summary>
public sealed class CompactRepeatSustainTests
{
    private static List<BeatMarker> Beats(int count = 16)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        return beats;
    }

    /// <summary>Four identical F chords under one hand shape, each ringing a
    /// full beat — the canonical repeat run, with a length to lose.</summary>
    private static ChartProject RepeatedRun()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement
        { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.ChordTemplates.Add(new ChordTemplate
        { Id = 1, Name = "F", Frets = { [0] = 1, [1] = 3, [2] = 3 } });
        arrangement.Phrases.AddRange(["COUNT", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var i = 0; i < 4; i++)
        {
            var chord = new Chord { Position = new FractionalPosition(i), TemplateId = 1 };
            foreach (var (stringIndex, fret) in new[] { (0, 1), (1, 3), (2, 3) })
            {
                chord.ChordNotes.Add(new ChordNote
                { StringIndex = stringIndex, Fret = fret, Sustain = new FractionalPosition(1) });
            }

            level.Chords.Add(chord);
        }

        level.HandShapes.Add(new HandShape
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(4),
            TemplateId = 1
        });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static List<RsNote> SngChords(ChartProject project) =>
        SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 8000).Sng
            .Levels[0].Notes
            .Where(n => (n.Mask & RsNoteMask.Chord) != 0)
            .OrderBy(n => n.Time)
            .ToList();

    private static List<XElement> XmlChords(ChartProject project) =>
        new RsXmlSongWriter().Write(project, null).Root!
            .Element("levels")!.Element("level")!.Element("chords")!.Elements("chord").ToList();

    /// <summary>The claim that cannot rot: the SNG's sustain matches what a
    /// reader of our own XML can see. A chord with children keeps its length;
    /// one without has none to keep.</summary>
    [Fact]
    public void EveryChordsSngSustain_MatchesWhatTheXmlStates()
    {
        var project = RepeatedRun();
        var sngChords = SngChords(project);
        var xmlChords = XmlChords(project);

        Assert.Equal(4, sngChords.Count);
        Assert.Equal(4, xmlChords.Count);

        for (var i = 0; i < sngChords.Count; i++)
        {
            var children = xmlChords[i].Elements("chordNote").ToList();
            if (children.Count == 0)
            {
                Assert.Equal(0f, sngChords[i].Sustain);
                Assert.True((sngChords[i].Mask & RsNoteMask.Sustain) == 0,
                    $"chord {i} ships as a bare repeat but claims a sustain");
                continue;
            }

            Assert.True(sngChords[i].Sustain > 0f, $"chord {i} kept its children but lost its length");
            Assert.True((sngChords[i].Mask & RsNoteMask.Sustain) != 0);
        }
    }

    /// <summary>Non-vacuity keeper: this fixture really does produce both cases
    /// — one full chord and three compacted repeats.</summary>
    [Fact]
    public void TheFixtureProducesBothCases()
    {
        var xmlChords = XmlChords(RepeatedRun());
        Assert.Equal(3, xmlChords[0].Elements("chordNote").Count());
        Assert.All(xmlChords.Skip(1), c => Assert.Empty(c.Elements("chordNote")));
    }

    /// <summary>`ForceNoNotes` reaches the same place by the charter's own hand,
    /// and must reach the same answer.</summary>
    [Fact]
    public void AForceNoNotesChord_AlsoShipsWithoutASustain()
    {
        var project = RepeatedRun();
        var level = project.Arrangements[0].Levels[0];
        level.Chords.RemoveAt(3);
        level.Chords.RemoveAt(2);
        level.Chords.RemoveAt(1);
        level.Chords[0].ForceNoNotes = true;

        var chord = Assert.Single(SngChords(project));
        Assert.Equal(0f, chord.Sustain);
        Assert.True((chord.Mask & RsNoteMask.Sustain) == 0);
    }
}
