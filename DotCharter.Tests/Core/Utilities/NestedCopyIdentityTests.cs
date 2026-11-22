namespace DotCharter.Tests.Core.Utilities;

public sealed class NestedCopyIdentityTests
{
    [Fact]
    public void AClonedSiblingInTheSameOwner_IsASeparateLiveObjectOnEveryReplay()
    {
        var chord = new Chord();
        var original = new ChordNote { StringIndex = 0, Fret = 5 };
        chord.ChordNotes.Add(original);
        var before = chord.Clone();
        var after = chord.Clone();
        var sibling = after.ChordNotes[0].Clone();
        sibling.StringIndex = 1;
        after.ChordNotes.Insert(0, sibling);
        chord.CopyFrom(after);
        var liveSibling = chord.ChordNotes[0];

        Assert.NotSame(liveSibling, chord.ChordNotes[1]);
        Assert.Same(original, chord.ChordNotes[1]);
        chord.CopyFrom(before);
        Assert.Same(original, Assert.Single(chord.ChordNotes));
        chord.CopyFrom(after);
        Assert.Same(liveSibling, chord.ChordNotes[0]);
        Assert.Same(original, chord.ChordNotes[1]);
    }

    [Fact]
    public void CopyFrom_ReattachesRemovedBendsAndReusesAddedBendsOnEveryReplay()
    {
        var note = new GuitarNote();
        var first = new BendValue(new FractionalPosition(1), 1);
        var second = new BendValue(new FractionalPosition(2), 2);
        note.BendValues.Add(first);
        note.BendValues.Add(second);
        var before = note.Clone();
        var after = note.Clone();
        after.BendValues.RemoveAt(0);
        after.BendValues.Add(new BendValue(new FractionalPosition(3), 3));
        note.CopyFrom(after);
        var added = note.BendValues[1];

        for (var i = 0; i < 3; i++)
        {
            note.CopyFrom(before);
            Assert.Same(first, note.BendValues[0]);
            Assert.Same(second, note.BendValues[1]);
            note.CopyFrom(after);
            Assert.Same(second, note.BendValues[0]);
            Assert.Same(added, note.BendValues[1]);
        }
    }

    [Fact]
    public void ReplacingAWorkingChordCollection_PreservesChildIdentitiesThroughCopyFromAndSnapshots()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var chord = new Chord();
        var first = new ChordNote { StringIndex = 0 };
        var second = new ChordNote { StringIndex = 1 };
        var bend = new BendValue(new FractionalPosition(1), 1);
        second.BendValues.Add(bend);
        chord.ChordNotes.Add(first);
        chord.ChordNotes.Add(second);
        level.Chords.Add(chord);
        var before = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        var edited = chord.Clone();
        edited.ChordNotes = [edited.ChordNotes[1].Clone()];
        edited.ChordNotes[0].StringIndex = 4;

        chord.CopyFrom(edited);
        Assert.Same(second, Assert.Single(chord.ChordNotes));
        Assert.Same(bend, Assert.Single(second.BendValues));
        var after = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        ChartProjectRestorer.RestoreArrangement(arrangement, before);
        Assert.Same(first, chord.ChordNotes[0]);
        Assert.Same(second, chord.ChordNotes[1]);
        ChartProjectRestorer.RestoreArrangement(arrangement, after);
        Assert.Same(second, Assert.Single(chord.ChordNotes));
        Assert.Equal(4, second.StringIndex);
        Assert.Same(bend, Assert.Single(second.BendValues));
    }
}
