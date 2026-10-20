namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     The provenance contract of <see cref="ArrangementPropertiesDeriver" />.
///     <para>
///     The defect this closes: nothing in the editor could author an
///     <c>ArrangementProperties</c> flag, and the only derivation lived inside the
///     RS-XML writer's local clone — so the psarc manifest, which is the artifact the
///     GAME reads, shipped a from-scratch chart declaring no techniques at all, and
///     <c>RsManifestGenerator.CalculateDifficulties</c> rated it accordingly.
///     </para>
/// </summary>
public sealed class ArrangementPropertiesDeriverTests
{
    private static Arrangement NewArrangement(ArrangementType type = ArrangementType.Lead, int strings = 6)
    {
        var arrangement = new Arrangement { ArrangementType = type, StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    private static GuitarNote Note(int stringIndex = 0, int fret = 5) =>
        new() { Position = new FractionalPosition(0), StringIndex = stringIndex, Fret = fret };

    private static Chord Chord(int templateId, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(0), TemplateId = templateId };
        foreach (var (str, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return chord;
    }

    // ── the headline defect ─────────────────────────────────────────────────

    /// <summary>
    ///     A chart authored entirely inside DotCharter must tell the game which
    ///     techniques it uses. Before 0.35 every one of these came out 0.
    /// </summary>
    [Fact]
    public void AFromScratchChart_ReportsEveryTechniqueItActuallyUses()
    {
        var arrangement = NewArrangement();
        var level = arrangement.Levels[0];

        var bend = Note(1, 7);
        bend.BendValues.Add(new BendValue(new FractionalPosition(0), 1.0));
        level.Notes.Add(bend);

        var sustained = Note(0, 5);
        sustained.Sustain = new FractionalPosition(1);
        sustained.Mute = MuteType.Palm;
        sustained.Hopo = HopoType.HammerOn;
        sustained.SlideTo = 7;
        sustained.Vibrato = true;
        sustained.Tremolo = true;
        level.Notes.Add(sustained);

        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 12,
            Harmonic = HarmonicType.Normal, Hopo = HopoType.Tap
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 3, Fret = 9,
            Harmonic = HarmonicType.Pinch, BassPicking = BassPickingType.Slap
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(4), StringIndex = 4, Fret = 4,
            SlideTo = 9, UnpitchedSlide = true
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(5), StringIndex = 5, Fret = 3, Mute = MuteType.Full
        });

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.Bends);
        Assert.Equal(1, derived.Sustain);
        Assert.Equal(1, derived.PalmMutes);
        Assert.Equal(1, derived.Hopo);
        Assert.Equal(1, derived.Slides);
        Assert.Equal(1, derived.Vibrato);
        Assert.Equal(1, derived.Tremolo);
        Assert.Equal(1, derived.Harmonics);
        Assert.Equal(1, derived.Tapping);
        Assert.Equal(1, derived.PinchHarmonics);
        Assert.Equal(1, derived.SlapPop);
        Assert.Equal(1, derived.UnpitchedSlides);
        Assert.Equal(1, derived.FretHandMutes);
        Assert.Equal(1, derived.StandardTuning);
    }

    /// <summary>A sustained CHORD is a sustain — Log Charter counts it, EOF (single
    /// notes only, rs.c:1932-1938) does not. Deliberate divergence, pinned.</summary>
    [Fact]
    public void ASustainedChord_CountsAsSustain()
    {
        var arrangement = NewArrangement();
        var chord = Chord(-1, (0, 3), (1, 5));
        foreach (var note in chord.ChordNotes) note.Sustain = new FractionalPosition(2);
        arrangement.Levels[0].Chords.Add(chord);

        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).Sustain);
    }

    /// <summary>A fret-hand-mute marker is authored technique the RS-XML writer
    /// emits; EOF hardcodes the flag to 0 despite owning the data.</summary>
    [Fact]
    public void AFretHandMuteMarker_SetsTheFlag()
    {
        var arrangement = NewArrangement();
        arrangement.Levels[0].FretHandMutes.Add(new FretHandMute { Position = new FractionalPosition(1) });

        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).FretHandMutes);
    }

    // ── clearing, and the pin ───────────────────────────────────────────────

    /// <summary>
    ///     The derivation is AUTHORITATIVE, not additive: a stale flag an import left
    ///     behind (or a technique the charter deleted) has to go back to 0, or the
    ///     chart claims a technique it does not have.
    /// </summary>
    [Fact]
    public void AFlagWithNoContentBehindIt_IsCleared()
    {
        var arrangement = NewArrangement();
        arrangement.Properties.Bends = 1;
        arrangement.Properties.Tapping = 1;
        arrangement.Properties.PowerChords = 1;
        arrangement.Levels[0].Notes.Add(Note());

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(0, derived.Bends);
        Assert.Equal(0, derived.Tapping);
        Assert.Equal(0, derived.PowerChords);
    }

    /// <summary>
    ///     …unless the charter pinned it. This is the <c>IgnoreDerived</c> role and
    ///     DLC Builder's <c>ArrangementPropertiesOverride</c> shape: opt-in, and then
    ///     nothing recomputes.
    /// </summary>
    [Fact]
    public void ManualOverride_PinsTheTechniqueFlags()
    {
        var arrangement = NewArrangement();
        arrangement.Properties.ManualOverride = true;
        arrangement.Properties.Bends = 1;
        arrangement.Properties.Syncopation = 1;
        arrangement.Levels[0].Notes.Add(Note()); // no bend anywhere

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.Bends);
        Assert.Equal(1, derived.Syncopation);
        Assert.True(derived.ManualOverride);
    }

    /// <summary>
    ///     The tuning is a first-class field, so its summary flag can only ever agree
    ///     with it — the pin deliberately does not cover it (same exclusion DLC
    ///     Builder makes).
    /// </summary>
    [Fact]
    public void StandardTuning_IsDerivedEvenWhenPinned()
    {
        var arrangement = NewArrangement();
        arrangement.Properties.ManualOverride = true;
        arrangement.Properties.StandardTuning = 1;
        arrangement.TuningPitches[0] = new TuningOffset(-2); // drop D

        Assert.Equal(0, ArrangementPropertiesDeriver.Derive(arrangement).StandardTuning);

        var standard = NewArrangement();
        standard.Properties.ManualOverride = true;
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(standard).StandardTuning);
    }

    /// <summary>No note field encodes these four, so they are carried verbatim in
    /// BOTH modes — deriving them would be a guess presented as a fact.</summary>
    [Fact]
    public void TheUndecidableFlags_AreCarriedVerbatimWithoutThePin()
    {
        var arrangement = NewArrangement();
        arrangement.Properties.FingerPicking = 1;
        arrangement.Properties.TwoFingerPicking = 1;
        arrangement.Properties.PickDirection = 1;
        arrangement.Properties.Syncopation = 1;
        arrangement.Levels[0].Notes.Add(Note());

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.False(derived.ManualOverride);
        Assert.Equal(1, derived.FingerPicking);
        Assert.Equal(1, derived.TwoFingerPicking);
        Assert.Equal(1, derived.PickDirection);
        Assert.Equal(1, derived.Syncopation);
    }

    /// <summary>Every flag belongs to exactly one provenance group; a new flag with
    /// no group is a flag nobody owns.</summary>
    [Fact]
    public void TheThreeProvenanceGroups_PartitionEveryFlag()
    {
        var all = typeof(ArrangementProperties)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(int))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToList();

        var grouped = ArrangementPropertiesDeriver.AuthoredFlags
            .Concat(ArrangementPropertiesDeriver.DerivedFlags)
            .Concat(ArrangementPropertiesDeriver.UndecidableFlags)
            .ToList();

        Assert.Equal(31, all.Count);
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Empty(all.Except(grouped));
        Assert.Empty(grouped.Except(all));
    }

    // ── authored, never derived ─────────────────────────────────────────────

    [Theory]
    [InlineData(ArrangementSubtype.Main, 1, 0)]
    [InlineData(ArrangementSubtype.Bonus, 0, 1)]
    [InlineData(ArrangementSubtype.Alternate, 0, 0)]
    public void Represent_And_BonusArr_MirrorTheSubtype(ArrangementSubtype subtype, int represent, int bonus)
    {
        var arrangement = NewArrangement();
        arrangement.Subtype = subtype;
        // Both start wrong, so a pass-through would fail here.
        arrangement.Properties.Represent = 1 - represent;
        arrangement.Properties.BonusArr = 1 - bonus;

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(represent, derived.Represent);
        Assert.Equal(bonus, derived.BonusArr);
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 1, 0, 0)]
    [InlineData(ArrangementType.Rhythm, 0, 1, 0)]
    [InlineData(ArrangementType.Bass, 0, 0, 1)]
    [InlineData(ArrangementType.Combo, 1, 1, 0)]
    public void ThePathFlags_DefaultToTheArrangementType(ArrangementType type, int lead, int rhythm, int bass)
    {
        var derived = ArrangementPropertiesDeriver.Derive(NewArrangement(type));

        Assert.Equal(lead, derived.PathLead);
        Assert.Equal(rhythm, derived.PathRhythm);
        Assert.Equal(bass, derived.PathBass);
    }

    /// <summary>
    ///     The official base game names BOTH of Satch Boogie's guitar arrangements
    ///     "Combo"; only the path flags say which is the Lead path. An authored route
    ///     therefore has to beat the type default.
    /// </summary>
    [Fact]
    public void AnAuthoredPath_BeatsTheTypeDefault()
    {
        var arrangement = NewArrangement(ArrangementType.Combo);
        arrangement.Properties.PathRhythm = 1;

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(0, derived.PathLead);
        Assert.Equal(1, derived.PathRhythm);
        Assert.Equal(ArrangementPropertiesDeriver.RouteRhythm,
            ArrangementPropertiesDeriver.ResolveRouteMask(arrangement));
    }

    /// <summary>EOF gates picked bass on the arrangement really being bass
    /// (rs.c:1788-1792); so does DLC Builder's GUI.</summary>
    [Fact]
    public void BassPick_MirrorsPickedBass_AndOnlyOnBass()
    {
        var bass = NewArrangement(ArrangementType.Bass);
        bass.PickedBass = true;
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(bass).BassPick);

        var guitar = NewArrangement();
        guitar.PickedBass = true;
        guitar.Properties.BassPick = 1;
        Assert.Equal(0, ArrangementPropertiesDeriver.Derive(guitar).BassPick);
    }

    // ── chord-shape flags ───────────────────────────────────────────────────

    [Fact]
    public void APowerChord_IsRecognisedByItsIntervals()
    {
        var arrangement = NewArrangement();
        // E5: low E fret 5 (A) + A string fret 7 (E) — a fifth on adjacent strings.
        arrangement.Levels[0].Chords.Add(Chord(-1, (0, 5), (1, 7)));

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.PowerChords);
        Assert.Equal(0, derived.DropDPower); // not a flat shape
        Assert.Equal(1, derived.DoubleStops); // two adjacent strings
    }

    /// <summary>
    ///     A power chord whose strings share ONE fret is only playable with the lowest
    ///     string dropped — Log Charter's per-chord test. EOF instead ANDs any power
    ///     chord with a whole-track "tuning is drop D" check, which claims drop-D power
    ///     for every ordinary power chord in that tuning.
    /// </summary>
    [Fact]
    public void ADropTunedFlatPowerChord_SetsDropDPower()
    {
        var arrangement = NewArrangement();
        arrangement.TuningPitches[0] = new TuningOffset(-2); // drop D
        arrangement.Levels[0].Chords.Add(Chord(-1, (0, 5), (1, 5)));

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.PowerChords);
        Assert.Equal(1, derived.DropDPower);
        Assert.Equal(0, derived.StandardTuning);
    }

    /// <summary>EOF requires the played strings to be contiguous
    /// (note.c:3006-3032); Log Charter does not. EOF's is the stricter,
    /// non-over-claiming rule.</summary>
    [Fact]
    public void TwoNonAdjacentStrings_AreNotADoubleStop()
    {
        var arrangement = NewArrangement();
        arrangement.Levels[0].Chords.Add(Chord(-1, (0, 5), (3, 5)));

        Assert.Equal(0, ArrangementPropertiesDeriver.Derive(arrangement).DoubleStops);
    }

    /// <summary>On bass EOF re-expresses double stops as fifths-and-octaves and
    /// force-clears doubleStops (rs.c:1980-1987).</summary>
    [Fact]
    public void OnBass_DoubleStopsBecomeFifthsAndOctaves()
    {
        var arrangement = NewArrangement(ArrangementType.Bass, strings: 4);
        arrangement.Levels[0].Chords.Add(Chord(-1, (0, 5), (1, 7)));

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.FifthsAndOctaves);
        Assert.Equal(0, derived.DoubleStops);
    }

    [Fact]
    public void OpenAndBarreAndUnnamedChords_AreRecognised()
    {
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "F",
            Frets = new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 3, [3] = 2, [4] = 1, [5] = 1 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4, [3] = 2, [4] = 1, [5] = 1 }
        });
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1, Name = "", Frets = new Dictionary<int, int> { [0] = 0, [1] = 2 }
        });

        arrangement.Levels[0].Chords.Add(Chord(0, (0, 1), (1, 3), (2, 3), (3, 2), (4, 1), (5, 1)));
        arrangement.Levels[0].Chords.Add(Chord(1, (0, 0), (1, 2)));

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(1, derived.BarreChords);
        Assert.Equal(1, derived.OpenChords);
        Assert.Equal(1, derived.NonStandardChords); // the nameless template
    }

    /// <summary>
    ///     A fully string-muted strum has no pitch, so no pitch-derived flag may fire —
    ///     but it IS a double stop, because EOF counts the note's USED strings and a
    ///     mute does not clear the bit (note.c:3006-3032). Measured: this is the only
    ///     shape rule the EOF corpus disagreed on until it was matched.
    /// </summary>
    [Fact]
    public void AFullyMutedDyad_HasNoPitchFlags_ButIsStillADoubleStop()
    {
        var arrangement = NewArrangement();
        var chord = Chord(-1, (0, 5), (1, 7));
        foreach (var note in chord.ChordNotes) note.Mute = MuteType.Full;
        arrangement.Levels[0].Chords.Add(chord);

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal(0, derived.PowerChords);
        Assert.Equal(0, derived.OpenChords);
        Assert.Equal(1, derived.DoubleStops);
        Assert.Equal(1, derived.FretHandMutes);
    }

    // ── purity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Derive_NeverMutatesTheArrangement()
    {
        var arrangement = NewArrangement();
        arrangement.Properties.Bends = 1;
        arrangement.Levels[0].Notes.Add(Note());

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);
        derived.Tapping = 1;

        Assert.Equal(1, arrangement.Properties.Bends); // not cleared in place
        Assert.Equal(0, arrangement.Properties.Tapping); // not aliased
    }
}
