namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Placing a note on a string a small chord already uses COLLAPSES that chord.
///     Two defects lived in that path, both absent from the sibling
///     <c>EditChordStringCommand.BuildCollapseAction</c>, which has always been
///     written correctly — the asymmetry WAS the bug.
///     <list type="number">
///         <item>
///             <c>.First(n =&gt; n != existing)</c> on a ONE-note chord threw
///             <c>InvalidOperationException</c>. The guard admits
///             <c>Count == 1</c>, and one-note chords are routine:
///             <c>ChartSpacingEnforcer.RemoveMutedNotesFromPartiallyMutedChords</c>
///             produces them, and it runs on every save AND every committed
///             highway edit.
///         </item>
///         <item>
///             The surviving note was rebuilt from string/sustain/fret only, so
///             every collapse silently dropped mute, HOPO, harmonic, vibrato,
///             tremolo, link-next, slides and the whole bend curve.
///         </item>
///     </list>
/// </summary>
public sealed class ChordCollapsePlacementTests
{
    [Fact]
    public void PlacingOnTheOnlyStringOfAOneNoteChordRemovesItInsteadOfThrowing()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        h.AddTemplate(0, (3, 5));
        h.AddChord(2, 0, (3, 5));

        // Would throw InvalidOperationException before the fix.
        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        Assert.Empty(h.State.PlacedChords);
        Assert.Empty(h.State.PlacedNotes);
    }

    [Fact]
    public void CollapsingATwoNoteChordCarriesTheSurvivingNotesTechniques()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        h.AddTemplate(0, (3, 5), (4, 7));
        var chord = h.AddChord(2, 0, (3, 5), (4, 7));

        var survivor = chord.ChordNotes.Single(n => n.StringIndex == 4);
        survivor.Hopo = HopoType.HammerOn;
        survivor.Vibrato = true;
        survivor.Tremolo = true;
        survivor.LinkNext = true;
        survivor.SlideTo = 9;
        survivor.Mute = MuteType.Palm;
        survivor.BendValues.Add(new BendValue { Step = 1.0f });
        chord.Accent = true;

        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        var collapsed = Assert.Single(h.State.PlacedNotes);
        Assert.Equal(4, collapsed.StringIndex);
        Assert.Equal(7, collapsed.Fret);
        Assert.Equal(HopoType.HammerOn, collapsed.Hopo);
        Assert.True(collapsed.Vibrato);
        Assert.True(collapsed.Tremolo);
        Assert.True(collapsed.LinkNext);
        Assert.Equal(9, collapsed.SlideTo);
        Assert.Equal(MuteType.Palm, collapsed.Mute);
        Assert.Equal(1.0f, Assert.Single(collapsed.BendValues).Step);
        Assert.True(collapsed.Accent);
    }

    /// <summary>
    ///     The bend curve must be CLONED, not shared: the chord it came from is
    ///     still on the undo stack, and a later edit to the note would otherwise
    ///     reach back and rewrite the reverted state too.
    /// </summary>
    [Fact]
    public void TheCollapsedNotesBendCurveIsACopy()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        h.AddTemplate(0, (3, 5), (4, 7));
        var chord = h.AddChord(2, 0, (3, 5), (4, 7));
        var survivor = chord.ChordNotes.Single(n => n.StringIndex == 4);
        var original = new BendValue { Step = 1.0f };
        survivor.BendValues.Add(original);

        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        var collapsed = Assert.Single(h.State.PlacedNotes);
        Assert.NotSame(original, Assert.Single(collapsed.BendValues));
    }

    /// <summary>
    ///     The OPPOSITE conversion, and it had the same defect the collapse
    ///     branch above was fixed for: placing a second string at a position that
    ///     already holds loose notes PROMOTES them into a chord, and each survivor
    ///     was re-expressed as a ChordNote carrying only string/fret/sustain.
    ///     Mute, HOPO, harmonic, vibrato, tremolo, link-next, slides, bass picking
    ///     and the whole bend curve were dropped on the spot - silently, and
    ///     ChordNote round-trips every one of them.
    /// </summary>
    [Fact]
    public void PromotingLooseNotesIntoAChordCarriesTheirTechniques()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        var note = new GuitarNote
        {
            Position = new FractionalPosition(2),
            StringIndex = 4,
            Fret = 7,
            Hopo = HopoType.HammerOn,
            Harmonic = HarmonicType.Pinch,
            Vibrato = true,
            VibratoStrength = 80,
            Tremolo = true,
            LinkNext = true,
            SlideTo = 9,
            UnpitchedSlide = true,
            Mute = MuteType.Palm,
            BassPicking = BassPickingType.Slap,
            Accent = true
        };
        note.BendValues.Add(new BendValue { Step = 1.0f });
        h.State.PlacedNotes.Add(note);

        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        var chord = Assert.Single(h.State.PlacedChords);
        var promoted = chord.ChordNotes.Single(n => n.StringIndex == 4);
        Assert.Equal(7, promoted.Fret);
        Assert.Equal(HopoType.HammerOn, promoted.Hopo);
        Assert.Equal(HarmonicType.Pinch, promoted.Harmonic);
        Assert.True(promoted.Vibrato);
        Assert.Equal(80, promoted.VibratoStrength);
        Assert.True(promoted.Tremolo);
        Assert.True(promoted.LinkNext);
        Assert.Equal(9, promoted.SlideTo);
        Assert.True(promoted.UnpitchedSlide);
        Assert.Equal(MuteType.Palm, promoted.Mute);
        Assert.Equal(BassPickingType.Slap, promoted.BassPicking);
        Assert.True(promoted.Accent);
        Assert.Equal(1.0f, Assert.Single(promoted.BendValues).Step);
    }

    /// <summary>The promoted bend curve must be a COPY - the loose note it came
    /// from is still on the undo stack.</summary>
    [Fact]
    public void ThePromotedChordNotesBendCurveIsACopy()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 4, Fret = 7 };
        var original = new BendValue { Step = 1.0f };
        note.BendValues.Add(original);
        h.State.PlacedNotes.Add(note);

        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        var chord = Assert.Single(h.State.PlacedChords);
        var promoted = chord.ChordNotes.Single(n => n.StringIndex == 4);
        Assert.NotSame(original, Assert.Single(promoted.BendValues));
    }

    /// <summary>Bass picking is an IGuitarSound member on both sides and one of
    /// the per-string verbs (SelectedNoteMutationCommands.TogglePicking), but the
    /// hand-written collapse constructors omitted it, so a slapped note came out
    /// of a collapsed chord plain.</summary>
    [Fact]
    public void CollapsingATwoNoteChordCarriesBassPicking()
    {
        var h = new PlacementTemplateRepointTests.Harness();
        h.AddTemplate(0, (3, 5), (4, 7));
        var chord = h.AddChord(2, 0, (3, 5), (4, 7));
        chord.ChordNotes.Single(n => n.StringIndex == 4).BassPicking = BassPickingType.Pop;

        h.Service.ApplyPlacement(new FractionalPosition(2), 3);

        var collapsed = Assert.Single(h.State.PlacedNotes);
        Assert.Equal(BassPickingType.Pop, collapsed.BassPicking);
    }
}
