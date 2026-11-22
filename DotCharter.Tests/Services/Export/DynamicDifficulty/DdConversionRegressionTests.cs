using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>Difficulty conversion preserves chord grips, ignore flags and authored mute events and names.</summary>
public sealed class DdConversionRegressionTests
{
    [Theory]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(6, false)]
    public void Reduced_chord_notes_play_the_same_strings_as_the_reduced_template(int slots, bool treble)
    {
        var start = treble ? slots - 3 : 0;
        var template = Template(slots, start);
        var chord = Chord(1000, start);
        var templates = new List<DdChordTemplate> { template };

        var chosen = Choose(chord, templates, 0.5);
        var reduced = Assert.IsType<DdChord>(chosen.Chord);
        var chordNotes = reduced.ChordNotes!;
        int[] expected = treble ? [slots - 2, slots - 1] : [0, 1];

        Assert.Equal(expected, chordNotes.Select(n => n.String));
        Assert.Equal(expected, Enumerable.Range(0, slots).Where(s => templates[reduced.ChordId].Frets[s] >= 0));
        Assert.All(chordNotes, n => Assert.Equal(templates[reduced.ChordId].Frets[n.String], n.Fret));
        Assert.Equal(3, chord.ChordNotes!.Count);
        Assert.Equal(3, DdTemplates.NoteCount(template));
    }

    [Fact]
    public void Pruning_a_linked_treble_string_also_removes_its_following_note()
    {
        var chord = Chord(1000, 3);
        chord.IsLinkNext = true;
        chord.ChordNotes![0].IsLinkNext = true;
        var next = new DdNote { Time = 1500, String = 3, Fret = 5 };

        var chosen = DdEntityChooser.Choose(0.5,
            new Dictionary<int, DdDifficultyRange> { [0] = new(0, 0) },
            new Dictionary<int, int> { [1000] = 0, [1500] = 0 },
            new Dictionary<int, int> { [0] = 2 },
            [Template(6, 3)], [], 3, 6, [DdEntity.Of(chord), DdEntity.Of(next)]);

        Assert.Single(chosen);
        Assert.DoesNotContain(chosen, c => c.Entity.Note?.String == 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_note_generated_from_an_ignored_chord_remains_ignored_in_XML(bool withChordNotes)
    {
        var chord = Chord(1000, 0);
        chord.IsIgnore = true;
        if (!withChordNotes) chord.ChordNotes = null;
        var arr = Arrangement();
        arr.ChordTemplates.Add(Template(6, 0));

        var note = Assert.IsType<DdNote>(Choose(chord, arr.ChordTemplates, 0.2).Note);
        arr.Levels.Single().Notes.Clear();
        arr.Levels.Single().Notes.Add(note);
        var song = new XElement("song");
        DdXml.Write(song, arr);

        Assert.True(note.IsIgnore);
        Assert.Equal("1", song.Descendants("note").Single().Attribute("ignore")?.Value);
    }

    [Fact]
    public void Muted_chord_cleanup_rebinds_every_affected_chord_without_changing_shared_grips()
    {
        var arr = Arrangement();
        var original = Template(6, 0);
        arr.ChordTemplates.Add(original);
        var first = Chord(1000, 0, 0);
        var unmuted = Chord(1500, 0);
        var repeat = Chord(2000, 0, 0);
        var differentMute = Chord(2500, 0, 2);
        var allMuted = Chord(3000, 0);
        foreach (var note in allMuted.ChordNotes!) note.Mask = DdNoteMask.FretHandMute;
        var laterLevel = Chord(2000, 0, 1);
        arr.Levels.Single().Chords.AddRange([first, unmuted, repeat, differentMute, allMuted]);
        arr.Levels.Add(new DdLevel(1) { Chords = [laterLevel] });

        DdImprover.ApplyAll(arr);

        Assert.Equal(new[] { 5, 5, 5, -1, -1, -1 }, original.Frets);
        Assert.Equal(new[] { 1, 1, 1, -1, -1, -1 }, original.Fingers);
        Assert.Equal(0, unmuted.ChordId);
        Assert.Equal(0, allMuted.ChordId);
        Assert.Equal(3, allMuted.ChordNotes!.Count);
        foreach (var chord in new[] { first, repeat, differentMute, laterLevel })
        {
            Assert.NotEqual(0, chord.ChordId);
            var notes = chord.ChordNotes!;
            var template = arr.ChordTemplates[chord.ChordId];
            Assert.Equal(2, notes.Count);
            Assert.DoesNotContain(notes, n => n.IsFretHandMute);
            Assert.Equal(notes.Select(n => n.String), Enumerable.Range(0, 6).Where(s => template.Frets[s] >= 0));
            Assert.Equal(original.Name, template.Name);
            Assert.Equal(original.DisplayName, template.DisplayName);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void XML_conversion_keeps_fret_hand_mutes_with_and_without_fixes_and_generation(bool improve, bool generate)
    {
        var song = Song();
        var mutes = song.Descendants("fretHandMutes").Single();
        mutes.SetAttributeValue("count", "3");
        mutes.Add(XElement.Parse("""<fretHandMute time="0.250" custom="kept" />"""),
            XElement.Parse("""<fretHandMute time="2.000" />"""),
            XElement.Parse("""<fretHandMute time="3.500" />"""));
        var expected = mutes.ToString(SaveOptions.DisableFormatting);
        var arr = DdXml.Read(song);

        if (improve) DdImprover.ApplyAll(arr);
        if (generate) DdGenerator.GenerateForArrangement(new DdGeneratorConfig(null, DdLevelCountMode.Constant, 3), arr);
        DdXml.Write(song, arr);

        Assert.Equal(generate ? 3 : 1, arr.Levels.Count);
        Assert.All(song.Descendants("level"), level =>
            Assert.Equal(expected, level.Element("fretHandMutes")!.ToString(SaveOptions.DisableFormatting)));
    }

    [Fact]
    public void XML_conversion_keeps_each_authored_levels_own_mutes()
    {
        var song = Song();
        var first = song.Descendants("level").Single();
        first.Element("fretHandMutes")!.ReplaceWith(XElement.Parse("""<fretHandMutes count="1"><fretHandMute time="1.000" /></fretHandMutes>"""));
        var second = new XElement(first);
        second.SetAttributeValue("difficulty", "1");
        second.Element("fretHandMutes")!.Element("fretHandMute")!.SetAttributeValue("time", "2.000");
        first.AddAfterSelf(second);
        var arr = DdXml.Read(song);

        DdImprover.ApplyAll(arr);
        DdXml.Write(song, arr);

        Assert.Equal(new[] { "1.000", "2.000" }, song.Descendants("fretHandMute").Select(m => m.Attribute("time")!.Value));
    }

    [Theory]
    [InlineData("Dominant7", "Dominant7", "Dominant7")]
    [InlineData("MyCONVChord", "MyCONVChord", "MyCONVChord")]
    [InlineData("C-arp-bridge", "C-arp-bridge", "C-arp-bridge")]
    [InlineData("C-nop-bridge", "C-nop-bridge", "C-nop-bridge")]
    [InlineData("Cminaddendum", "Cminaddendum", "Cminaddendum")]
    [InlineData("Cmin", "Cm", "Cm")]
    [InlineData("F#min7", "F#m7", "F#m7")]
    [InlineData("Bbmin7/F", "Bbm7/F", "Bbm7/F")]
    [InlineData("Cmin7b5", "Cm7b5", "Cm7b5")]
    [InlineData("Cminmaj7", "Cmmaj7", "Cmmaj7")]
    [InlineData("Cmin(maj7)", "Cm(maj7)", "Cm(maj7)")]
    [InlineData("CminCONV", "Cm", "Cm-arp")]
    [InlineData("Cmin-arp", "Cm", "Cm-arp")]
    [InlineData("Cmin-nop", "Cm", "Cm-nop")]
    [InlineData("Cmin-nop-arp", "Cm", "Cm-nop-arp")]
    [InlineData("   ", "", "")]
    public void Chord_name_normalization_changes_only_recognized_tokens(string name, string expectedName, string expectedDisplay)
    {
        var arr = Arrangement();
        arr.ChordTemplates.Add(new DdChordTemplate(name, name, [1, 1, 1, -1, -1, -1], [5, 5, 5, -1, -1, -1]));

        DdImprover.ApplyAll(arr);

        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(expectedName, template.Name);
        Assert.Equal(expectedDisplay, template.DisplayName);
    }

    private static DdEntity Choose(DdChord chord, List<DdChordTemplate> templates, double percent)
    {
        var chosen = Assert.Single(DdEntityChooser.Choose(percent,
            new Dictionary<int, DdDifficultyRange> { [0] = new(0, 0) },
            new Dictionary<int, int> { [chord.Time] = 0 },
            new Dictionary<int, int> { [0] = 1 },
            templates, [], 3, templates[0].Frets.Length, [DdEntity.Of(chord)]));
        if (chosen.Request != null) new DdTemplateRequestApplier(templates).Apply(chosen.Request);
        return chosen.Entity;
    }

    private static DdChordTemplate Template(int slots, int start)
    {
        var frets = Enumerable.Repeat(-1, slots).ToArray();
        var fingers = Enumerable.Repeat(-1, slots).ToArray();
        for (var s = start; s < start + 3; s++)
        {
            frets[s] = 5;
            fingers[s] = 1;
        }

        return new DdChordTemplate("C", "C", fingers, frets);
    }

    private static DdChord Chord(int time, int start, int mutedString = -1) => new()
    {
        Time = time,
        ChordId = 0,
        ChordNotes = [.. Enumerable.Range(start, 3).Select(s => new DdNote
        {
            Time = time,
            String = s,
            Fret = 5,
            LeftHand = 1,
            Mask = s == mutedString ? DdNoteMask.FretHandMute : DdNoteMask.None,
        })],
    };

    private static DdArrangement Arrangement() => DdXml.Read(Song());

    private static XElement Song() => XElement.Parse("""
        <song>
          <songLength>4.000</songLength>
          <averageTempo>120</averageTempo>
          <phrases count="2"><phrase name="one" maxDifficulty="0" /><phrase name="empty" maxDifficulty="0" /></phrases>
          <phraseIterations count="2"><phraseIteration time="0.000" phraseId="0" /><phraseIteration time="3.000" phraseId="1" /></phraseIterations>
          <ebeats count="9">
            <ebeat time="0.000" measure="1" /><ebeat time="0.500" /><ebeat time="1.000" /><ebeat time="1.500" />
            <ebeat time="2.000" measure="2" /><ebeat time="2.500" /><ebeat time="3.000" /><ebeat time="3.500" /><ebeat time="4.000" measure="3" />
          </ebeats>
          <levels count="1"><level difficulty="0">
            <notes count="3"><note time="1.000" string="0" fret="5" /><note time="1.500" string="0" fret="7" /><note time="2.500" string="0" fret="9" /></notes>
            <chords count="0" /><fretHandMutes count="0" /><anchors count="1"><anchor time="0.000" fret="5" width="4.000" /></anchors><handShapes count="0" />
          </level></levels>
        </song>
        """);
}
