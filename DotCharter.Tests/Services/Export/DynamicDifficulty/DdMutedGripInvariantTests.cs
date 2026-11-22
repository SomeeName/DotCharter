using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     #636: the muted-string cleanup never edits a shared chord template, and the
///     difficulty generator reduces a hand shape together with the reduced chords it covers.
///     One invariant runs over every dynamic-difficulty fixture and the review charts.
/// </summary>
public sealed class DdMutedGripInvariantTests
{
    public static TheoryData<string> Cases => [.. CaseNames()];

    public static TheoryData<string> ReviewCases => [.. ReviewSongs().Select(song => song.Name)];

    private static IEnumerable<string> CaseNames() =>
        DdGoldenParityTests.Names.Select(name => "dd/" + name).Concat(ReviewSongs().Select(song => song.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_level_keeps_grips_that_cover_what_is_played(string name)
    {
        var arr = Generated(name);

        var violations = Violations(arr);

        Assert.True(violations.Count == 0,
            $"{name}: {violations.Count} violation(s)\n{string.Join('\n', violations.Take(12))}");
    }

    [Theory]
    [MemberData(nameof(ReviewCases))]
    public void On_the_review_charts_every_shape_covers_every_chord_and_note_under_it(string name)
    {
        var mismatches = ShapeMismatches(Generated(name), reducedChordsOnly: false);

        Assert.True(mismatches.Count == 0, $"{name}:\n{string.Join('\n', mismatches.Take(12))}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Row_1_a_later_unmuted_chord_keeps_the_whole_shared_template(bool laterHasChordNotes)
    {
        var arr = DdXml.Read(Row1(laterHasChordNotes));

        DdImprover.ApplyAll(arr);

        var level = Assert.Single(arr.Levels);
        Assert.Equal([0, 1, 2, 3], Strings(arr.ChordTemplates[0]));
        Assert.Equal(0, level.Chords[1].ChordId);
        Assert.Equal(0, level.HandShapes[1].ChordId);
        Assert.NotEqual(0, level.Chords[0].ChordId);
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[level.Chords[0].ChordId]));
        Assert.Equal([0, 1, 2], level.Chords[0].ChordNotes!.Select(n => n.String));
        Assert.Equal(level.Chords[0].ChordId, level.HandShapes[0].ChordId);
        Assert.Empty(Violations(arr));
    }

    [Fact]
    public void Row_2_two_chords_with_different_muted_strings_each_get_their_own_reduced_grip()
    {
        var arr = DdXml.Read(Row2());

        DdImprover.ApplyAll(arr);

        var level = Assert.Single(arr.Levels);
        Assert.Equal([0, 1, 2, 3], Strings(arr.ChordTemplates[0]));
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[level.Chords[0].ChordId]));
        Assert.Equal([1, 2, 3], Strings(arr.ChordTemplates[level.Chords[1].ChordId]));
        Assert.All(level.Chords, chord => Assert.DoesNotContain(chord.ChordNotes!, n => n.IsFretHandMute));
        Assert.Equal([1, 2, 3], level.Chords[1].ChordNotes!.Select(n => n.String));
        Assert.Empty(Violations(arr));
    }

    [Fact]
    public void An_arpeggio_grip_over_a_muted_chord_is_reduced_with_that_chord_at_every_generated_level()
    {
        var arr = Generated("review/arpeggio-grip-over-muted-chord");

        Assert.True(arr.Levels.Count > 1);
        var partial = arr.Levels.Take(arr.Levels.Count - 1)
            .Where(level => level.HandShapes.Exists(shape => shape.StartTime == 0) && level.Chords.Exists(chord => chord.Time == 0))
            .ToList();
        Assert.NotEmpty(partial);
        foreach (var level in arr.Levels)
        {
            if (level.Chords.Find(c => c.Time == 0) is not { } chord) continue;
            Assert.Equal([1, 2], chord.ChordNotes!.Select(n => n.String));
            Assert.Equal([1, 2], Strings(arr.ChordTemplates[chord.ChordId]));
        }

        foreach (var level in partial)
        {
            var shape = level.HandShapes.Single(s => s.StartTime == 0);
            Assert.Equal([1, 2], Strings(arr.ChordTemplates[shape.ChordId]));
        }

        // The authored arpeggio grip is the hardest level's, untouched.
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[arr.Levels[^1].HandShapes.Single(s => s.StartTime == 0).ChordId]));
        Assert.Equal([0, 1, 2], Strings(arr.ChordTemplates[0]));
        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     What must hold at every level of an improved (and generated) arrangement:
    ///     no chord mixes muted and played chord notes; a chord's box holds every string its
    ///     chord notes play and, where the hardest level's does, no string they do not; a
    ///     hand shape keeps every string of its full grip that a chord on a reduced grip
    ///     (<see cref="DdChordTemplate.ReducedFrom" />) sounds under it.
    /// </summary>
    internal static List<string> Violations(DdArrangement arr)
    {
        var violations = new List<string>();
        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.HasChordNotes))
            {
                var notes = chord.ChordNotes!;
                var box = Strings(arr.ChordTemplates[chord.ChordId]);
                var played = notes.Select(n => n.String).ToArray();
                if (!chord.IsFretHandMute && notes.Exists(n => n.IsFretHandMute) && notes.Exists(n => !n.IsFretHandMute))
                    violations.Add($"level {level.Difficulty} chord {chord.Time}: muted and played chord notes [{string.Join(',', played)}]");
                if (played.Except(box).Any())
                    violations.Add($"level {level.Difficulty} chord {chord.Time}: plays [{string.Join(',', played)}], box [{string.Join(',', box)}]");
                var hardest = arr.Levels.Select(l => l.Chords.Find(c => c.Time == chord.Time)).Last(c => c != null)!;
                var hardestIsExact = hardest.HasChordNotes &&
                                     !Strings(arr.ChordTemplates[hardest.ChordId]).Except(hardest.ChordNotes!.Select(n => n.String)).Any();
                if (hardestIsExact && box.Except(played).Any())
                    violations.Add($"level {level.Difficulty} chord {chord.Time}: box [{string.Join(',', box)}] holds a string nobody plays [{string.Join(',', played)}]");
            }
        }

        violations.AddRange(ShapeMismatches(arr, reducedChordsOnly: true));
        return violations;
    }

    /// <summary>
    ///     Every hand shape whose grip lacks a string of its FULL grip (the same range at
    ///     the highest level that has it) that a chord or note under it sounds at that level.
    ///     With <paramref name="reducedChordsOnly" />, only chords on a reduced grip count:
    ///     DLC Builder narrows shape and sound independently, its fixtures hold shapes that
    ///     lack a sounded string, and the golden parity pins those.
    /// </summary>
    internal static List<string> ShapeMismatches(DdArrangement arr, bool reducedChordsOnly)
    {
        var mismatches = new List<string>();
        foreach (var level in arr.Levels)
        {
            foreach (var shape in level.HandShapes)
            {
                var grip = Strings(arr.ChordTemplates[shape.ChordId]);
                var full = arr.Levels.Select(l => l.HandShapes.Find(s => s.StartTime == shape.StartTime && s.EndTime == shape.EndTime))
                    .Last(s => s != null)!;
                var fullGrip = Strings(arr.ChordTemplates[full.ChordId]);
                foreach (var chord in level.Chords.Where(c => c.Time >= shape.StartTime && c.Time < shape.EndTime))
                {
                    if (reducedChordsOnly && arr.ChordTemplates[chord.ChordId].ReducedFrom == null) continue;
                    var sounded = chord.HasChordNotes
                        ? chord.ChordNotes!.Select(n => n.String).ToArray()
                        : Strings(arr.ChordTemplates[chord.ChordId]);
                    var lost = sounded.Intersect(fullGrip).Except(grip).ToArray();
                    if (lost.Length > 0)
                        mismatches.Add($"level {level.Difficulty} shape {shape.StartTime}: grip [{string.Join(',', grip)}] of [{string.Join(',', fullGrip)}] lacks [{string.Join(',', lost)}] of the chord at {chord.Time}");
                }

                if (reducedChordsOnly) continue;
                foreach (var note in level.Notes.Where(n => n.Time >= shape.StartTime && n.Time < shape.EndTime))
                {
                    if (fullGrip.Contains(note.String) && !grip.Contains(note.String))
                        mismatches.Add($"level {level.Difficulty} shape {shape.StartTime}: grip [{string.Join(',', grip)}] of [{string.Join(',', fullGrip)}] lacks string {note.String} of the note at {note.Time}");
                }
            }
        }

        return mismatches;
    }

    /// <summary>DLC Builder's build order on one case: the fixes, then, for a single
    /// level, the generated difficulty levels.</summary>
    private static DdArrangement Generated(string name)
    {
        var song = name.StartsWith("dd/", StringComparison.Ordinal)
            ? DdGoldenParityTests.LoadFixture(name[3..] + ".xml")
            : ReviewSongs().Single(candidate => candidate.Name == name).Song;
        var arr = DdXml.Read(song);
        DdImprover.ApplyAll(arr);
        if (arr.Levels.Count == 1) DdGenerator.GenerateForArrangement(DdGeneratorConfig.Default, arr);
        return arr;
    }

    internal static IEnumerable<(string Name, XElement Song)> ReviewSongs() =>
    [
        ("review/row-1-chord-notes", Row1(true)),
        ("review/row-1-template-only", Row1(false)),
        ("review/row-2", Row2()),
        ("review/arpeggio-grip-over-muted-chord", ArpeggioGripOverMutedChord()),
        ("review/shape-over-muted-and-unmuted", ShapeOverMutedAndUnmuted())
    ];

    // Template A on strings 0-3; a chord on A with string 3 muted, later an unmuted chord on A.
    private static XElement Row1(bool laterHasChordNotes) => Song(
        [Template("A", "A", 0, 1, 2, 3)],
        [Chord(1000, 0, [0, 1, 2, 3], muted: [3]), Chord(2000, 0, laterHasChordNotes ? [0, 1, 2, 3] : [])],
        [Shape(0, 1000, 1500), Shape(0, 2000, 2500)]);

    // Two chords on A with different muted strings.
    private static XElement Row2() => Song(
        [Template("A", "A", 0, 1, 2, 3)],
        [Chord(1000, 0, [0, 1, 2, 3], muted: [3]), Chord(2000, 0, [0, 1, 2, 3], muted: [0])],
        [Shape(0, 1000, 1500), Shape(0, 2000, 2500)]);

    // The review chart of the #561 batch: an arpeggio grip on 0/1/2, at 0 s a chord on it
    // that mutes string 0 under its own shape, at 2 s an unmuted three-string chord on
    // another template.
    private static XElement ArpeggioGripOverMutedChord() => Song(
        [Template("A", "A-arp", 0, 1, 2), Template("B", "B", 3, 4, 5)],
        [Chord(0, 0, [0, 1, 2], muted: [0]), Chord(2000, 1, [3, 4, 5])],
        [Shape(0, 0, 1000), Shape(1, 2000, 3000)]);

    // One shape holds a chord that mutes string 0 and, later, the same grip played whole.
    private static XElement ShapeOverMutedAndUnmuted() => Song(
        [Template("A", "A", 0, 1, 2), Template("B", "B", 2, 3, 4, 5)],
        [Chord(1000, 0, [0, 1, 2], muted: [0]), Chord(1500, 0, [0, 1, 2]), Chord(3000, 1, [2, 3, 4, 5])],
        [Shape(0, 1000, 2000), Shape(1, 3000, 3500)]);

    private static XElement Song(XElement[] templates, XElement[] chords, XElement[] shapes)
    {
        var beats = Enumerable.Range(0, 17).Select(i =>
        {
            var beat = new XElement("ebeat", new XAttribute("time", DdXml.FormatTime(i * 500)));
            if (i % 4 == 0) beat.SetAttributeValue("measure", i / 4 + 1);
            return beat;
        });
        return new XElement("song",
            new XElement("songLength", "8.000"), new XElement("averageTempo", "120"),
            new XElement("phrases", new XAttribute("count", 2),
                new XElement("phrase", new XAttribute("name", "one"), new XAttribute("maxDifficulty", 0)),
                new XElement("phrase", new XAttribute("name", "END"), new XAttribute("maxDifficulty", 0))),
            new XElement("phraseIterations", new XAttribute("count", 2),
                new XElement("phraseIteration", new XAttribute("time", "0.000"), new XAttribute("phraseId", 0)),
                new XElement("phraseIteration", new XAttribute("time", "7.500"), new XAttribute("phraseId", 1))),
            new XElement("chordTemplates", new XAttribute("count", templates.Length), templates),
            new XElement("ebeats", new XAttribute("count", 17), beats),
            new XElement("levels", new XAttribute("count", 1),
                new XElement("level", new XAttribute("difficulty", 0),
                    new XElement("notes", new XAttribute("count", 0)),
                    new XElement("fretHandMutes", new XAttribute("count", 0)),
                    new XElement("chords", new XAttribute("count", chords.Length), chords),
                    new XElement("anchors", new XAttribute("count", 1),
                        new XElement("anchor", new XAttribute("time", "0.000"), new XAttribute("fret", 5), new XAttribute("width", "4.000"))),
                    new XElement("handShapes", new XAttribute("count", shapes.Length), shapes))));
    }

    private static XElement Template(string name, string displayName, params int[] strings)
    {
        var template = new XElement("chordTemplate", new XAttribute("chordName", name), new XAttribute("displayName", displayName));
        for (var s = 0; s < 6; s++)
        {
            template.SetAttributeValue($"fret{s}", strings.Contains(s) ? 5 : -1);
            template.SetAttributeValue($"finger{s}", strings.Contains(s) ? 1 : -1);
        }

        return template;
    }

    private static XElement Chord(int timeMs, int templateId, int[] chordNoteStrings, int[]? muted = null)
    {
        var time = DdXml.FormatTime(timeMs);
        var chord = new XElement("chord", new XAttribute("time", time), new XAttribute("chordId", templateId));
        foreach (var s in chordNoteStrings)
        {
            var note = new XElement("chordNote", new XAttribute("time", time), new XAttribute("string", s), new XAttribute("fret", 5));
            if (muted?.Contains(s) == true) note.SetAttributeValue("mute", 1);
            chord.Add(note);
        }

        return chord;
    }

    private static XElement Shape(int templateId, int startMs, int endMs) => new("handShape",
        new XAttribute("chordId", templateId),
        new XAttribute("startTime", DdXml.FormatTime(startMs)), new XAttribute("endTime", DdXml.FormatTime(endMs)));

    private static int[] Strings(DdChordTemplate template) =>
        [.. Enumerable.Range(0, template.Frets.Length).Where(s => template.Frets[s] >= 0)];
}
