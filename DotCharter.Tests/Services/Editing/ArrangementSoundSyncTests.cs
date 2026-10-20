using DotCharter.Services.Editing.Sync;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Carrying the charting work done on one arrangement over to another.
///     <para>
///         A Lead chart is usually Lead AND rhythm parts — the same riff is in
///         both arrangements note for note — so every fret-hand position, every
///         fingering and every hand shape gets authored twice. The owner's
///         report: "I did several FHP adjustments, fingering adjustments,
///         handshape additions and adjustments and don't want to do the same
///         thing again on Rhythm."
///     </para>
///     <para>
///         The unit is the SOUND, never the measure — which is the whole
///         difficulty. In the reported bar, Lead and Rhythm share the notes but
///         Lead ends it with a slide chord Rhythm does not play; the shared
///         notes must come across and the slide chord must not follow.
///     </para>
/// </summary>
public sealed class ArrangementSoundSyncTests
{
    [Fact]
    public void ASharedNote_TakesTheSourcesExpression()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        var lead = AddNote(source, beat: 4, stringIndex: 1, fret: 9, sustain: 2);
        AddNote(target, beat: 4, stringIndex: 1, fret: 9);
        lead.Vibrato = true;
        lead.Accent = true;

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.SharedSounds);
        Assert.Equal(1, summary.SoundsChanged);
        var twin = Assert.Single(target.Levels[0].Notes);
        Assert.Equal(new FractionalPosition(2), twin.Sustain);
        Assert.True(twin.Vibrato);
        Assert.True(twin.Accent);
    }

    /// <summary>
    ///     The reported bar. A sound the target does not play IDENTICALLY is not
    ///     shared, and nothing about it travels — not the note, not a hand shape
    ///     that covers it.
    /// </summary>
    [Fact]
    public void ASoundOnlyTheSourcePlays_IsNeverCopied()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 1, 9, sustain: 2);
        AddNote(source, 6, 3, 12); // the Lead-only lick
        AddNote(target, 4, 1, 9);

        ArrangementSoundSync.Apply(source, target);

        Assert.Single(target.Levels[0].Notes);
        Assert.DoesNotContain(target.Levels[0].Notes, n => n.Position == new FractionalPosition(6));
    }

    [Fact]
    public void ANoteAtTheSamePositionOnADIFFERENTFret_IsNotShared()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        var lead = AddNote(source, 4, 1, 9, sustain: 2);
        var rhythm = AddNote(target, 4, 1, 7);
        lead.Vibrato = true;

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(0, summary.SharedSounds);
        Assert.False(rhythm.Vibrato);
        Assert.Equal(7, rhythm.Fret);
    }

    [Fact]
    public void AChordMatchesOnItsWholeShape_NotJustItsPosition()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        var lead = AddChord(source, 8, (0, 3), (1, 5));
        lead.Accent = true;
        AddChord(target, 8, (0, 3), (1, 5), (2, 5)); // one string more

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(0, summary.SharedSounds);
        Assert.False(target.Levels[0].Chords[0].Accent);
    }

    [Fact]
    public void AHandShapeOverEntirelySharedGround_TravelsWithItsFingering()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 0, 7);
        AddNote(source, 5, 1, 9);
        AddNote(target, 4, 0, 7);
        AddNote(target, 5, 1, 9);

        var grip = Template(source, 0, (0, 7), (1, 9));
        grip.Fingers[0] = 1;
        grip.Fingers[1] = 3;
        grip.Name = "Bmin";
        source.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = 0
        });

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.HandShapes);
        var copied = Assert.Single(target.Levels[0].HandShapes);
        var targetGrip = target.ChordTemplates.Single(t => t.Id == copied.TemplateId);
        Assert.Equal(grip.Frets, targetGrip.Frets);
        Assert.Equal(grip.Fingers, targetGrip.Fingers); // the fingering is the point
        Assert.Equal("Bmin", targetGrip.Name);
    }

    /// <summary>
    ///     The protection the reported case needs: a bar covering even ONE sound
    ///     the target does not play would declare a string the target never
    ///     sounds, so it does not travel at all.
    /// </summary>
    [Fact]
    public void AHandShapeCoveringAnyUnsharedSound_DoesNotTravel()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 0, 7);
        AddNote(source, 5, 3, 12); // Lead only
        AddNote(target, 4, 0, 7);

        Template(source, 0, (0, 7), (3, 12));
        source.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = 0
        });

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(0, summary.HandShapes);
        Assert.Empty(target.Levels[0].HandShapes);
    }

    [Fact]
    public void AHandShapeOverEmptyGround_DoesNotTravel()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 20, 0, 7);
        AddNote(target, 20, 0, 7);
        Template(source, 0, (0, 7));
        source.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = 0
        });

        Assert.Equal(0, ArrangementSoundSync.Apply(source, target).HandShapes);
    }

    [Fact]
    public void AnAnchorGoverningSharedGround_Travels()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 5, 0, 7);
        AddNote(target, 5, 0, 7);
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 7, 4));

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.Anchors);
        var copied = Assert.Single(target.Levels[0].Anchors);
        Assert.Equal(7, copied.Fret);
        Assert.Equal(new FractionalPosition(4), copied.Position);
    }

    [Fact]
    public void AnAnchorOverGroundTheTargetDoesNotPlay_DoesNotTravel()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 20, 0, 7);
        AddNote(target, 20, 0, 7);
        // Governs beats 4..19 — nothing shared in there.
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 12, 4));
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(20), 7, 4));

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.Anchors);
        Assert.Equal(new FractionalPosition(20), Assert.Single(target.Levels[0].Anchors).Position);
    }

    /// <summary>One beat holds ONE fret-hand position — same rule the highway's
    ///     own anchor collision enforces.</summary>
    [Fact]
    public void ACopiedAnchor_ReplacesTheTargetsOwnOnThatBeat()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 0, 7);
        AddNote(target, 4, 0, 7);
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 7, 4));
        target.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 2, 4));

        ArrangementSoundSync.Apply(source, target);

        Assert.Equal(7, Assert.Single(target.Levels[0].Anchors).Fret);
    }

    [Fact]
    public void PreviewChangesNothing()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 1, 9, sustain: 2);
        var twin = AddNote(target, 4, 1, 9);
        source.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(4), 9, 4));

        var summary = ArrangementSoundSync.Preview(source, target);

        Assert.Equal(1, summary.SharedSounds);
        Assert.Equal(1, summary.SoundsChanged);
        Assert.Equal(1, summary.Anchors);
        // …and the target is untouched.
        Assert.Equal(FractionalPosition.Zero, twin.Sustain);
        Assert.Empty(target.Levels[0].Anchors);
    }

    [Fact]
    public void SyncingAnArrangementWithItself_DoesNothing() =>
        Assert.Same(ArrangementSyncSummary.Empty, ArrangementSoundSync.Apply(Arr("Lead"), null));

    [Fact]
    public void AlreadyMatchingSounds_CountAsSharedButNotAsChanges()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        AddNote(source, 4, 1, 9);
        AddNote(target, 4, 1, 9);

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(1, summary.SharedSounds);
        Assert.Equal(0, summary.SoundsChanged);
        Assert.False(summary.WouldChangeAnything);
    }

    /// <summary>
    ///     Template ids are arrangement-LOCAL: id 7 in Lead is a different grip
    ///     from id 7 in Rhythm. A copied id has to be resolved by SHAPE or the
    ///     synced chord renders and exports as whatever the target's id 7 is.
    /// </summary>
    [Fact]
    public void ASharedChordsTemplateIsResolvedIntoTheTargetsOwnIds()
    {
        var source = Arr("Lead");
        var target = Arr("Rhythm");
        var leadGrip = Template(source, 7, (0, 10), (1, 12));
        leadGrip.Fingers[0] = 1;
        leadGrip.Name = "Dmin";
        AddChord(source, 8, (0, 10), (1, 12)).TemplateId = 7;

        var stranger = Template(target, 7, (0, 0), (1, 2));
        stranger.Name = "E5";
        AddChord(target, 8, (0, 10), (1, 12)).TemplateId = -1;

        ArrangementSoundSync.Apply(source, target);

        var synced = Assert.Single(target.Levels[0].Chords);
        Assert.NotEqual(7, synced.TemplateId);
        var grip = target.ChordTemplates.Single(t => t.Id == synced.TemplateId);
        Assert.Equal(leadGrip.Frets, grip.Frets);
        Assert.Equal(1, grip.Fingers[0]);
        // The stranger's template is untouched.
        Assert.Equal("E5", stranger.Name);
        Assert.Equal(2, stranger.Frets.Count);
    }

    // ---- fixtures ----------------------------------------------------------

    private static Arrangement Arr(string name)
    {
        var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static GuitarNote AddNote(Arrangement arrangement, int beat, int stringIndex, int fret, int sustain = 0)
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIndex,
            Fret = fret,
            Sustain = new FractionalPosition(sustain)
        };
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

    private static ChordTemplate Template(Arrangement arrangement, int id, params (int S, int F)[] frets)
    {
        var template = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        arrangement.ChordTemplates.Add(template);
        return template;
    }
}
