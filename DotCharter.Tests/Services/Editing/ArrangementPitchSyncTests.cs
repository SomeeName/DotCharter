using DotCharter.Services.Editing.Sync;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #913: copying the hand-authored work between an extended-range part and its reduced
///     twin — a 7-string in drop A and a 6-string in drop D, a 5-string bass and a 4-string
///     one. The shared upper strings sit at DIFFERENT indices, so a sound matches on
///     (position, sounding pitch, fret), and only the hand travels: chord fingerings, hand
///     shapes and fret-hand positions. Whatever the target re-voices is left alone.
/// </summary>
public sealed class ArrangementPitchSyncTests
{
    private static readonly int[] DropA7 = [-2, 0, 0, 0, 0, 0, 0]; // A1 E2 A2 D3 G3 B3 E4
    private static readonly int[] DropD6 = [-2, 0, 0, 0, 0, 0];    // D2 A2 D3 G3 B3 E4
    private static readonly int[] Beadg5 = [-5, -5, -5, -5, -4];   // B0 E1 A1 D2 G2
    private static readonly int[] Eadg4 = [0, 0, 0, 0];

    [Fact]
    public void SevenToSix_SharedRiffCarriesTheHand_ReVoicedBarIsUntouched()
    {
        var source = Arr("Lead 7", ArrangementType.Lead, DropA7);
        var target = Arr("Lead 6", ArrangementType.Lead, DropD6);

        // Beat 2: a single note on the high E — string 6 on the seven, 5 on the six.
        AddNote(source, 2, 6, 12).Mute = MuteType.Palm;
        var twinNote = AddNote(target, 2, 5, 12);

        // Beat 4: the shared riff chord on A2/D3/G3.
        var grip = Template(source, 3, (2, 5), (3, 7), (4, 7));
        grip.Name = "D5";
        grip.Fingers[2] = 1;
        grip.Fingers[3] = 3;
        grip.Fingers[4] = 4;
        AddChord(source, 4, (2, 5), (3, 7), (4, 7)).TemplateId = grip.Id;
        var twinChord = AddChord(target, 4, (1, 5), (2, 7), (3, 7));
        source.Levels[0].HandShapes.Add(Shape(4, 6, grip.Id));
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 5, 4));

        // Beat 8: the low A1 power chord the six cannot play, re-voiced up a fourth.
        var low = Template(source, 4, (0, 0), (1, 0), (2, 0));
        AddChord(source, 8, (0, 0), (1, 0), (2, 0)).TemplateId = low.Id;
        source.Levels[0].HandShapes.Add(Shape(8, 10, low.Id));
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(8), 1, 4));
        var ownGrip = Template(target, 0, (0, 7), (1, 7), (2, 7));
        var reVoiced = AddChord(target, 8, (0, 7), (1, 7), (2, 7));
        reVoiced.TemplateId = ownGrip.Id;
        target.Levels[0].HandShapes.Add(Shape(8, 10, ownGrip.Id));
        target.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(8), 7, 4));

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.True(summary.ByPitch);
        Assert.Equal((2, 1), (summary.SharedSounds, summary.UnmatchedSounds));
        Assert.Equal((1, 1, 1), (summary.SoundsChanged, summary.HandShapes, summary.HandShapesLeft));
        Assert.Equal((1, 1), (summary.Anchors, summary.AnchorsLeft));

        // Note edits never travel between tunings.
        Assert.Equal(MuteType.None, twinNote.Mute);

        // The riff's grip, on the six's strings, with the source's fingers.
        var copied = target.FindChordTemplate(twinChord.TemplateId);
        Assert.NotNull(copied);
        Assert.Equal(new Dictionary<int, int> { [1] = 5, [2] = 7, [3] = 7 }, copied.Frets);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [3] = 4 }, copied.Fingers);
        Assert.Equal("D5", copied.Name);
        var shapes = target.Levels[0].HandShapes;
        Assert.Equal(2, shapes.Count);
        Assert.Equal(copied.Id, shapes[0].TemplateId);
        Assert.Equal(new FractionalPosition(4), shapes[0].Position);

        // The re-voiced bar keeps the target's own answer.
        Assert.Equal(ownGrip.Id, reVoiced.TemplateId);
        Assert.Equal(ownGrip.Id, shapes[1].TemplateId);
        Assert.Equal([(new FractionalPosition(4), 5), (new FractionalPosition(8), 7)],
            target.Levels[0].Anchors.Select(a => (a.Position, a.Fret)).ToArray());

        // The source's shared grip is not edited by the remap.
        Assert.Equal(new Dictionary<int, int> { [2] = 5, [3] = 7, [4] = 7 }, grip.Frets);
    }

    [Fact]
    public void FiveToFourBass_MatchesTheSharedStrings_AndAnAnchorOverAReVoicedNoteStays()
    {
        var source = Arr("Bass 5", ArrangementType.Bass, Beadg5);
        var target = Arr("Bass 4", ArrangementType.Bass, Eadg4);

        // Beats 0..3: E A D G riff — strings 1..4 on the five, 0..3 on the four.
        for (var s = 1; s <= 4; s++)
        {
            AddNote(source, s - 1, s, 3);
            AddNote(target, s - 1, s - 1, 3);
        }

        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(0), 3, 4));

        // Beat 8: the five plays its low E on the B string's 5th fret; the four plays it open.
        AddNote(source, 8, 0, 5);
        AddNote(target, 8, 0, 0);
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(8), 5, 4));

        var preview = ArrangementSoundSync.Preview(source, target);
        Assert.Empty(target.Levels[0].Anchors);

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(preview.Anchors, summary.Anchors);
        Assert.True(summary.ByPitch);
        Assert.Equal((4, 1), (summary.SharedSounds, summary.UnmatchedSounds));
        Assert.Equal((1, 1), (summary.Anchors, summary.AnchorsLeft));
        var anchor = Assert.Single(target.Levels[0].Anchors);
        Assert.Equal((new FractionalPosition(0), 3), (anchor.Position, anchor.Fret));
    }

    /// <summary>
    ///     Frets are absolute and fret 0 is the capo: with the same capo an open string matches,
    ///     with a different one it sounds a different pitch and does not — the fretted note does.
    /// </summary>
    [Theory]
    [InlineData(2, 2, 0)]
    [InlineData(0, 1, 1)]
    public void Capo_OpenStringsMatchOnlyUnderTheSameCapo(int targetCapo, int shared, int unmatched)
    {
        var source = Arr("Lead 7", ArrangementType.Lead, DropA7);
        source.Capo = 2;
        var target = Arr("Lead 6", ArrangementType.Lead, DropD6);
        target.Capo = targetCapo;
        AddNote(source, 0, 3, 0);
        AddNote(target, 0, 2, 0);
        AddNote(source, 1, 3, 5);
        AddNote(target, 1, 2, 5);

        var summary = ArrangementSoundSync.Preview(source, target);

        Assert.True(summary.ByPitch);
        Assert.Equal((shared, unmatched), (summary.SharedSounds, summary.UnmatchedSounds));
    }

    [Fact]
    public void ATargetTemplateSharedWithAnotherChord_IsNeverEdited()
    {
        var source = Arr("Lead 7", ArrangementType.Lead, DropA7);
        var target = Arr("Lead 6", ArrangementType.Lead, DropD6);
        var authored = Template(source, 0, (2, 5), (3, 7));
        authored.Fingers[2] = 1;
        authored.Fingers[3] = 3;
        AddChord(source, 4, (2, 5), (3, 7)).TemplateId = authored.Id;

        // Template 0 in the target already has this grip, fingered differently, and backs
        // a chord outside the shared ground too.
        var incumbent = Template(target, 0, (1, 5), (2, 7));
        incumbent.Fingers[1] = 1;
        incumbent.Fingers[2] = 4;
        var before = incumbent.Clone();
        var matched = AddChord(target, 4, (1, 5), (2, 7));
        matched.TemplateId = incumbent.Id;
        var elsewhere = AddChord(target, 20, (1, 5), (2, 7));
        elsewhere.TemplateId = incumbent.Id;

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.Templates);
        Assert.Equal(before, incumbent);
        Assert.Equal(before.Fingers, incumbent.Fingers);
        Assert.Equal(incumbent.Id, elsewhere.TemplateId);
        Assert.NotEqual(incumbent.Id, matched.TemplateId);
        var minted = target.FindChordTemplate(matched.TemplateId)!;
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3 }, minted.Fingers);

        // Running it again finds the minted grip instead of minting another.
        Assert.Equal(0, ArrangementSoundSync.Apply(source, target).Templates);
    }

    [Fact]
    public void SameStringsAndTuning_StaysOnTheIndexSync_DifferentTuningGoesByPitch()
    {
        var a = Arr("A", ArrangementType.Lead, DropA7);
        var b = Arr("B", ArrangementType.Lead, DropA7);
        AddNote(a, 1, 3, 5);
        AddNote(b, 1, 3, 5);
        Assert.False(ArrangementSoundSync.Preview(a, b).ByPitch);

        var standard = Arr("E", ArrangementType.Lead, [0, 0, 0, 0, 0, 0]);
        var dropD = Arr("D", ArrangementType.Lead, DropD6);
        AddNote(standard, 1, 0, 5);
        AddNote(dropD, 1, 0, 5);
        AddNote(standard, 2, 1, 5);
        AddNote(dropD, 2, 1, 5);
        var summary = ArrangementSoundSync.Preview(standard, dropD);
        Assert.True(summary.ByPitch);
        // The low string sounds A2 vs G2 at fret 5 — not the same sound any more.
        Assert.Equal((1, 1), (summary.SharedSounds, summary.UnmatchedSounds));
    }

    [Fact]
    public void ThePitchSync_IsOneUndoStep()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var source = Arr("Lead 7", ArrangementType.Lead, DropA7);
        var target = Arr("Lead 6", ArrangementType.Lead, DropD6);
        var grip = Template(source, 0, (2, 5), (3, 7));
        grip.Fingers[2] = 1;
        grip.Fingers[3] = 3;
        AddChord(source, 4, (2, 5), (3, 7)).TemplateId = grip.Id;
        AddChord(target, 4, (1, 5), (2, 7));
        source.Levels[0].HandShapes.Add(Shape(4, 6, grip.Id));
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 5, 4));
        project.Arrangements.Add(source);
        project.Arrangements.Add(target);
        var targetIndex = project.Arrangements.IndexOf(target);

        var summary = h.Maintenance.SyncArrangement(source, target);
        var synced = project.Arrangements[targetIndex];
        Assert.True(summary.ByPitch);
        Assert.Single(synced.Levels[^1].HandShapes);
        Assert.Single(synced.Levels[^1].Anchors);
        Assert.NotEmpty(synced.ChordTemplates);

        h.History.Undo();

        var restored = h.State.ActiveProject!.Arrangements[targetIndex];
        Assert.Empty(restored.Levels[^1].HandShapes);
        Assert.Empty(restored.Levels[^1].Anchors);
        Assert.Empty(restored.ChordTemplates);
        Assert.Equal(-1, Assert.Single(restored.Levels[^1].Chords).TemplateId);
    }

    // ---- fixtures ----------------------------------------------------------

    private static Arrangement Arr(string name, ArrangementType type, int[] offsets)
    {
        var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = offsets.Length };
        foreach (var step in offsets) arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static GuitarNote AddNote(Arrangement arrangement, int beat, int stringIndex, int fret)
    {
        var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };
        arrangement.Levels[0].Notes.Add(note);
        return note;
    }

    private static Chord AddChord(Arrangement arrangement, int beat, params (int StringIndex, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = -1 };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        arrangement.Levels[0].Chords.Add(chord);
        return chord;
    }

    private static HandShape Shape(int start, int end, int templateId) => new()
    {
        Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end), TemplateId = templateId
    };

    private static ChordTemplate Template(Arrangement arrangement, int id, params (int S, int F)[] frets)
    {
        var template = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        arrangement.ChordTemplates.Add(template);
        return template;
    }
}
