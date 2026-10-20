using DotCharter.Core.Diff;

namespace DotCharter.Tests.Core;

public sealed class ChartDiffEngineTests
{
    [Fact]
    public void Diff_IdenticalArrangements_IsEmpty()
    {
        var result = ChartDiffEngine.Diff(BuildArrangement(), BuildArrangement());

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Levels);
    }

    [Fact]
    public void Diff_DetectsAddedAndRemovedNotes()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        newArr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(6), StringIndex = 2, Fret = 9 });
        var removed = oldArr.Levels[0].Notes[0]; // note @0 string 0

        // Remove note @0 from the NEW side → it reads as Removed.
        newArr.Levels[0].Notes.RemoveAt(0);

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.ChangedCount);
        var level = Assert.Single(result.Levels);
        Assert.Contains(level.Entries, e =>
            e.Change == ChartDiffChange.Added && e.Position == new FractionalPosition(6) && e.StringIndex == 2);
        Assert.Contains(level.Entries, e =>
            e.Change == ChartDiffChange.Removed && e.Position == removed.Position && e.StringIndex == 0);
    }

    [Fact]
    public void Diff_DetectsChangedFretSustainAndTechniques()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        var note = newArr.Levels[0].Notes[0];
        note.Fret = 12;
        note.Sustain = new FractionalPosition(2);
        note.Tremolo = true;

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entry = Assert.Single(Assert.Single(result.Levels).Entries);
        Assert.Equal(ChartDiffChange.Changed, entry.Change);
        Assert.Equal(ChartDiffSoundKind.Note, entry.Kind);
        Assert.Contains("Fret", entry.ChangedFields);
        Assert.Contains("Sustain", entry.ChangedFields);
        Assert.Contains("Tremolo", entry.ChangedFields);
        Assert.NotNull(entry.OldNote);
        Assert.NotNull(entry.NewNote);
    }

    [Fact]
    public void Diff_MatchesWithinPositionTolerance_ButFlagsFarther()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();

        // Nudge note @0 by 1/128 (inside the default 1/64 tolerance) AND
        // change its fret: one Changed entry, not remove+add.
        var nudged = newArr.Levels[0].Notes[0];
        nudged.Position += new Fraction(1, 128);
        nudged.Fret = 5;

        var result = ChartDiffEngine.Diff(oldArr, newArr);
        var entry = Assert.Single(Assert.Single(result.Levels).Entries);
        Assert.Equal(ChartDiffChange.Changed, entry.Change);
        Assert.Contains("Position", entry.ChangedFields);
        Assert.Contains("Fret", entry.ChangedFields);

        // Move it a half beat instead: now it is remove + add.
        var movedArr = BuildArrangement();
        movedArr.Levels[0].Notes[0].Position += new Fraction(1, 2);
        var movedResult = ChartDiffEngine.Diff(oldArr, movedArr);
        Assert.Equal(1, movedResult.AddedCount);
        Assert.Equal(1, movedResult.RemovedCount);
        Assert.Equal(0, movedResult.ChangedCount);
    }

    [Fact]
    public void Diff_SamePositionDifferentString_IsRemovePlusAdd()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        newArr.Levels[0].Notes[0].StringIndex = 4; // same position, other string

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.ChangedCount);
    }

    [Fact]
    public void Diff_DetectsChordTemplateAndNoteChanges()
    {
        var oldArr = BuildArrangement();

        var templateChanged = BuildArrangement();
        templateChanged.Levels[0].Chords[0].TemplateId = 9;
        var templateEntry = Assert.Single(Assert.Single(ChartDiffEngine.Diff(oldArr, templateChanged).Levels).Entries);
        Assert.Equal(ChartDiffSoundKind.Chord, templateEntry.Kind);
        Assert.Equal(-1, templateEntry.StringIndex);
        Assert.Contains("Template", templateEntry.ChangedFields);

        var fretChanged = BuildArrangement();
        fretChanged.Levels[0].Chords[0].ChordNotes[0].Fret = 10;
        var fretEntry = Assert.Single(Assert.Single(ChartDiffEngine.Diff(oldArr, fretChanged).Levels).Entries);
        Assert.Contains("Notes", fretEntry.ChangedFields);

        var muted = BuildArrangement();
        foreach (var chordNote in muted.Levels[0].Chords[0].ChordNotes) chordNote.Mute = MuteType.Palm;
        var mutedEntry = Assert.Single(Assert.Single(ChartDiffEngine.Diff(oldArr, muted).Levels).Entries);
        Assert.Contains("Mute", mutedEntry.ChangedFields);
    }

    [Fact]
    public void Diff_DetectsBendChanges()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        newArr.Levels[0].Notes[1].BendValues.Add(new BendValue(newArr.Levels[0].Notes[1].Position, 1.0));

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entry = Assert.Single(Assert.Single(result.Levels).Entries);
        Assert.Contains("Bends", entry.ChangedFields);
    }

    [Fact]
    public void Diff_LevelOnlyOnOneSide_DiffsAgainstEmpty()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        var extra = new ChartLevel { DifficultyIndex = 1 };
        extra.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 1 });
        extra.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 1, Fret = 2 });
        newArr.Levels.Add(extra);

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var levelDiff = Assert.Single(result.Levels);
        Assert.Equal(1, levelDiff.DifficultyIndex);
        Assert.Equal(2, result.AddedCount);
        Assert.All(levelDiff.Entries, e => Assert.Equal(ChartDiffChange.Added, e.Change));
    }

    [Fact]
    public void Diff_EntriesArePositionSorted()
    {
        var oldArr = BuildArrangement();
        var newArr = new Arrangement();
        newArr.Levels.Add(new ChartLevel { DifficultyIndex = 0 }); // everything removed

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entries = Assert.Single(result.Levels).Entries;
        Assert.Equal(oldArr.Levels[0].Notes.Count + oldArr.Levels[0].Chords.Count, entries.Count);
        for (var i = 1; i < entries.Count; i++)
            Assert.True(entries[i - 1].Position <= entries[i].Position);
    }

    [Fact]
    public void Diff_IsDeterministic()
    {
        var oldArr = BuildArrangement();
        var newArr = BuildArrangement();
        newArr.Levels[0].Notes[0].Fret = 9;
        newArr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(7), StringIndex = 3, Fret = 2 });

        string Fingerprint(ChartDiffResult r)
        {
            return string.Join("|", r.Levels.SelectMany(l => l.Entries.Select(e =>
                $"{l.DifficultyIndex} {e.Change} {e.Kind} {e.Position} s{e.StringIndex} [{string.Join(",", e.ChangedFields)}]")));
        }

        Assert.Equal(
            Fingerprint(ChartDiffEngine.Diff(oldArr, newArr)),
            Fingerprint(ChartDiffEngine.Diff(oldArr, newArr)));
    }

    // ─── Structure: everything that is not a sound ────────────────────────────
    // DiffLevel walks only Notes and Chords, so a chart edited ONLY in anchors,
    // hand shapes, sections, tone changes or templates diffed as identical — and
    // IsEmpty is the documented LAN "peer has an update" signal.

    [Fact]
    public void Diff_DetectsAMovedAnchor()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.Levels[0].Anchors[0].Fret = 9;

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        Assert.False(result.IsEmpty);
        Assert.True(result.SoundsIdentical, "no sound changed, so the sound-only view must stay clean");
        var entry = Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.Anchor);
        Assert.Equal(ChartDiffChange.Changed, entry.Change);
        Assert.Contains("Fret", entry.ChangedFields);
        Assert.Equal(0, entry.DifficultyIndex);
    }

    [Fact]
    public void Diff_DetectsAChangedSectionName()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.EventPoints[0].Section = "chorus";

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        Assert.False(result.IsEmpty);
        var entry = Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.EventPoint);
        Assert.Contains("Section", entry.ChangedFields);
        Assert.Equal("chorus", entry.Label);
        Assert.Equal(-1, entry.DifficultyIndex);
    }

    [Fact]
    public void Diff_DetectsAResizedHandShape()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.Levels[0].HandShapes[0].EndPosition = new FractionalPosition(9);

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entry = Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.HandShape);
        Assert.Contains("EndPosition", entry.ChangedFields);
    }

    [Fact]
    public void Diff_DetectsAChangedToneName()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.ToneChanges[0].ToneName = "lead";

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entry = Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.ToneChange);
        Assert.Contains("ToneName", entry.ChangedFields);
    }

    [Fact]
    public void Diff_DetectsAnAddedAndARemovedChordTemplate()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.ChordTemplates[0].Name = "Am";

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        // Templates have no position, so a renamed one reads as remove + add.
        var templates = result.Structure.Where(e => e.Kind == ChartDiffElementKind.ChordTemplate).ToList();
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, e => e.Change == ChartDiffChange.Removed);
        Assert.Contains(templates, e => e.Change == ChartDiffChange.Added);
    }

    [Fact]
    public void Diff_DetectsARemovedFretHandMute()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.Levels[0].FretHandMutes.Clear();

        var result = ChartDiffEngine.Diff(oldArr, newArr);

        var entry = Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.FretHandMute);
        Assert.Equal(ChartDiffChange.Removed, entry.Change);
    }

    [Fact]
    public void Diff_StructurallyIdenticalArrangements_StayEmpty()
    {
        var result = ChartDiffEngine.Diff(BuildStructuredArrangement(), BuildStructuredArrangement());

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Structure);
    }

    [Fact]
    public void Diff_StructureIsDeterministic()
    {
        var oldArr = BuildStructuredArrangement();
        var newArr = BuildStructuredArrangement();
        newArr.Levels[0].Anchors[0].Fret = 9;
        newArr.EventPoints[0].Section = "chorus";
        newArr.ToneChanges[0].ToneName = "lead";

        var first = ChartDiffEngine.Diff(oldArr, newArr).Structure
            .Select(e => $"{e.Kind}/{e.Change}/{e.Position}/{e.Label}").ToList();
        var second = ChartDiffEngine.Diff(oldArr, newArr).Structure
            .Select(e => $"{e.Kind}/{e.Change}/{e.Position}/{e.Label}").ToList();

        Assert.Equal(first, second);
    }

    /// <summary><see cref="BuildArrangement" /> plus one of every structural
    /// element, so a test can perturb exactly one and nothing else.</summary>
    private static Arrangement BuildStructuredArrangement()
    {
        var arrangement = BuildArrangement();
        var level = arrangement.Levels[0];

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(3), EndPosition = new FractionalPosition(4), TemplateId = 1
        });
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(5)));

        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "rhythm" });
        arrangement.Phrases.Add("riff");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1, Name = "A", Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 }
        });

        return arrangement;
    }

    /// <summary>One level: notes @0 (s0 f3), @2 (s1 f5, sustain 1), @4 (s0 f7);
    /// chord @3 (template 1, two notes).</summary>
    private static Arrangement BuildArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 7 });
        level.Chords.Add(new Chord
        {
            Position = new FractionalPosition(3), TemplateId = 1,
            ChordNotes =
            {
                new ChordNote { StringIndex = 0, Fret = 5 },
                new ChordNote { StringIndex = 1, Fret = 7 }
            }
        });
        arrangement.Levels.Add(level);
        return arrangement;
    }
}
