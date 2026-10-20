using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.Editing.Collisions;

public class ChartSpacingEnforcerTests
{
    private static AppConfig DefaultConfig()
    {
        return new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
            MinNoteDistanceDivisor = 64
        };
    }

    private static List<BeatMarker> BuildBeats(int count)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    [Fact]
    public void GetMinDistance_BeatFractionMode_ReturnsOneOverDivisor()
    {
        var dist = ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, BuildBeats(4), DefaultConfig());
        Assert.Equal(new FractionalPosition(new Fraction(1, 64)), dist);
    }

    [Fact]
    public void GetMinDistance_MeasureFractionMode_ScalesByBeatsInMeasure()
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.MeasureFraction,
            MinNoteDistanceDivisor = 32
        };
        var beats = BuildBeats(4);
        // 4 beats per measure / 32 = 1/8 of a beat.
        var dist = ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, beats, config);
        Assert.Equal(new FractionalPosition(new Fraction(4, 32)), dist);
    }

    [Fact]
    public void GetMinDistance_MillisecondsMode_TranslatesMsToPosition()
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds,
            MinNoteDistanceMs = 10.0
        };
        var dist = ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, BuildBeats(4), config);
        // At 500ms per beat, 10ms = 1/50th of a beat; result should be positive but small.
        Assert.True(dist > FractionalPosition.Zero);
        Assert.True(dist < new FractionalPosition(new Fraction(1, 10)));
    }

    [Fact]
    public void GetMinDistance_MillisecondsMode_WithZeroMs_ReturnsZero()
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds,
            MinNoteDistanceMs = 0.0
        };
        Assert.Equal(FractionalPosition.Zero,
            ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, BuildBeats(4), config));
    }

    /// <summary>
    ///     The sweep asks for the minimum distance once per SOUND, and since v0.60
    ///     it does so through a one-slot cache keyed on the beat (the derivation is
    ///     a backwards walk to the nearest downbeat plus two GCD-reducing
    ///     constructions, and on a 4/4 chart it is the same answer every time).
    ///     <para>
    ///         A cache that assumed one answer per LEVEL would be invisible on the
    ///         4/4 chart every other test uses, so this one changes METER mid-song
    ///         and drives the real level pass: two sounds a beat apart in a 4/4
    ///         measure and two more a beat apart in a 7/8 measure, with the
    ///         clamp big enough that the two meters shorten the sustains
    ///         differently. Whatever the enforcer does here, it must be what
    ///         <see cref="ChartSpacingEnforcer.GetMinDistance" /> — the uncached
    ///         function, pinned above — says for each of those positions.
    ///     </para>
    /// </summary>
    [Fact]
    public void EnforceLevel_UsesEachMeasuresOwnMinDistance_WhenTheMeterChanges()
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.MeasureFraction,
            MinNoteDistanceDivisor = 8
        };

        // Beats 0-3 are one 4/4 measure; beats 4-10 are one 7/8 measure.
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120,
                IsMeasure = i is 0 or 4,
                BeatsInMeasure = i < 4 ? 4 : 7
            });
        }

        var inFour = new FractionalPosition(1);
        var inSeven = new FractionalPosition(5);
        var fourDistance = ChartSpacingEnforcer.GetMinDistance(inFour, beats, config);
        var sevenDistance = ChartSpacingEnforcer.GetMinDistance(inSeven, beats, config);
        Assert.NotEqual(fourDistance, sevenDistance); // the fixture has to actually differ

        var level = new ChartLevel();
        // Each sound has a whole beat of sustain and a neighbour one beat later,
        // so the clamp is (1 beat - that measure's min distance).
        foreach (var start in new[] { inFour, inSeven })
        {
            level.Notes.Add(new GuitarNote
            { Position = start, StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1) });
            level.Notes.Add(new GuitarNote
            { Position = start + new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        }

        ChartSpacingEnforcer.EnforceLevel(level, beats, config);

        Assert.Equal(new FractionalPosition(1) - fourDistance,
            level.Notes.First(n => n.Position == inFour).Sustain);
        Assert.Equal(new FractionalPosition(1) - sevenDistance,
            level.Notes.First(n => n.Position == inSeven).Sustain);
    }

    /// <summary>
    ///     #322: the enforcer runs on every save and load, and it used to rewrite
    ///     phrase names to Rocksmith's <c>[a-zA-Z0-9 _#]</c> IN THE MODEL. That
    ///     erased <c>サビ</c>, renamed <c>Solo (fast)</c>, and orphaned the
    ///     <c>PhraseInfos</c> entry keyed by the old name — the solo flag and the
    ///     difficulty ceiling silently fell back to their defaults. The names are
    ///     the charter's; Rocksmith's spelling is made at the export boundary.
    /// </summary>
    [Fact]
    public void EnforceProject_KeepsAuthoredPhraseNames_AndTheirPhraseInfoStillResolves()
    {
        var arrangement = new Arrangement();
        arrangement.Phrases.AddRange(["Solo (fast)", "サビ", "Übergang", "Refrain – 2"]);
        arrangement.PhraseInfos["Solo (fast)"] = new PhraseInfo(5, true);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1), Phrase = "Solo (fast)" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "サビ" });
        arrangement.Levels.Add(new ChartLevel());
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        foreach (var beat in BuildBeats(4)) project.Beats.Add(beat);

        ChartSpacingEnforcer.EnforceProject(project, DefaultConfig());

        Assert.Equal(["Solo (fast)", "サビ", "Übergang", "Refrain – 2"], arrangement.Phrases);
        Assert.Equal(["Solo (fast)", "サビ"], arrangement.EventPoints.Select(e => e.Phrase));
        var info = Assert.Contains("Solo (fast)", arrangement.PhraseInfos);
        Assert.True(info.Solo);
        Assert.Equal(5, info.MaxDifficulty);
    }

    [Fact]
    public void EnforceArrangement_StripsRsDisplaySuffixesFromChordTemplateNames()
    {
        var arrangement = new Arrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Name = "Cmaj-arpCONV" });
        arrangement.Levels.Add(new ChartLevel());

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(4), DefaultConfig());

        Assert.Equal("Cmaj", arrangement.ChordTemplates[0].Name);
        // Stripping -arp must not silently downgrade an arpeggio to a plain
        // chord — the flag is what RsXmlSongWriter re-appends the suffix from.
        Assert.True(arrangement.ChordTemplates[0].ForceArpeggioInRs);
    }

    [Fact]
    public void EnforceArrangement_KeepsTheChordNamersMinorSpelling()
    {
        // ChordNameSuggester deliberately spells minor chords "min" (the EOF /
        // RS2014 convention the corpus ground truth uses — measured, it lifted
        // chord-name exact agreement from ~47% to ~86%). The enforcer used to
        // rewrite min -> m on every save AND every load, silently undoing the
        // app's own naming convention for every chart that was ever saved.
        var arrangement = new Arrangement();
        foreach (var name in new[] { "Amin", "Amin7", "AminMaj7", "Cmin(add9)", "Emin11(b5)" })
            arrangement.ChordTemplates.Add(new ChordTemplate { Name = name });
        arrangement.Levels.Add(new ChartLevel());

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(4), DefaultConfig());

        Assert.Equal(new[] { "Amin", "Amin7", "AminMaj7", "Cmin(add9)", "Emin11(b5)" },
            arrangement.ChordTemplates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void EnforceArrangement_DoesNotEatChordNameTextThatMerelyContainsAMarker()
    {
        // Plain String.Replace over the whole name, with no boundary anchor.
        var arrangement = new Arrangement();
        foreach (var name in new[] { "Dominant7", "Diminished", "Terminator riff", "CONVergence" })
            arrangement.ChordTemplates.Add(new ChordTemplate { Name = name });
        arrangement.Levels.Add(new ChartLevel());

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(4), DefaultConfig());

        Assert.Equal(new[] { "Dominant7", "Diminished", "Terminator riff", "CONVergence" },
            arrangement.ChordTemplates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void EnforceArrangement_FoldsTheLoadBearingNopSuffixIntoItsField()
    {
        // "-nop" is RS ChordMask.Nop. A plain strip on save once deleted the only
        // signal the SNG export had; it is FOLDED into NopInRs now (#489), so the
        // name is clean and the flag survives.
        var arrangement = new Arrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Name = "Amin-nop" });
        arrangement.ChordTemplates.Add(new ChordTemplate { Name = "-nop" });
        arrangement.Levels.Add(new ChartLevel());

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(4), DefaultConfig());

        Assert.Equal("Amin", arrangement.ChordTemplates[0].Name);
        Assert.True(arrangement.ChordTemplates[0].NopInRs);
        // Rocksmith's own Nop double stops are unnamed: the bare marker folds to "".
        Assert.Equal("", arrangement.ChordTemplates[1].Name);
        Assert.True(arrangement.ChordTemplates[1].NopInRs);
    }

    [Fact]
    public void EnforceProject_IsSafeWithNull() => ChartSpacingEnforcer.EnforceProject(null, DefaultConfig());

    // ---- EnforceSoundSpacing semantics (pinned for the v0.32.5 zero-alloc
    // backward-sweep rewrite — these encode the behavior of the historical
    // dictionary-based implementation) -----------------------------------

    private static FractionalPosition Pos(int beat, int num = 0, int den = 1)
        => new(beat, new Fraction(num, den));

    [Fact]
    public void SoundSpacing_ClampsSustainToNextSound_MinusMinDistance()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        { Position = Pos(1), StringIndex = 0, Sustain = new FractionalPosition(2, new Fraction(0, 1)) });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        // Next distinct position is beat 2; max sustain = 1 beat − 1/64.
        Assert.Equal(new FractionalPosition(0, new Fraction(63, 64)), level.Notes[0].Sustain);
    }

    [Fact]
    public void SoundSpacing_EqualPositionRun_BothClampAgainstNextDistinctPosition()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        { Position = Pos(1), StringIndex = 0, Sustain = new FractionalPosition(3, new Fraction(0, 1)) });
        level.Notes.Add(new GuitarNote
        { Position = Pos(1), StringIndex = 1, Sustain = new FractionalPosition(3, new Fraction(0, 1)) });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 2 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        // Neither sound at beat 1 clamps against the OTHER equal-position
        // sound — both clamp against beat 2 (next DISTINCT position).
        var expected = new FractionalPosition(0, new Fraction(63, 64));
        Assert.Equal(expected, level.Notes[0].Sustain);
        Assert.Equal(expected, level.Notes[1].Sustain);
    }

    [Fact]
    public void SoundSpacing_LinkNext_SustainReachesNextSameStringSound()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0, LinkNext = true });
        // Different string in between — must NOT terminate the link.
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });
        level.Notes.Add(new GuitarNote { Position = Pos(3), StringIndex = 0 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.Equal(new FractionalPosition(2, new Fraction(0, 1)), level.Notes[0].Sustain);
    }

    [Fact]
    public void SoundSpacing_LinkNext_DuplicateSameStringPosition_SkipsToNextDistinct()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0, LinkNext = true });
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0 });
        level.Notes.Add(new GuitarNote { Position = Pos(3), StringIndex = 0 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        // The equal-position duplicate on the same string is not a link
        // target; the sustain reaches the next DISTINCT position (beat 3).
        Assert.Equal(new FractionalPosition(2, new Fraction(0, 1)), level.Notes[0].Sustain);
    }

    [Fact]
    public void SoundSpacing_LinkNextWithNoTarget_ZeroesSustain()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, LinkNext = true,
            Sustain = new FractionalPosition(1, new Fraction(0, 1))
        });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.Equal(FractionalPosition.Zero, level.Notes[0].Sustain);
    }

    [Fact]
    public void SoundSpacing_PassOtherNotes_KeepsSustainUnclamped()
    {
        var level = new ChartLevel();
        var sustain = new FractionalPosition(3, new Fraction(0, 1));
        level.Notes.Add(new GuitarNote
        { Position = Pos(1), StringIndex = 0, Sustain = sustain, PassOtherNotes = true });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.Equal(sustain, level.Notes[0].Sustain);
    }

    [Fact]
    public void SoundSpacing_ChordNotes_ParticipateWithChordPosition()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, LinkNext = true });
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 1, Sustain = new FractionalPosition(4, new Fraction(0, 1)) });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 0 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        // Linked chord note reaches the next sound on its string (beat 2).
        Assert.Equal(new FractionalPosition(1, new Fraction(0, 1)), chord.ChordNotes[0].Sustain);
        // Unlinked chord note clamps against the next distinct position.
        Assert.Equal(new FractionalPosition(0, new Fraction(63, 64)), chord.ChordNotes[1].Sustain);
    }

    // ── a shortened sustain must never leave a bend hanging past the note ────
    // (owner report, repeatedly: "the bends go outside of the note sustain").
    // The enforcer runs on every save/edit and shortens sustains; before 0.34.1
    // it left bend points where they were, so a bend authored near the end of a
    // long sustain rendered PAST the shortened note.

    private static GuitarNote NoteWithLateBend(int beat, int stringIndex, FractionalPosition sustain,
        FractionalPosition bendAt)
    {
        var note = new GuitarNote { Position = Pos(beat), StringIndex = stringIndex, Sustain = sustain };
        note.BendValues.Add(new BendValue(Pos(beat), 0)); // origin at the head
        note.BendValues.Add(new BendValue(bendAt, 2));     // a late destination point
        return note;
    }

    [Fact]
    public void SoundSpacing_ShorteningASustain_PullsALateBendPointBackToTheNewEnd()
    {
        var level = new ChartLevel();
        // A 2-beat sustain with a bend point almost at the far end (beat 3 − a
        // hair), then a next note at beat 2 forces the sustain down to ~1 beat.
        var bendAt = new FractionalPosition(2, new Fraction(63, 64));
        level.Notes.Add(NoteWithLateBend(1, 0, new FractionalPosition(2), bendAt));
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        var note = level.Notes[0];
        // Sustain clamped to just under 1 beat (next distinct pos − 1/64)…
        Assert.Equal(new FractionalPosition(0, new Fraction(63, 64)), note.Sustain);
        // …and the invariant holds: no bend point sits past the new end.
        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position),
            "a bend must never lie outside the shortened sustain");
        Assert.Equal(note.Position + note.Sustain, note.BendValues[^1].Position);
    }

    [Fact]
    public void SoundSpacing_ShorteningAChordNoteSustain_ClampsItsBend()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1) };
        var bend = new ChordNote { StringIndex = 0, Sustain = new FractionalPosition(4) };
        bend.BendValues.Add(new BendValue(Pos(1), 0));
        bend.BendValues.Add(new BendValue(new FractionalPosition(4, new Fraction(50, 100)), 3)); // way past
        chord.ChordNotes.Add(bend);
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.False(BendGeometry.HasBendOutsideSustain(chord.ChordNotes[0], chord.Position));
        Assert.Equal(chord.Position + chord.ChordNotes[0].Sustain, chord.ChordNotes[0].BendValues[^1].Position);
    }

    [Fact]
    public void EnforceArrangement_ImportEntryPoint_ClampsAChordBendOverAShortenedSustain()
    {
        // The exact path GP import takes (GpImportApplier calls
        // EnforceArrangement, which runs the RS-compatibility passes THEN the
        // sound-spacing shorten). A chord note bends to a destination near the end
        // of a long sustain; a close following note forces the sustain down. This
        // is the owner's "chord bends go outside the handshape WHEN IMPORTED" case
        // (a /Full chord bend at beat 116 in the reference tab) reproduced end to
        // end through the entry point import actually uses.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1) };
        var bendNote = new ChordNote { StringIndex = 0, Fret = 18, Sustain = new FractionalPosition(2) };
        bendNote.BendValues.Add(new BendValue(Pos(1), 0));
        bendNote.BendValues.Add(new BendValue(new FractionalPosition(2, new Fraction(99, 100)), 2)); // /Full at 99%
        chord.ChordNotes.Add(bendNote);
        level.Chords.Add(chord);
        // A note at beat 2 forces the bend chord's sustain down to ~1 beat.
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());

        var cn = level.Chords[0].ChordNotes[0];
        Assert.True(cn.Sustain < new FractionalPosition(2), "the sustain was shortened by the spacing pass");
        Assert.False(BendGeometry.HasBendOutsideSustain(cn, level.Chords[0].Position),
            "a /Full chord bend must never survive import sitting past the shortened sustain");
    }

    [Fact]
    public void SoundSpacing_UnchangedSustainWithAnInRangeBend_LeavesItAlone()
    {
        var level = new ChartLevel();
        // A lone note (no following sound) keeps its sustain; an in-range bend
        // must be untouched (the clamp is idempotent, not a mangler).
        var note = new GuitarNote { Position = Pos(1), StringIndex = 0, Sustain = new FractionalPosition(2) };
        var inRange = new FractionalPosition(1, new Fraction(1, 2));
        note.BendValues.Add(new BendValue(Pos(1), 0));
        note.BendValues.Add(new BendValue(inRange, 2));
        level.Notes.Add(note);

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.Equal(new FractionalPosition(2), note.Sustain);
        Assert.Equal(inRange, note.BendValues[^1].Position);
    }

    // ── Ignore is DERIVED, so it has to be derivable back to false ───────────
    // The pass only ever wrote `true`, which made it a one-way ratchet: the flag
    // outlived the condition that justified it, and since the enforcer runs on
    // every save AND load the ratchet was monotone over the life of the file. An
    // ignored note is unscored in game and invisible in the editor.

    private static Arrangement ArrangementWith(ChartLevel level, params ChordTemplate[] templates)
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        foreach (var template in templates) arr.ChordTemplates.Add(template);
        arr.Levels.Add(level);
        return arr;
    }

    [Fact]
    public void EnforceArrangement_NoteDraggedOffTheUnplayableFret_LosesTheIgnoreFlag()
    {
        var level = new ChartLevel();
        var note = new GuitarNote { Position = Pos(1), StringIndex = 0, Fret = 24 };
        level.Notes.Add(note);
        var arr = ArrangementWith(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());
        Assert.True(note.Ignore, "fret 24 is off the end of the RS neck and cannot be scored");

        note.Fret = 12;
        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());
        Assert.False(note.Ignore, "the note is playable again, so nothing justifies ignore= any more");
    }

    [Fact]
    public void EnforceArrangement_ChordTemplateLoweredOffTheUnplayableFret_LosesTheIgnoreFlag()
    {
        var template = new ChordTemplate { Id = 0, Name = "High" };
        template.Frets[0] = 22;
        template.Frets[1] = 24;

        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 22 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 24 });
        level.Chords.Add(chord);
        var arr = ArrangementWith(level, template);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());
        Assert.True(chord.Ignore);

        template.Frets[1] = 14;
        chord.ChordNotes[1].Fret = 14;
        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());
        Assert.False(chord.Ignore);
    }

    [Fact]
    public void EnforceArrangement_SeventhFretHarmonicWhoseSustainTheSpacingPassZeroes_IsNotLeftIgnored()
    {
        // The flag is only justified while the harmonic RINGS. The enforcer's own
        // EnforceSoundSpacing zeroes that sustain here (the next sound sits exactly
        // one min-distance away), so deriving the flag BEFORE the spacing pass —
        // as the pass used to — left the chart disagreeing with itself after a
        // single enforce: ignore="1" on a note with no sustain at all.
        var level = new ChartLevel();
        var harmonic = new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 7, Harmonic = HarmonicType.Normal,
            Sustain = new FractionalPosition(1)
        };
        level.Notes.Add(harmonic);
        level.Notes.Add(new GuitarNote { Position = Pos(1, 1, 64), StringIndex = 1 });
        var arr = ArrangementWith(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());

        Assert.Equal(FractionalPosition.Zero, harmonic.Sustain);
        Assert.False(harmonic.Ignore, "a 7th-fret harmonic that no longer rings is playable in RS");
    }

    // ── the clamp must not re-create the duplicate bend positions the
    // compatibility dedupe exists to remove ──────────────────────────────────
    // BendGeometry.ClampBendsToSustain COLLAPSES (every out-of-range point lands
    // on position + Sustain), so a curve with two points past a shortened end
    // produced two BendValues at ONE position — the exact RS-invalid state
    // RemoveOverlappingBendValues had removed three passes earlier, written
    // verbatim by the rscp/RS-XML/SNG writers and re-minted on every save.

    private static IEnumerable<FractionalPosition> BendPositions(IGuitarSound sound)
        => sound.BendValues.Select(b => b.Position);

    [Fact]
    public void SoundSpacing_TwoBendPointsPastTheShortenedEnd_DoNotBothSurviveOnOnePosition()
    {
        var level = new ChartLevel();
        // Rise / hold / release over a 2-beat sustain: the last TWO points sit
        // past the end the spacing pass is about to impose (beat 2 − 1/64).
        var note = new GuitarNote { Position = Pos(1), StringIndex = 0, Sustain = new FractionalPosition(2) };
        note.BendValues.Add(new BendValue(Pos(1), 0));
        note.BendValues.Add(new BendValue(Pos(2, 1, 2), 2));
        note.BendValues.Add(new BendValue(Pos(2, 9, 10), 0));
        level.Notes.Add(note);
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position));
        Assert.Equal(BendPositions(note).Distinct().Count(), note.BendValues.Count);
        // The survivor is the FIRST of the collapsed run — the value the bend was
        // travelling toward when the note was cut short, not the unreachable tail
        // (keeping the last would make a released bend read as never bent).
        Assert.Equal(2, note.BendValues.Count);
        Assert.Equal(2.0, note.BendValues[^1].Step);
    }

    [Fact]
    public void EnforceArrangement_ChordBendCollapsedByTheSpacingPass_LeavesNoDuplicateBendPositions()
    {
        // Through the REAL entry point (save/load/import), so the compatibility
        // dedupe genuinely runs first and the duplicates are demonstrably minted
        // AFTER it rather than merely surviving it.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1) };
        var bent = new ChordNote { StringIndex = 0, Fret = 12, Sustain = new FractionalPosition(3) };
        bent.BendValues.Add(new BendValue(Pos(1), 0));
        bent.BendValues.Add(new BendValue(Pos(2, 1, 4), 1));
        bent.BendValues.Add(new BendValue(Pos(3, 1, 2), 2));
        chord.ChordNotes.Add(bent);
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1 });
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());

        Assert.False(BendGeometry.HasBendOutsideSustain(bent, chord.Position));
        Assert.Equal(BendPositions(bent).Distinct().Count(), bent.BendValues.Count);
    }

    // ── a hand shape leaves the min-distance gap before the next SOUND ───────
    // (owner report: with min distance set, the note sustains around a shape
    // shortened but the shape ran right up to / over the following note).

    [Fact]
    public void HandShapeSpacing_LeavesMinDistanceBeforeNextSound_NotJustNextHandShape()
    {
        var level = new ChartLevel();
        // A shape spanning beats 1..3, then a NOTE (no hand shape) at beat 3.
        level.HandShapes.Add(new HandShape { Position = Pos(1), EndPosition = Pos(3), TemplateId = 0 });
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0 });
        level.Notes.Add(new GuitarNote { Position = Pos(3), StringIndex = 0 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        // Ends 1/64 (the default min distance) before the beat-3 note, not on it.
        Assert.Equal(new FractionalPosition(2, new Fraction(63, 64)), level.HandShapes[0].EndPosition);
    }

    [Fact]
    public void HandShapeSpacing_ShapeEndingInEmptySpace_IsLeftAlone()
    {
        var level = new ChartLevel();
        // Only sound is the shape's own opener; nothing follows for many beats.
        level.HandShapes.Add(new HandShape { Position = Pos(1), EndPosition = Pos(2), TemplateId = 0 });
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(16), DefaultConfig());

        Assert.Equal(Pos(2), level.HandShapes[0].EndPosition);
    }

    // ── FixLinkNexts ─────────────────────────────────────────────────────────

    [Fact]
    public void ADanglingLinkNext_DropsTheFlagButKeepsTheAuthoredSustain()
    {
        // The flag being wrong is not a reason to delete the ring-out. A 2-beat
        // sustain whose link target sat 1 ms off used to become a 0-length note
        // on the next save, irreversibly.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(2), LinkNext = true
        });
        // Next sound on the same string is at beat 4, not at 1 + 2 = 3.
        level.Notes.Add(new GuitarNote { Position = Pos(4), StringIndex = 0, Fret = 7 });
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        Assert.False(level.Notes[0].LinkNext);
        Assert.Equal(new FractionalPosition(2), level.Notes[0].Sustain);
    }

    [Fact]
    public void ALinkNextIntoAChord_DoesNotRewriteTheChordsFret()
    {
        // Overwriting a ChordNote's fret leaves the chord playing a shape its
        // SHARED template does not describe, with nothing repointing TemplateId —
        // the permanent "template drift" state RefretPlanner.IsChordPinned refuses
        // to plan around. Reached whenever a note links/slides into a chord that
        // shares a string with it, which is what the GP importers produce.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var shared = new ChordTemplate { Id = 0, Name = "Shape" };
        shared.Frets[0] = 7;
        shared.Frets[1] = 9;
        arr.ChordTemplates.Add(shared);

        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(1), LinkNext = true, SlideTo = 12
        });
        level.Chords.Add(ChordAt(2, (0, 7, MuteType.None), (1, 9, MuteType.None)));
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        var chord = Assert.Single(level.Chords);
        Assert.Equal(7, chord.ChordNotes[0].Fret);
        Assert.Equal(9, chord.ChordNotes[1].Fret);
        // …and the chord still describes exactly its (untouched) template.
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(7, shared.Frets[0]);
    }

    [Fact]
    public void ALinkNextIntoAStandaloneNote_StillFretsTheContinuation()
    {
        // The rewrite is the RS link-next requirement and stays for plain notes,
        // which carry no template to diverge from.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(1), LinkNext = true, SlideTo = 12
        });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 0, Fret = 3 });
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        Assert.Equal(12, level.Notes[1].Fret);
    }

    // ── anchors ──────────────────────────────────────────────────────────────

    [Fact]
    public void TwoAnchorsSnappingOntoOneSound_CollapseToASingleAnchor()
    {
        // Anchors within 5 ms of a sound are snapped ONTO it, so two anchors a
        // few ms either side of one note both land on its position.
        // RemoveRedundantAnchors only drops a consecutive pair with the SAME fret
        // and width, so two anchors with DIFFERENT frets survived at one
        // FractionalPosition — a state nothing downstream is defined for.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(4), StringIndex = 0, Fret = 5 });

        // BuildBeats is 500 ms/beat, so 1/200 of a beat is 2.5 ms — inside the
        // 5 ms snap window on both sides.
        var offset = new FractionalPosition(new Fraction(1, 200));
        level.Anchors.Add(new FretAnchor { Position = Pos(4) - offset, Fret = 3, Width = 4 });
        level.Anchors.Add(new FretAnchor { Position = Pos(4) + offset, Fret = 7, Width = 4 });
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        var anchor = Assert.Single(level.Anchors);
        Assert.Equal(Pos(4), anchor.Position);
        // The LAST of a collapsed run wins: consumers resolve "the anchor in
        // effect at p" as the last one at or before p.
        Assert.Equal(7, anchor.Fret);
    }

    [Fact]
    public void EnforcingAnAlreadySortedLevel_RaisesNoCollectionChangeForTheSort()
    {
        // The four SortByPosition calls used to assign EVERY slot of every live
        // ObservableCollection, i.e. ~4 x N Replace notifications per level on
        // the UI thread during every save and auto-backup — even though the
        // collections are already sorted in practically every case.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        for (var beat = 1; beat <= 8; beat++)
            level.Notes.Add(new GuitarNote { Position = Pos(beat), StringIndex = beat % 6, Fret = beat });
        level.Chords.Add(ChordAt(9, (0, 3, MuteType.None), (1, 5, MuteType.None)));
        level.Chords.Add(ChordAt(10, (0, 3, MuteType.None), (1, 5, MuteType.None)));
        arr.Levels.Add(level);

        // Settle first: the first enforce legitimately clamps sustains.
        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(16), DefaultConfig());

        var noteEvents = 0;
        var chordEvents = 0;
        level.Notes.CollectionChanged += (_, _) => noteEvents++;
        level.Chords.CollectionChanged += (_, _) => chordEvents++;

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(16), DefaultConfig());

        Assert.Equal(0, noteEvents);
        Assert.Equal(0, chordEvents);
    }

    // ── partially-muted chords must not corrupt a SHARED chord template ──────

    private static Chord ChordAt(int beat, params (int stringIdx, int fret, MuteType mute)[] notes)
    {
        var chord = new Chord { Position = Pos(beat), TemplateId = 0 };
        foreach (var (s, f, m) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Mute = m });
        return chord;
    }

    /// <summary>
    ///     Builds an arrangement where a plain chord and a partially fret-hand-muted
    ///     chord SHARE one template (templates are deduped by shape, so this is the
    ///     normal state of an imported chart — not a contrived one).
    /// </summary>
    private static (Arrangement Arr, ChartLevel Level, ChordTemplate Shared) SharedTemplateFixture()
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var shared = new ChordTemplate { Id = 0, Name = "Shape" };
        shared.Frets[0] = 3;
        shared.Frets[1] = 5;
        shared.Frets[2] = 5;
        shared.Fingers[0] = 1;
        shared.Fingers[1] = 3;
        shared.Fingers[2] = 4;
        arr.ChordTemplates.Add(shared);

        var level = new ChartLevel();
        level.Chords.Add(ChordAt(1, (0, 3, MuteType.None), (1, 5, MuteType.None), (2, 5, MuteType.None)));
        level.Chords.Add(ChordAt(5, (0, 3, MuteType.None), (1, 5, MuteType.None), (2, 5, MuteType.Full)));
        arr.Levels.Add(level);

        return (arr, level, shared);
    }

    [Fact]
    public void PartiallyMutedChord_DoesNotMutateTheSharedTemplate()
    {
        var (arr, level, shared) = SharedTemplateFixture();

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        // THE regression: the pass used to delete string 2 from the resolved
        // template in place, silently shrinking the shape of every OTHER chord
        // and hand shape using it — and it runs on every save, so the damage
        // accumulated.
        Assert.Equal(3, shared.Frets.Count);
        Assert.Equal(5, shared.Frets[2]);
        Assert.Equal(3, shared.Fingers.Count);
        Assert.Equal(4, shared.Fingers[2]);

        var plain = level.Chords.Single(c => c.ChordNotes.Count == 3);
        Assert.Equal(shared.Id, plain.TemplateId);
    }

    [Fact]
    public void PartiallyMutedChord_GetsItsOwnReducedTemplate()
    {
        var (arr, level, shared) = SharedTemplateFixture();

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        var reducedChord = level.Chords.Single(c => c.ChordNotes.Count == 2);
        Assert.NotEqual(shared.Id, reducedChord.TemplateId);

        var reduced = arr.ChordTemplates.Single(t => t.Id == reducedChord.TemplateId);
        Assert.Equal(2, reduced.Frets.Count);
        Assert.Equal(3, reduced.Frets[0]);
        Assert.Equal(5, reduced.Frets[1]);
        Assert.False(reduced.Frets.ContainsKey(2));
        Assert.False(reduced.Fingers.ContainsKey(2));
    }

    [Fact]
    public void PartiallyMutedChord_IsIdempotent_AndDoesNotMintTemplatesPerSave()
    {
        var (arr, _, _) = SharedTemplateFixture();

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());
        var afterFirst = arr.ChordTemplates.Count;

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());
        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        Assert.Equal(2, afterFirst); // the shared one + the reduced one
        Assert.Equal(afterFirst, arr.ChordTemplates.Count);
    }

    // ── a dangling TemplateId resolves to NOTHING, never to a list neighbour ──
    // FindTemplate used to fall back to ChordTemplates[templateId]. The id lookup
    // runs first, so the fallback only ever fired on an ALREADY dangling id — and
    // then answered with an unrelated template picked by list POSITION. Ids stay
    // dense today (every creator is append-only, nothing removes templates), so
    // these fixtures make them sparse on purpose.

    private static ChordTemplate TemplateWithFrets(int id, string name, params (int str, int fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        return template;
    }

    [Fact]
    public void DanglingTemplateId_DoesNotFlagAChordFromAListNeighboursFrets()
    {
        // Sparse ids: 5 and 9. The chord's dangling id 1 indexes the SECOND
        // template, which reaches fret 24 — a shape this chord never plays.
        var low = TemplateWithFrets(5, "Low", (0, 3), (1, 5));
        var high = TemplateWithFrets(9, "High", (0, 22), (1, 24));

        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1), TemplateId = 1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        var arr = ArrangementWith(level, low, high);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());

        Assert.False(chord.Ignore, "a chord must never be scored out of the game by a stranger's frets");
    }

    [Fact]
    public void DanglingTemplateId_DoesNotRepointAPartiallyMutedChordAtAStrangersReducedShape()
    {
        // The far worse consequence: the wrong template is cloned, reduced and the
        // chord permanently repointed at a shape it never played — written
        // straight to the .rscp.
        var first = TemplateWithFrets(5, "Stranger", (0, 12), (1, 14), (2, 14));
        var second = TemplateWithFrets(9, "Other", (0, 3), (1, 5), (2, 5));

        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1), TemplateId = 0 }; // dangles; index 0 == "Stranger"
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Mute = MuteType.Full });
        level.Chords.Add(chord);
        var arr = ArrangementWith(level, first, second);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(8), DefaultConfig());

        Assert.Equal(2, arr.ChordTemplates.Count); // no reduction of the stranger was minted
        Assert.Equal(0, chord.TemplateId);         // still dangling, honestly so
        Assert.Equal(3, first.Frets.Count);        // and the stranger is untouched
    }

    // ── the whole enforcer must be IDEMPOTENT ────────────────────────────────
    // EnforceArrangement runs on every save AND every load, so any pass whose
    // second run disagrees with its first does not fix a chart — it EDITS one, a
    // little more each time the charter hits Ctrl+S. That is the shape of every
    // cumulative-corruption bug this file has had, so the invariant gets its own
    // test over a chart that exercises all five passes at once.

    private static string Snapshot(Arrangement arr)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("phrases:").AppendJoin('|', arr.Phrases).AppendLine();
        foreach (var ep in arr.EventPoints)
        {
            sb.Append("event ").Append(ep.Position).Append(" p=").Append(ep.Phrase)
                .Append(" s=").Append(ep.Section).AppendLine();
        }

        foreach (var t in arr.ChordTemplates)
        {
            sb.Append("template ").Append(t.Id).Append(" n=").Append(t.Name)
                .Append(" arp=").Append(t.Arpeggio).Append('/').Append(t.ForceArpeggioInRs)
                .Append(" frets=").AppendJoin(',', t.Frets.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}"))
                .Append(" fingers=").AppendJoin(',', t.Fingers.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}"))
                .AppendLine();
        }

        foreach (var level in arr.Levels)
        {
            foreach (var n in level.Notes)
                sb.Append("note ").Append(Describe(n, n.Position)).Append(" ignore=").Append(n.Ignore).AppendLine();
            foreach (var c in level.Chords)
            {
                sb.Append("chord ").Append(c.Position).Append(" tpl=").Append(c.TemplateId)
                    .Append(" ignore=").Append(c.Ignore).AppendLine();
                foreach (var cn in c.ChordNotes)
                    sb.Append("  cn ").Append(Describe(cn, c.Position)).Append(" mute=").Append(cn.Mute).AppendLine();
            }

            foreach (var a in level.Anchors)
            {
                sb.Append("anchor ").Append(a.Position).Append(' ').Append(a.Fret).Append('/').Append(a.Width)
                    .AppendLine();
            }

            foreach (var h in level.HandShapes)
            {
                sb.Append("shape ").Append(h.Position).Append("..").Append(h.EndPosition)
                    .Append(" tpl=").Append(h.TemplateId).AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Describe(IGuitarSound sound, FractionalPosition position)
    {
        var bends = string.Join(",", sound.BendValues.Select(b => $"{b.Position}@{b.Step}"));
        return $"{position} str={sound.StringIndex} fret={sound.Fret} sus={sound.Sustain} " +
               $"link={sound.LinkNext} slide={sound.SlideTo} harm={sound.Harmonic} bends=[{bends}]";
    }

    /// <summary>
    ///     A chart that reaches every compatibility pass: names to normalize, a
    ///     bend curve the spacing pass will collapse, a fret-24 note, a 7th-fret
    ///     harmonic whose sustain the spacing pass itself zeroes, a valid and an
    ///     orphaned link-next, a partially muted chord over a SHARED template,
    ///     anchors to snap and de-duplicate, and a hand shape to shorten.
    /// </summary>
    private static Arrangement RichFixture()
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.Phrases.Add("Verse 1!");
        arr.EventPoints.Add(new EventPoint { Position = Pos(1), Phrase = "Solo (fast)", Section = "solo" });

        var shared = new ChordTemplate { Id = 0, Name = "Cmaj-arp" };
        shared.Frets[0] = 3;
        shared.Frets[1] = 5;
        shared.Frets[2] = 5;
        shared.Fingers[0] = 1;
        shared.Fingers[1] = 3;
        shared.Fingers[2] = 4;
        arr.ChordTemplates.Add(shared);

        var level = new ChartLevel();

        // Rise / hold / release whose tail lands past the sustain the spacing pass
        // imposes — the clamp collapses all of it onto one position.
        var bent = new GuitarNote { Position = Pos(1), StringIndex = 0, Fret = 12, Sustain = new FractionalPosition(2) };
        bent.BendValues.Add(new BendValue(Pos(1), 0));
        bent.BendValues.Add(new BendValue(Pos(2, 1, 2), 2));
        bent.BendValues.Add(new BendValue(Pos(2, 9, 10), 0));
        level.Notes.Add(bent);

        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 24 });

        // 7th-fret harmonic whose ring the enforcer's OWN spacing pass removes.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(3), StringIndex = 2, Fret = 7, Harmonic = HarmonicType.Normal,
            Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(new GuitarNote { Position = Pos(3, 1, 64), StringIndex = 3, Fret = 5 });

        // A link-next with a real target, and one with none.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(4), StringIndex = 4, Fret = 9, LinkNext = true,
            Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(new GuitarNote { Position = Pos(5), StringIndex = 4, Fret = 11 });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(6), StringIndex = 5, Fret = 3, LinkNext = true,
            Sustain = new FractionalPosition(1)
        });

        level.Chords.Add(ChordAt(8, (0, 3, MuteType.None), (1, 5, MuteType.None), (2, 5, MuteType.None)));
        level.Chords.Add(ChordAt(9, (0, 3, MuteType.None), (1, 5, MuteType.None), (2, 5, MuteType.Full)));

        level.Anchors.Add(new FretAnchor(Pos(1), 12, 4));
        level.Anchors.Add(new FretAnchor(Pos(4, 1, 1000), 9, 4)); // a hair off the beat-4 note
        level.Anchors.Add(new FretAnchor(Pos(6), 9, 4));          // redundant: same fret/width
        level.Anchors.Add(new FretAnchor(Pos(8), 3, 4));

        level.HandShapes.Add(new HandShape { Position = Pos(8), EndPosition = Pos(10), TemplateId = 0 });

        arr.Levels.Add(level);
        return arr;
    }

    [Fact]
    public void EnforceArrangement_IsIdempotent_TheSecondPassChangesNothing()
    {
        var arr = RichFixture();
        var beats = BuildBeats(16);

        ChartSpacingEnforcer.EnforceArrangement(arr, beats, DefaultConfig());
        var afterFirst = Snapshot(arr);

        ChartSpacingEnforcer.EnforceArrangement(arr, beats, DefaultConfig());
        var afterSecond = Snapshot(arr);

        Assert.Equal(afterFirst, afterSecond);

        // …and it stays put — a chart must not drift over a charting session's
        // worth of saves.
        for (var save = 0; save < 5; save++)
            ChartSpacingEnforcer.EnforceArrangement(arr, beats, DefaultConfig());
        Assert.Equal(afterFirst, Snapshot(arr));
    }

    [Fact]
    public void FullyMutedChord_IsLeftAloneEntirely()
    {
        // A fret-hand-muted strum keeps every note AND its template (the GP
        // import fix of v0.33.2 relies on nothing downstream stripping it).
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var shared = new ChordTemplate { Id = 0, Name = "Shape" };
        shared.Frets[0] = 3;
        shared.Frets[1] = 5;
        arr.ChordTemplates.Add(shared);

        var level = new ChartLevel();
        level.Chords.Add(ChordAt(1, (0, 3, MuteType.Full), (1, 5, MuteType.Full)));
        arr.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arr, BuildBeats(12), DefaultConfig());

        var chord = Assert.Single(level.Chords);
        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.Equal(new[] { 3, 5 }, chord.ChordNotes.Select(cn => cn.Fret).ToArray());
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(2, shared.Frets.Count);
        Assert.Single(arr.ChordTemplates);
    }

    // ---- allocation shape ---------------------------------------------------
    //
    // The enforcer runs after EVERY committed highway edit and inside every
    // collection action's post-pass, so what it allocates is what the editor
    // allocates per keystroke. Structural, metered on THIS thread — never timed.

    /// <summary>
    ///     One <c>EnforceLevel</c> over a 10,000-sound level with hand shapes must
    ///     not allocate per sound.
    ///     <para>
    ///         <c>EnforceHandShapeSpacing</c> used to build a
    ///         <c>List&lt;FractionalPosition&gt;</c> of every note and chord onset
    ///         — the same onsets the pooled sound pass had just sorted — and sort
    ///         them again, plus a <c>handShapes.OrderBy(...).ToList()</c>.
    ///         <c>FractionalPosition</c> is 12 bytes, so that backing array
    ///         crossed the 85,000-byte LOH threshold at 7,084 sounds: 120 KB on
    ///         the LARGE OBJECT HEAP per keystroke on a dense chart, right beside
    ///         the pass that had been rewritten onto <c>ArrayPool</c> precisely
    ///         because "the historical shape allocated ~1 MB per keystroke".
    ///     </para>
    /// </summary>
    [Fact]
    public void EnforceLevel_OnADenseLevel_DoesNotAllocatePerSound()
    {
        var beats = BuildBeats(2_600);
        var config = DefaultConfig();

        var level = new ChartLevel();
        for (var i = 0; i < 10_000; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i / 4, new Fraction(i % 4, 4)),
                StringIndex = i % 6,
                Fret = 1 + i % 12,
                Sustain = new FractionalPosition(new Fraction(1, 8))
            });
        }

        for (var i = 0; i < 500; i++)
        {
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(i * 5),
                EndPosition = new FractionalPosition(i * 5 + 4),
                TemplateId = 0
            });
        }

        ChartSpacingEnforcer.EnforceLevel(level, beats, config); // warm the pools

        var before = GC.GetAllocatedBytesForCurrentThread();
        ChartSpacingEnforcer.EnforceLevel(level, beats, config);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // The old shape was ~120 KB of onset list (LOH) plus the OrderBy buffers
        // for 500 shapes. 16 KB leaves ample room for the pooled arrays' own
        // bookkeeping without leaving room for a per-sound collection.
        Assert.True(bytes < 16 * 1024,
            $"one EnforceLevel over 10,000 sounds allocated {bytes} bytes — the per-sound onset list is back.");
    }

    /// <summary>
    ///     …and the shape it produces has not moved. The hand-shape pass now
    ///     binary-searches the sound pass's own sorted array, so this pins the two
    ///     rules it derives from it: the gap before the next SHAPE and the gap
    ///     before the next SOUND.
    /// </summary>
    [Fact]
    public void AHandShapeStillStopsShortOfTheNextShapeAndTheNextSound()
    {
        var beats = BuildBeats(24);
        var config = DefaultConfig();
        var gap = ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, beats, config);

        var level = new ChartLevel();
        // A shape running straight into the next SOUND.
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        // …and one running into the next SHAPE, over empty bars.
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(12)
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(12), EndPosition = new FractionalPosition(16)
        });

        ChartSpacingEnforcer.EnforceLevel(level, beats, config);

        Assert.Equal(new FractionalPosition(4) - gap, level.HandShapes[0].EndPosition);
        Assert.Equal(new FractionalPosition(12) - gap, level.HandShapes[1].EndPosition);
        Assert.Equal(new FractionalPosition(16), level.HandShapes[2].EndPosition);
    }

    /// <summary>
    ///     A chord with NO chord notes contributes nothing to the pooled sound
    ///     array, but it is still an onset a hand shape has to stop short of — the
    ///     one asymmetry between the old separately-built onset list and the
    ///     shared one.
    /// </summary>
    [Fact]
    public void AChordWithNoChordNotes_IsStillAnOnsetAHandShapeStopsShortOf()
    {
        var beats = BuildBeats(24);
        var config = DefaultConfig();
        var gap = ChartSpacingEnforcer.GetMinDistance(FractionalPosition.Zero, beats, config);

        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4)
        });
        level.Chords.Add(new Chord { Position = new FractionalPosition(4) }); // no chord notes at all

        ChartSpacingEnforcer.EnforceLevel(level, beats, config);

        Assert.Equal(new FractionalPosition(4) - gap, level.HandShapes[0].EndPosition);
    }

    /// <summary>
    ///     A sound with NO sustain is skipped before the clamp is computed
    ///     (0.49): <c>maxSustain</c> is <c>Max(Zero, …)</c>, so a zero sustain
    ///     can never exceed it and the whole computation was dead work for the
    ///     majority of sounds on most charts. The skip must not become a skip of
    ///     anything else — a sound that DOES have sustain is still clamped.
    /// </summary>
    [Fact]
    public void SoundSpacing_LeavesAZeroSustainAloneAndStillClampsARealOne()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0 }); // no sustain
        level.Notes.Add(new GuitarNote
        { Position = Pos(1), StringIndex = 1, Sustain = new FractionalPosition(3, new Fraction(0, 1)) });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 2 });

        ChartSpacingEnforcer.EnforceLevel(level, BuildBeats(8), DefaultConfig());

        Assert.Equal(FractionalPosition.Zero, level.Notes[0].Sustain);
        Assert.Equal(new FractionalPosition(0, new Fraction(63, 64)), level.Notes[1].Sustain);
    }

    /// <summary>
    ///     The derived-ignore pass resolves chord templates through an id map
    ///     built once per level instead of a <c>FirstOrDefault</c> per chord
    ///     (0.49). A map is not a linear search when ids REPEAT, so the rule the
    ///     map must reproduce is FirstOrDefault's: the first template carrying
    ///     the id wins, whatever a later duplicate says.
    /// </summary>
    [Fact]
    public void DerivedIgnore_ResolvesTheFIRSTTemplateCarryingTheId()
    {
        var arrangement = new Arrangement();
        // First wins: fret 24 => the chord must be ignored. The duplicate says
        // otherwise, and a last-wins map would believe it.
        var high = new ChordTemplate { Id = 7 };
        high.Frets[0] = 24;
        var low = new ChordTemplate { Id = 7 };
        low.Frets[0] = 3;
        arrangement.ChordTemplates.Add(high);
        arrangement.ChordTemplates.Add(low);

        var level = new ChartLevel();
        var chord = new Chord { Position = Pos(1), TemplateId = 7 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);

        ChartSpacingEnforcer.EnforceArrangement(arrangement, BuildBeats(8), DefaultConfig());

        Assert.True(chord.Ignore);
        Assert.True(chord.IgnoreDerived);
    }
}
