using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>Muted chord cleanup updates strummed chord shapes and preserves authored grips.</summary>
public sealed class DdMutedHandShapeTests
{
    [Fact]
    public void Muted_chord_and_covering_shape_keep_mains_strings_1_and_2_at_every_generated_level()
    {
        var arr = DdXml.Read(Song(false));
        int[] mainPlayedStrings = [1, 2];

        DdImprover.ApplyAll(arr);
        DdGenerator.GenerateForArrangement(new DdGeneratorConfig(null, DdLevelCountMode.Constant, 3), arr);

        Assert.Equal(3, arr.Levels.Count);
        var chords = arr.Levels.SelectMany(l => l.Chords).ToList();
        Assert.NotEmpty(chords);
        Assert.Contains(arr.Levels.Take(2), l => l.Chords.Count > 0 && l.HandShapes.Count > 0);
        Assert.All(chords, c => Assert.Equal(mainPlayedStrings, Strings(arr.ChordTemplates[c.ChordId])));
        AssertHandShapeStrings(arr);
    }

    [Fact]
    public void A_shape_covering_muted_and_unmuted_grips_keeps_its_original_template_and_range()
    {
        var arr = DdXml.Read(Song(true));
        var shared = arr.ChordTemplates[0];

        DdImprover.ApplyAll(arr);

        Assert.Equal([0, 1, 2], Strings(shared));
        Assert.Equal(0, arr.Levels[0].Chords[1].ChordId);
        var shape = Assert.Single(arr.Levels[0].HandShapes);
        Assert.Equal(0, shape.ChordId);
        Assert.Equal(1_000, shape.StartTime);
        Assert.Equal(3_000, shape.EndTime);
    }

    [Fact]
    public void An_unaffected_own_chord_keeps_the_full_shape_even_when_its_note_details_are_partial()
    {
        var song = Song(false);
        AddChord(song, "2.000", 0, true);
        song.Descendants("chord").Last().Elements("chordNote").First().Remove();
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        Assert.Equal(0, Assert.Single(arr.Levels[0].HandShapes).ChordId);
        Assert.Equal(1, arr.Levels[0].Chords[0].ChordId);
        Assert.Equal(0, arr.Levels[0].Chords[1].ChordId);
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[0]));
        Assert.Equal([1, 2], arr.Levels[0].Chords[1].ChordNotes!.Select(n => n.String).Order().ToArray());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Arpeggio_and_foreign_shapes_keep_authored_template_zero_during_cleanup(bool arpeggio, bool ownTemplate)
    {
        const int authoredShapeTemplate = 0;
        int[] authoredGripStrings = [0, 1, 2, 3];
        var song = AuthoredGripSong(arpeggio, ownTemplate);
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        var shape = Assert.Single(arr.Levels[0].HandShapes);
        Assert.Equal(authoredShapeTemplate, shape.ChordId);
        Assert.Equal(authoredGripStrings, Strings(arr.ChordTemplates[shape.ChordId]));
        int[] playedStrings = ownTemplate ? [0, 1, 2] : [1, 2];
        Assert.Equal(playedStrings, Strings(arr.ChordTemplates[Assert.Single(arr.Levels[0].Chords).ChordId]));
    }

    [Fact]
    public void A_shared_arpeggio_keeps_template_zero_while_only_the_mixed_chord_is_rebound()
    {
        var arr = DdXml.Read(SharedOwnArpeggioSong());

        DdImprover.ApplyAll(arr);

        var level = arr.Levels[0];
        Assert.Equal(0, Assert.Single(level.HandShapes).ChordId);
        Assert.Equal([0, 1, 2, 3], Strings(arr.ChordTemplates[0]));
        Assert.Equal(1, level.Chords[0].ChordId);
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[1]));
        Assert.Equal(0, level.Chords[1].ChordId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identical_reduced_grips_are_deduplicated_only_when_source_attributes_match(bool differentSource)
    {
        var song = Song(false);
        var templates = song.Element("chordTemplates")!;
        var original = templates.Element("chordTemplate")!;
        original.SetAttributeValue("customGrip", "first");
        var copy = new XElement(original);
        if (differentSource) copy.SetAttributeValue("customGrip", "second");
        templates.Add(copy);
        templates.SetAttributeValue("count", "2");
        AddChord(song, "2.000", 1);
        var arr = DdXml.Read(song);
        var sources = arr.ChordTemplates.Select(t => t.Source!.ToString()).ToArray();

        DdImprover.ApplyAll(arr);

        Assert.Equal(differentSource ? 4 : 3, arr.ChordTemplates.Count);
        Assert.Equal(2, arr.Levels[0].Chords[0].ChordId);
        Assert.Equal(differentSource ? 3 : 2, arr.Levels[0].Chords[1].ChordId);
        for (var i = 0; i < sources.Length; i++)
        {
            Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[i]));
            Assert.Equal(sources[i], arr.ChordTemplates[i].Source!.ToString());
        }
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(1, 7, 1)]
    public void A_covered_note_on_a_pruned_string_blocks_shape_rebinding(int noteString, int fret, int expectedTemplate)
    {
        var song = Song(false);
        var notes = song.Descendants("notes").Single();
        notes.Add(new XElement("note", new XAttribute("time", "1.500"),
            new XAttribute("string", noteString), new XAttribute("fret", fret)));
        notes.SetAttributeValue("count", "1");
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        Assert.Equal(expectedTemplate, Assert.Single(arr.Levels[0].HandShapes).ChordId);
    }

    [Fact]
    public void An_inferred_arpeggio_keeps_its_original_template()
    {
        var song = Song(false);
        var notes = song.Descendants("notes").Single();
        foreach (var (time, noteString) in new[] { ("1.500", 1), ("2.000", 2) })
        {
            notes.Add(new XElement("note", new XAttribute("time", time),
                new XAttribute("string", noteString), new XAttribute("fret", "5")));
        }

        notes.SetAttributeValue("count", "2");
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        Assert.Equal(0, Assert.Single(arr.Levels[0].HandShapes).ChordId);
        Assert.Equal(1, Assert.Single(arr.Levels[0].Chords).ChordId);
    }

    [Theory]
    [InlineData("1.000", 0)]
    [InlineData("0.250", 1)]
    public void A_note_sustaining_into_the_shape_blocks_pruning_its_string(string sustain, int expectedTemplate)
    {
        var song = Song(false);
        var notes = song.Descendants("notes").Single();
        notes.Add(new XElement("note", new XAttribute("time", "0.500"),
            new XAttribute("string", "0"), new XAttribute("fret", "5"), new XAttribute("sustain", sustain)));
        notes.SetAttributeValue("count", "1");
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        Assert.Equal(expectedTemplate, Assert.Single(arr.Levels[0].HandShapes).ChordId);
    }

    [Fact]
    public void A_covered_foreign_chord_blocks_shape_rebinding()
    {
        var song = Song(false);
        var templates = song.Element("chordTemplates")!;
        templates.Add(new XElement(templates.Element("chordTemplate")!));
        templates.SetAttributeValue("count", "2");
        AddChord(song, "2.000", 1);
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        Assert.Equal(0, Assert.Single(arr.Levels[0].HandShapes).ChordId);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Multiple_reduced_grips_keep_one_shape_using_a_candidate_only_if_it_covers_all_sounds(bool compatible, int expectedTemplate)
    {
        var song = Song(true);
        var chords = song.Descendants("chords").Single();
        chords.Elements("chord").ElementAt(1).Remove();
        chords.SetAttributeValue("count", "2");
        if (compatible) chords.Elements("chord").Last().Elements("chordNote").First().SetAttributeValue("mute", "1");
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);

        var shape = Assert.Single(arr.Levels[0].HandShapes);
        Assert.Equal(expectedTemplate, shape.ChordId);
        Assert.Equal(1_000, shape.StartTime);
        Assert.Equal(3_000, shape.EndTime);
    }

    internal static ChartProject SharedOwnArpeggioChart() =>
        new RsXmlSongReader().ReadDocument(new XDocument(SharedOwnArpeggioSong()), "shared-arpeggio.xml");

    internal static ChartProject AuthoredArpeggioChart() =>
        new RsXmlSongReader().ReadDocument(new XDocument(AuthoredGripSong(true, false)), "authored-arpeggio.xml");

    internal static ChartProject MutedChart() =>
        new RsXmlSongReader().ReadDocument(new XDocument(Song(true)), "muted-hand-shapes.xml");

    internal static void AssertHandShapeStrings(DdArrangement arr)
    {
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords)
            {
                var strings = chord.HasChordNotes
                    ? chord.ChordNotes!.Select(n => n.String).Order().ToArray()
                    : Strings(arr.ChordTemplates[chord.ChordId]);
                var covering = level.HandShapes.Where(h => h.StartTime <= chord.Time && chord.Time < h.EndTime).ToList();
                Assert.NotEmpty(covering);
                foreach (var shape in covering)
                {
                    Assert.True(strings.SequenceEqual(Strings(arr.ChordTemplates[shape.ChordId])),
                        $"Level {level.Difficulty}, chord {chord.Time}: played [{string.Join(',', strings)}], shape [{string.Join(',', Strings(arr.ChordTemplates[shape.ChordId]))}]");
                }
            }
        }
    }

    private static int[] Strings(DdChordTemplate template) =>
        [.. Enumerable.Range(0, template.Frets.Length).Where(s => template.Frets[s] >= 0)];

    private static XElement SharedOwnArpeggioSong()
    {
        var song = AuthoredGripSong(true, true);
        AddChord(song, "2.000", 0, true);
        return song;
    }

    private static void AddChord(XElement song, string time, int templateId, bool unmuted = false)
    {
        var chords = song.Descendants("chords").Single();
        var chord = new XElement(chords.Elements("chord").First());
        chord.SetAttributeValue("time", time);
        chord.SetAttributeValue("chordId", templateId);
        foreach (var note in chord.Elements("chordNote"))
        {
            note.SetAttributeValue("time", time);
            if (unmuted) note.SetAttributeValue("mute", "0");
        }

        chords.Add(chord);
        chords.SetAttributeValue("count", chords.Elements("chord").Count());
    }

    private static XElement AuthoredGripSong(bool arpeggio, bool ownTemplate)
    {
        var song = Song(false);
        var templates = song.Element("chordTemplates")!;
        var grip = templates.Element("chordTemplate")!;
        grip.SetAttributeValue("displayName", arpeggio ? "C-arp" : "C");
        grip.SetAttributeValue("fret3", "5");
        grip.SetAttributeValue("finger3", "1");
        var chord = song.Descendants("chord").Single();
        if (!ownTemplate)
        {
            var played = new XElement(grip);
            played.SetAttributeValue("displayName", "C");
            played.SetAttributeValue("fret0", "-1");
            played.SetAttributeValue("finger0", "-1");
            templates.Add(played);
            templates.SetAttributeValue("count", "2");
            chord.SetAttributeValue("chordId", "1");
            chord.Elements("chordNote").First().Remove();
        }
        else
        {
            chord.Elements("chordNote").First().SetAttributeValue("mute", "0");
        }

        chord.Add(new XElement("chordNote", new XAttribute("time", "1.000"),
            new XAttribute("string", "3"), new XAttribute("fret", "5"), new XAttribute("mute", "1")));
        var notes = song.Descendants("notes").Single();
        notes.SetAttributeValue("count", "2");
        foreach (var time in new[] { "0.500", "1.500" })
            notes.Add(new XElement("note", new XAttribute("time", time), new XAttribute("string", "0"), new XAttribute("fret", "5")));
        song.Descendants("handShape").Single().SetAttributeValue("startTime", "0.500");
        return song;
    }

    private static XElement Song(bool mixed)
    {
        var song = XElement.Parse("""
            <song>
              <songLength>4.000</songLength><averageTempo>120</averageTempo>
              <phrases count="2"><phrase name="one" maxDifficulty="0" /><phrase name="END" maxDifficulty="0" /></phrases>
              <phraseIterations count="2"><phraseIteration time="0.000" phraseId="0" /><phraseIteration time="3.500" phraseId="1" /></phraseIterations>
              <chordTemplates count="1"><chordTemplate chordName="C" displayName="C" fret0="5" fret1="5" fret2="5" fret3="-1" fret4="-1" fret5="-1" finger0="1" finger1="1" finger2="1" finger3="-1" finger4="-1" finger5="-1" /></chordTemplates>
              <ebeats count="9">
                <ebeat time="0.000" measure="1" /><ebeat time="0.500" /><ebeat time="1.000" /><ebeat time="1.500" />
                <ebeat time="2.000" measure="2" /><ebeat time="2.500" /><ebeat time="3.000" /><ebeat time="3.500" /><ebeat time="4.000" measure="3" />
              </ebeats>
              <levels count="1"><level difficulty="0">
                <notes count="0" /><fretHandMutes count="0" />
                <chords count="1"><chord time="1.000" chordId="0"><chordNote time="1.000" string="0" fret="5" mute="1" /><chordNote time="1.000" string="1" fret="5" /><chordNote time="1.000" string="2" fret="5" /></chord></chords>
                <anchors count="1"><anchor time="0.000" fret="5" width="4.000" /></anchors>
                <handShapes count="1"><handShape chordId="0" startTime="1.000" endTime="3.000" /></handShapes>
              </level></levels>
            </song>
            """);
        if (!mixed) return song;
        var chords = song.Descendants("chords").Single();
        var original = chords.Element("chord")!;
        foreach (var (time, mutedString) in new[] { ("2.000", -1), ("2.500", 2) })
        {
            var chord = new XElement(original);
            chord.SetAttributeValue("time", time);
            foreach (var note in chord.Elements("chordNote"))
            {
                note.SetAttributeValue("time", time);
                note.SetAttributeValue("mute", (int)note.Attribute("string")! == mutedString ? "1" : "0");
            }

            chords.Add(chord);
        }

        chords.SetAttributeValue("count", "3");
        return song;
    }
}
