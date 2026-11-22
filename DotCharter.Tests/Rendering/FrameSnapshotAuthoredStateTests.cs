using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

public sealed class FrameSnapshotAuthoredStateTests
{
    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(512)]
    public void Dense_bends_keep_every_point_for_notes_and_chord_notes(int count)
    {
        var note = new GuitarNote { Fret = 5, Sustain = new FractionalPosition(1) };
        var chordNote = new ChordNote { Fret = 7, StringIndex = 1, Sustain = new FractionalPosition(1) };
        for (var i = 0; i < count; i++)
        {
            var position = new FractionalPosition(0, new Fraction(i, count));
            note.BendValues.Add(new BendValue(position, i));
            chordNote.BendValues.Add(new BendValue(position, count - i));
        }

        var chord = new Chord { ChordNotes = [chordNote] };
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = Beats(), ActiveNotes = [note], ActiveChords = [chord],
            PreviewNotes = [note], PreviewChords = [chord], GhostNotes = [note], GhostChords = [chord]
        };
        var snapshot = new FrameSnapshot();

        FrameSnapshot.BuildSnapshot(0, 1000, 0, snapshot, ctx, [], 0);

        Assert.Equal(count * 6, snapshot.BendValueCount);
        Assert.Equal(1, snapshot.NoteCount);
        Assert.Equal(1, snapshot.ChordNoteCount);
        Assert.Equal(1, snapshot.PreviewNoteCount);
        Assert.Equal(1, snapshot.PreviewChordNoteCount);
        Assert.Equal(1, snapshot.GhostNoteCount);
        Assert.Equal(1, snapshot.GhostChordNoteCount);
        foreach (var n in new[] { snapshot.Notes[0], snapshot.PreviewNotes[0], snapshot.GhostNotes[0] })
        {
            Assert.Equal(count, n.BendCount);
            Assert.True(n.Flags.HasFlag(NoteFlags.HasBends));
            Assert.Equal(count - 1, snapshot.BendValues[n.BendOffset + n.BendCount - 1].Step);
        }
        foreach (var n in new[] { snapshot.ChordNotes[0], snapshot.PreviewChordNotes[0], snapshot.GhostChordNotes[0] })
        {
            Assert.Equal(count, n.BendCount);
            Assert.True(n.Flags.HasFlag(NoteFlags.HasBends));
            Assert.Equal(1f, snapshot.BendValues[n.BendOffset + n.BendCount - 1].Step);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void In_place_time_signature_edit_undo_and_redo_refresh_both_snapshot_prefixes(bool versioned)
    {
        var beats = Beats();
        var firstBeat = beats[0];
        var snapshots = new[] { new FrameSnapshot(), new FrameSnapshot() };
        var ctx = new SnapshotUpdateContext { ActiveBeats = beats, BeatsVersion = versioned ? 0 : -1 };
        var history = new HistoryService(new FakeAppLogger());
        history.HistoryChanged += () =>
        {
            if (versioned) ctx.BeatsVersion++;
        };

        void AssertMeasure(int expected)
        {
            foreach (var snapshot in snapshots)
            {
                FrameSnapshot.BuildSnapshot(2000, 2500, 2000, snapshot, ctx, [], 0);
                Assert.Equal(2000d, snapshot.Beats[0].Time);
                Assert.Equal(expected, snapshot.Beats[0].MeasureNumber);
            }
        }

        AssertMeasure(2);
        var changed = beats.Select(b => b.Clone()).ToList();
        for (var i = 0; i < changed.Count; i++)
        {
            changed[i].BeatsInMeasure = 2;
            changed[i].IsMeasure = i % 2 == 0;
        }

        Assert.True(history.Do(new MutateBeatsAction(beats, beats, changed)));
        Assert.Same(firstBeat, beats[0]);
        AssertMeasure(3);
        history.Undo();
        AssertMeasure(2);
        history.Redo();
        AssertMeasure(3);
    }

    private static List<BeatMarker> Beats() => Enumerable.Range(0, 9)
        .Select(i => new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 }).ToList();
}
