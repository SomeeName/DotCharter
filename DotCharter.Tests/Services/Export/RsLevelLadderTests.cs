using System.Xml.Linq;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Rocksmith files store the difficulty ladder SPARSE (a level carries only
///     the phrases whose max reaches it); the chart stores it DENSE (every level
///     is the whole song), so the hardest level is the whole song at its
///     hardest. <see cref="RsLevelLadder" /> converts on import and back on
///     export (#323). These pin both directions and the round trip.
/// </summary>
public sealed class RsLevelLadderTests
{
    // COUNT (max 0) @0, riff (max 2) @4 and @8, outro (max 1) @12.
    private static ChartProject SparseProject()
    {
        var project = new ChartProject();
        project.Chart.SongName = "Ladder";
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.Phrases.AddRange(["COUNT", "riff", "outro"]);
        arr.PhraseInfos["COUNT"] = new PhraseInfo(0, false);
        arr.PhraseInfos["riff"] = new PhraseInfo(2, false);
        arr.PhraseInfos["outro"] = new PhraseInfo(1, false);
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 2));

        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[4] = 0;
        template.Frets[3] = 2;
        arr.ChordTemplates.Add(template);

        var level0 = new ChartLevel { DifficultyIndex = 0 };
        foreach (var beat in new[] { 1, 5, 9, 13 }) level0.Notes.Add(Note(beat, 1));
        foreach (var beat in new[] { 0, 4, 8, 12 }) level0.Anchors.Add(new FretAnchor(new FractionalPosition(beat), 1));

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        foreach (var beat in new[] { 5, 6, 9, 10, 13 }) level1.Notes.Add(Note(beat, 2));
        level1.Chords.Add(new Chord
        {
            Position = new FractionalPosition(14),
            TemplateId = 0,
            ChordNotes =
            [
                new ChordNote { StringIndex = 3, Fret = 2 },
                new ChordNote { StringIndex = 4, Fret = 0 }
            ]
        });
        level1.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(14),
            EndPosition = new FractionalPosition(15),
            TemplateId = 0,
            Name = "A5"
        });
        foreach (var beat in new[] { 4, 8, 12 }) level1.Anchors.Add(new FretAnchor(new FractionalPosition(beat), 2));

        var level2 = new ChartLevel { DifficultyIndex = 2 };
        foreach (var beat in new[] { 5, 6, 7, 9, 10, 11 }) level2.Notes.Add(Note(beat, 3));
        foreach (var beat in new[] { 4, 8 }) level2.Anchors.Add(new FretAnchor(new FractionalPosition(beat), 3));

        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Levels.Add(level2);
        project.Arrangements.Add(arr);
        return project;
    }

    private static GuitarNote Note(int beat, int fret) =>
        new() { Position = new FractionalPosition(beat), Fret = fret, StringIndex = 0 };

    private static int Sounds(ChartLevel level) => level.Notes.Count + level.Chords.Count;

    [Fact]
    public void Import_fills_each_level_above_a_phrases_max_with_that_phrases_max_level()
    {
        var arr = SparseProject().Arrangements[0];

        var copied = RsLevelLadder.FillInheritedPhrases(arr);

        // Level 1 gains COUNT from level 0 (one note + one anchor). Level 2 gains
        // COUNT from level 0 and the outro from level 1 (one note, the chord, its
        // hand shape and its anchor).
        Assert.Equal(2 + 2 + 4, copied);
        Assert.Equal([1, 2, 2, 2, 2, 2], arr.Levels[1].Notes.Select(n => n.Fret));
        Assert.Equal([1, 3, 3, 3, 3, 3, 3, 2], arr.Levels[2].Notes.Select(n => n.Fret));
        Assert.Single(arr.Levels[2].Chords);
        Assert.Single(arr.Levels[2].HandShapes);
        Assert.Equal([1, 3, 3, 2], arr.Levels[2].Anchors.Select(a => a.Fret));

        // The hardest level is now the whole song at its hardest.
        Assert.Same(arr.Levels[2], ChartLevels.Hardest(arr));
        Assert.Equal(0, RsLevelLadder.FillInheritedPhrases(arr)); // already dense: nothing to fill
    }

    [Fact]
    public void Export_takes_the_copies_back_out_and_keeps_every_authored_max()
    {
        var sparse = SparseProject().Arrangements[0];
        var original = sparse.Levels.Select(l => l.Notes.Concat<object>(l.Chords).Concat(l.Anchors)
            .Concat(l.HandShapes).ToList()).ToList();

        RsLevelLadder.FillInheritedPhrases(sparse);
        var export = RsLevelLadder.ForExport(sparse);

        for (var i = 0; i < original.Count; i++)
        {
            var level = export.Levels[i];
            var written = level.Notes.Concat<object>(level.Chords).Concat(level.Anchors).Concat(level.HandShapes)
                .ToList();
            // The SAME instances the file had — the copies are what was dropped.
            Assert.Equal(original[i].Count, written.Count);
            Assert.All(written, item => Assert.Contains(item, original[i]));
        }

        Assert.Equal(0, export.ShippedMaxDifficulty["COUNT"]);
        Assert.Equal(1, export.ShippedMaxDifficulty["outro"]);
        Assert.False(export.ShippedMaxDifficulty.ContainsKey("riff")); // already at the top
    }

    /// <summary>A charter who edits a phrase on the hardest level, above the
    /// phrase's authored max, must see that edit in the game: the phrase ships
    /// with its max raised, and every level up to it keeps its content (a level
    /// left empty below the max would be a silent phrase in game).</summary>
    [Fact]
    public void An_edit_above_a_phrases_max_raises_it_and_keeps_every_level_up_to_it()
    {
        var arr = SparseProject().Arrangements[0];
        RsLevelLadder.FillInheritedPhrases(arr);

        arr.Levels[2].Notes.Single(n => n.Position == new FractionalPosition(13)).Fret = 5;
        var export = RsLevelLadder.ForExport(arr);

        Assert.Equal(2, export.ShippedMaxDifficulty["outro"]);
        // Level 2 ships the riff and the (edited) outro; COUNT, unedited, stays out.
        Assert.Equal(6 + 2, Sounds(export.Levels[2]));
        Assert.Contains(export.Levels[2].Notes, n => n.Fret == 5);
        Assert.Contains(export.Levels[2].Chords, c => c.Position == new FractionalPosition(14));
        // COUNT was not edited: still left out above its max.
        Assert.DoesNotContain(export.Levels[2].Notes, n => n.Fret == 1);
    }

    [Fact]
    public void A_sparse_chart_a_generated_ladder_and_a_single_level_export_unchanged()
    {
        var sparse = SparseProject().Arrangements[0];
        Assert.Equal(sparse.Levels, RsLevelLadder.ForExport(sparse).Levels); // same instances

        var generated = SparseProject().Arrangements[0];
        foreach (var info in generated.PhraseInfos.Values) info.MaxDifficulty = 2;
        RsLevelLadder.FillInheritedPhrases(generated);
        Assert.Equal(generated.Levels, RsLevelLadder.ForExport(generated).Levels);

        var single = SparseProject().Arrangements[0];
        single.Levels.RemoveAt(2);
        single.Levels.RemoveAt(1);
        Assert.Equal(0, RsLevelLadder.FillInheritedPhrases(single));
        Assert.Equal(single.Levels, RsLevelLadder.ForExport(single).Levels);
    }

    /// <summary>The RS-XML path end to end: a sparse file read (filled) and
    /// written again says exactly what it said — every level, every phrase max.</summary>
    [Fact]
    public void An_rs_xml_ladder_survives_read_and_write_unchanged()
    {
        var writer = new RsXmlSongWriter();
        var first = writer.Write(SparseProject());

        var imported = new RsXmlSongReader().ReadDocument(first, "ladder.xml");
        var arr = imported.Arrangements[0];
        Assert.Equal(3, arr.Levels.Count);
        Assert.Equal(1 + 6 + 2, Sounds(ChartLevels.Hardest(arr)!)); // whole song on top

        var second = writer.Write(imported);

        Assert.Equal(Element(first, "levels"), Element(second, "levels"));
        Assert.Equal(Element(first, "phrases"), Element(second, "phrases"));
    }

    private static string Element(XDocument doc, string name) => doc.Root!.Element(name)!.ToString();
}
