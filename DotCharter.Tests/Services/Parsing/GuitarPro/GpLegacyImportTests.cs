using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// Notation-hint capture from the LEGACY binary Guitar Pro 3/4/5 importer:
/// authored durations (dots + tuplets folded into <c>NotationDuration</c>),
/// before-beat grace ornaments (<c>GraceFret</c>) and GP5's second voice
/// (a notation-only <see cref="NotationVoice"/> sidecar). Before v0.25 the
/// binary path produced chart notes but dropped every one of these.
/// <para>
/// Fixture provenance: GP4/GP3 test files are authored by
/// <see cref="LegacyGpFixtureBuilder"/> byte-for-byte against the shipped
/// reader's own field order (no real .gp3/.gp4 existed on the build machine);
/// each test self-validates the writer by asserting the reader reads the notes
/// back. The GP5 path is additionally validated against the real v5.10 file
/// <c>Welcome to Guitar Tab Player.gp5</c> when present (see
/// <see cref="RealGp5File_CapturesAuthoredDurationsOnEveryScoreNote"/>).
/// </para>
/// </summary>
public sealed class GpLegacyImportTests
{
    private static Arrangement ImportSingle(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(arrangements);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Tied notes: continuation-type retarget + merge (v0.26 fix) ----

    [Theory]
    [InlineData("4.06")]
    [InlineData("3.00")]
    public void TiedNotes_MergeIntoOneLongNote(string version)
    {
        // Half fret 5 + tied half (binary note-type 2 on the SECOND note) +
        // a chain across a barline: the tie flag must retarget onto the
        // PREVIOUS note's LinkNext so MergeTiedNotes folds the chain into one
        // long note. Before the fix the flag sat on the continuation itself,
        // so legacy ties never merged and spuriously linked whatever followed.
        var m1 = new FxMeasure().AddVoice(
            FxBeat.Note(-1, stringIndex: 0, fret: 5), // half
            new FxBeat { Duration = -1, Notes = { new FxNote(0, 5, Tied: true) } }); // tied half
        var m2 = new FxMeasure().AddVoice(
            new FxBeat { Duration = -2, Notes = { new FxNote(0, 5, Tied: true) } }); // tied whole across the bar

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { m1, m2 }));
        var level = Assert.Single(arr.Levels);

        var note = Assert.Single(level.Notes);
        Assert.Equal(5, note.Fret);
        Assert.Equal(new FractionalPosition(0), note.Position);
        // half + half + whole = 8 quarter-beats of sustain.
        Assert.Equal(new FractionalPosition(8), note.Position + note.Sustain);
        // The chain is fully consumed — no dangling link out of the merge.
        Assert.False(note.LinkNext);
    }

    [Theory]
    [InlineData("4.06")]
    public void TieFlag_DoesNotLeakOntoFollowingUnrelatedNote(string version)
    {
        // Tied pair then an unrelated note on the same string: the old bug set
        // LinkNext on the CONTINUATION, linking it forward to the unrelated
        // note. After the merge the long note must NOT link onward.
        var measure = new FxMeasure().AddVoice(
            FxBeat.Note(0, stringIndex: 0, fret: 5), // quarter
            new FxBeat { Duration = 0, Notes = { new FxNote(0, 5, Tied: true) } }, // tied quarter
            FxBeat.Note(0, stringIndex: 0, fret: 7), // unrelated
            FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(5, notes[0].Fret);
        Assert.Equal(new FractionalPosition(2), notes[0].Position + notes[0].Sustain);
        Assert.False(notes[0].LinkNext);
        Assert.Equal(7, notes[1].Fret);
    }

    // ---- NotationDuration: dot / tuplet folding on the binary path ----

    [Theory]
    [InlineData("4.06")]
    [InlineData("3.00")]
    public void AuthoredDurations_FoldDotsAndTuplets(string version)
    {
        // One 4/4 bar: quarter, eighth, dotted quarter, eighth-triplet — all on
        // the low E string so they stay four independent notes.
        var measure = new FxMeasure().AddVoice(
            FxBeat.Note(0, stringIndex: 0, fret: 3), // quarter
            FxBeat.Note(1, stringIndex: 0, fret: 5), // eighth
            FxBeat.Note(0, stringIndex: 0, fret: 7, dotted: true), // dotted quarter
            FxBeat.Note(1, stringIndex: 0, fret: 8, tuplet: 3)); // eighth triplet

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(4, notes.Count);
        Assert.Equal(new[] { 3, 5, 7, 8 }, notes.Select(n => n.Fret));
        Assert.Equal(new Fraction(1, 1), notes[0].NotationDuration); // quarter
        Assert.Equal(new Fraction(1, 2), notes[1].NotationDuration); // eighth
        Assert.Equal(new Fraction(3, 2), notes[2].NotationDuration); // dotted quarter
        Assert.Equal(new Fraction(1, 3), notes[3].NotationDuration); // eighth triplet
    }

    /// <summary>
    ///     A beat's tuplet field is a raw <c>ReadInt32</c> guarded only by
    ///     <c>&gt; 1</c>, so header bit <c>0x20</c> plus <c>0x40000000</c> is a
    ///     ~200-byte file that pinned the import thread at 100% CPU forever —
    ///     <c>PreviousPowerOfTwo</c>'s doubling loop overflowed int and never
    ///     terminated. No exception, no timeout, no cancellation. The gpif reader
    ///     had bounded the same ratio all along; this path had not.
    ///     <para>
    ///         Driven on a worker with a watchdog for the same reason the unit
    ///         test is: unfixed this HANGS, and a hung gate reports nothing.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1 << 30)]
    [InlineData(int.MaxValue)]
    public async Task AnAbsurdTupletValue_ImportsInsteadOfHanging(int tuplet)
    {
        var measure = new FxMeasure().AddVoice(
            new FxBeat { Duration = 1, Tuplet = tuplet, Notes = { new FxNote(0, 5) } },
            FxBeat.RestBeat(0));
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure });

        var work = Task.Run(() =>
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
        });

        try
        {
            // A TimeoutException here IS the regression: the import thread is
            // stuck inside PreviousPowerOfTwo and never comes back.
            var arrangements =
                await work.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var arr = Assert.Single(arrangements);
            Assert.NotEmpty(arr.Levels);
        }
        finally
        {
            // Best effort: a still-spinning worker keeps the handle open, and an
            // IOException here would mask the timeout that is the real finding.
            BestEffortDelete.File(path);
        }
    }

    [Fact]
    public void ChordBeat_CarriesAuthoredDuration_TrimmedWithHandshape()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(0, new FxNote(0, 3), new FxNote(1, 5))); // quarter dyad

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile("4.06", new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var chord = Assert.Single(level.Chords);
        Assert.Equal(new Fraction(1, 1), chord.NotationDuration);
        Assert.Equal(2, chord.ChordNotes.Count);
        // Auto-handshape charting pass: a plain chord trims to a short strike and
        // the handshape carries the authored quarter-note duration.
        Assert.All(chord.ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));
        var hs = Assert.Single(level.HandShapes);
        Assert.Equal(chord.Position, hs.Position);
        Assert.Equal(new FractionalPosition(1), hs.EndPosition);
    }

    // ---- Grace notes ----

    [Fact]
    public void GraceNote_BecomesGraceFretHint_OnTheSameNote()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Note(0, stringIndex: 2, fret: 7, grace: 5));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile("4.06", new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var note = Assert.Single(level.Notes);
        Assert.Equal(7, note.Fret);
        Assert.Equal(2, note.StringIndex);
        Assert.Equal(5, note.GraceFret); // notation ornament captured, not dropped
    }

    // ---- GP5 second voice → notation sidecar ----

    [Fact]
    public void Gp5SecondVoice_BecomesNotationSidecar_WithAuthoredDurationsVerbatim()
    {
        var measure = new FxMeasure();
        // Voice 0 (playable): four quarters on the D string.
        measure.AddVoice(
            FxBeat.Note(0, 2, 5),
            FxBeat.Note(0, 2, 7),
            FxBeat.Note(0, 2, 5),
            FxBeat.Note(0, 2, 7));
        // Voice 1 (GP5 second voice): two distinct half notes on the low E string.
        measure.AddVoice(
            FxBeat.Note(-1, 0, 3),
            FxBeat.Note(-1, 0, 5));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }));
        var level = Assert.Single(arr.Levels);

        // The chart level holds ONLY voice 0.
        Assert.Equal(4, level.Notes.Count);
        Assert.All(level.Notes, n => Assert.Equal(2, n.StringIndex));

        // Voice 1 became one notation-only sidecar (voice index 1).
        var sidecar = Assert.Single(arr.NotationVoices);
        Assert.Equal(1, sidecar.VoiceIndex);
        var sideNotes = sidecar.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, sideNotes.Count);
        Assert.Equal(new[] { 3, 5 }, sideNotes.Select(n => n.Fret));
        // Authored half-note duration kept verbatim (AdjustSustains must NOT run
        // on the sidecar), and the full sustain survives with it.
        Assert.All(sideNotes, n => Assert.Equal(new Fraction(2, 1), n.NotationDuration));
        Assert.Equal(new FractionalPosition(0), sideNotes[0].Position);
        Assert.Equal(new FractionalPosition(2), sideNotes[1].Position);
        Assert.All(sideNotes, n => Assert.Equal(new FractionalPosition(2), n.Sustain));
    }

    [Fact]
    public void Gp5SingleVoiceFile_ProducesNoSidecar()
    {
        var measure = new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }));

        Assert.Single(Assert.Single(arr.Levels).Notes);
        Assert.Empty(arr.NotationVoices);
    }

    // ---- Malformed / truncated inputs: fail soft (throw, never hang/crash) ----

    [Fact]
    public void TruncatedFile_ThrowsCatchableException_DoesNotHang()
    {
        var full = File.ReadAllBytes(LegacyGpFixtureBuilder.WriteFile("5.10",
            new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5), FxBeat.Note(0, 0, 7)) }));
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gptrunc-{Guid.NewGuid():N}.gp5");
        File.WriteAllBytes(path, full.Take(full.Length / 2).ToArray()); // cut mid-stream

        try
        {
            // The truncation lands mid-header, so either the track scan or the
            // full import runs off the end of the stream — both must surface a
            // catchable exception (EOF), never a hang or a process crash.
            Assert.ThrowsAny<Exception>(() =>
            {
                var importer = new GpImporter();
                var tracks = importer.GetAvailableTracks(path);
                importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GarbageFile_ThrowsOnVersionParse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpgarbage-{Guid.NewGuid():N}.gp4");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xAB, 64).ToArray());

        try
        {
            Assert.ThrowsAny<Exception>(() => new GpImporter().GetAvailableTracks(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyFile_ThrowsCatchableException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpempty-{Guid.NewGuid():N}.gp3");
        File.WriteAllBytes(path, []);

        try
        {
            Assert.ThrowsAny<Exception>(() => new GpImporter().GetAvailableTracks(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Real-artifact validation (skips when the file is absent, e.g. CI) ----

    [Fact]
    public void RealGp5File_CapturesAuthoredDurationsOnEveryScoreNote()
    {
        // Real Guitar Pro 5.10 export shipped with Ample Sound. Absent on CI, so
        // this is a soft skip there; on the dev machine it is the external
        // ground-truth for the GP5 NotationDuration capture.
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Ample Sound", "Tabs", "Guitar", "Welcome to Guitar Tab Player.gp5");
        Assert.SkipWhen(!File.Exists(path),
            $"No GP5 duration fixture at '{path}' (third-party tab, cannot be committed).");

        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(path);
        var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

        var arr = Assert.Single(result.Arrangements);
        var level = Assert.Single(arr.Levels);
        Assert.NotEmpty(level.Notes);
        Assert.All(level.Notes, n => Assert.NotNull(n.NotationDuration));
        Assert.All(level.Chords, c => Assert.NotNull(c.NotationDuration));

        var distinct = level.Notes.Select(n => n.NotationDuration!.Value).Distinct().ToList();
        Assert.True(distinct.Count >= 3, "expected several distinct authored rhythms");
        Assert.Contains(new Fraction(3, 2), distinct); // the file uses a dotted quarter
    }

    // ---- Accents: note-header bit 0x40 (v0.33 fix — was silently dropped) ----

    [Theory]
    [InlineData("5.10")]
    [InlineData("4.06")]
    [InlineData("3.00")]
    public void AccentedNotesAndChords_CarryAccent(string version)
    {
        var accentedChord = FxBeat.Chord(0, new FxNote(0, 5, Accent: true), new FxNote(1, 7));
        var measure = new FxMeasure().AddVoice(
            new FxBeat { Duration = 0, Notes = { new FxNote(0, 3, Accent: true) } },
            accentedChord,
            FxBeat.Note(0, stringIndex: 0, fret: 9),
            FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);
        Assert.True(notes[0].Accent, "accented single note lost its accent");
        Assert.False(notes[1].Accent);

        var chord = Assert.Single(level.Chords);
        Assert.True(chord.Accent, "an accent on any chord string must accent the strum");
        Assert.True(chord.ChordNotes.Single(cn => cn.StringIndex == 0).Accent);
        Assert.False(chord.ChordNotes.Single(cn => cn.StringIndex == 1).Accent);
    }

    // ---- Beat-level vibrato / natural harmonic (v0.33 fix — was ignored) ----

    [Theory]
    [InlineData("5.10")]
    [InlineData("4.06")]
    [InlineData("3.00")]
    public void BeatLevelVibratoAndHarmonic_FoldOntoTheBeatsNotes(string version)
    {
        // GP binary authors chord vibrato on the BEAT (effects fx1 bit 0x01),
        // not per note; same for the beat-level natural harmonic (0x04). EOF
        // folds both onto every note of the beat — so must we.
        var vibratoChord = FxBeat.Chord(0, new FxNote(0, 5), new FxNote(1, 7));
        vibratoChord.BeatVibrato = true;
        var harmonicNote = FxBeat.Note(0, stringIndex: 0, fret: 12);
        harmonicNote.BeatNaturalHarmonic = true;
        var measure = new FxMeasure().AddVoice(
            vibratoChord,
            harmonicNote,
            FxBeat.Note(0, stringIndex: 0, fret: 9),
            FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, cn => Assert.True(cn.Vibrato, "beat vibrato must reach every chord note"));

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(HarmonicType.Normal, notes[0].Harmonic);
        Assert.False(notes[0].Vibrato);
        Assert.Equal(HarmonicType.None, notes[1].Harmonic);
    }

    // ---- Tremolo picking vs. the tremolo BAR, trills, slap/pop ----
    //
    // All three were losses in the binary path only, i.e. the same score
    // imported differently as .gp/.gp5 than as .gpif.

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void TremoloPicking_ComesFromTheNote_NotFromTheWhammyBar(string version)
    {
        // Bit 0x04 means two DIFFERENT things in the two fx2 bytes: the BEAT's
        // is the tremolo BAR (whammy), the NOTE's is tremolo PICKING. They were
        // crossed — the whammy was stamped onto every note of the beat as
        // Tremolo while the real picking byte was read and thrown away.
        var picked = FxBeat.Note(0, stringIndex: 0, fret: 5);
        picked.Notes[0] = new FxNote(0, 5, TremoloPicking: true);

        var whammy = FxBeat.Note(0, stringIndex: 1, fret: 7);
        whammy.BeatTremoloBar = true;

        var measure = new FxMeasure().AddVoice(picked, whammy, FxBeat.RestBeat(-1));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        Assert.Equal(5, notes[0].Fret);
        Assert.True(notes[0].Tremolo, "the note's own tremolo-picking flag must survive");

        Assert.Equal(7, notes[1].Fret);
        Assert.False(notes[1].Tremolo, "a whammy dip is not tremolo picking");
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Trill_ExpandsIntoAlternatingHammerOnPullOff(string version)
    {
        // The binary reader used to Skip(2) the trill's (fret, period) bytes, so
        // a trilled score lost it entirely — while the gpif path expands it
        // through the very same helper this now shares.
        var trilled = FxBeat.Note(-1, stringIndex: 0, fret: 5); // half note
        trilled.Notes[0] = new FxNote(0, 5, TrillUpperFret: 7);
        var measure = new FxMeasure().AddVoice(trilled, FxBeat.RestBeat(-1));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);
        var notes = level.Notes.OrderBy(n => n.Position).ToList();

        // A half note at 32nd-note steps is 16 alternations, not one note.
        Assert.True(notes.Count > 4, $"expected an expanded trill run, got {notes.Count} note(s)");
        Assert.Contains(notes, n => n.Fret == 5);
        Assert.Contains(notes, n => n.Fret == 7);
        Assert.All(notes, n => Assert.Equal(0, n.StringIndex));
        // Everything after the first strike is hammered/pulled, never picked.
        Assert.Equal(HopoType.None, notes[0].Hopo);
        Assert.All(notes.Skip(1), n => Assert.NotEqual(HopoType.None, n.Hopo));
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void BeatSlapAndPop_ReachTheBeatsSounds(string version)
    {
        // GP4/5 string-effect type: 1 tapping, 2 slap, 3 pop. Only tapping was
        // read; slap and pop were dropped — on bass, where they matter most.
        var slapped = FxBeat.Note(0, stringIndex: 0, fret: 3);
        slapped.BeatStringEffect = 2;
        var popped = FxBeat.Note(0, stringIndex: 1, fret: 5);
        popped.BeatStringEffect = 3;
        var poppedChord = FxBeat.Chord(0, new FxNote(0, 3), new FxNote(1, 5));
        poppedChord.BeatStringEffect = 3;
        var plain = FxBeat.Note(0, stringIndex: 0, fret: 7);

        var measure = new FxMeasure().AddVoice(slapped, popped, poppedChord, plain);

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(3, notes.Count);

        Assert.Equal(BassPickingType.Slap, notes[0].BassPicking);
        Assert.Equal(BassPickingType.Pop, notes[1].BassPicking);
        Assert.Equal(BassPickingType.None, notes[2].BassPicking);

        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, cn => Assert.Equal(BassPickingType.Pop, cn.BassPicking));
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void BeatTap_ImportsAsTapNotHammerOn(string version)
    {
        // String-effect type 1 is RIGHT-hand tapping — the same technique gpif
        // spells as the note property `Tapped`, which GpNoteParser maps to
        // HopoType.Tap. This reader claimed HopoType.HammerOn instead (its comment
        // said "matching the GP7/8 path", which it never did), and that cost three
        // things: the SNG never set RsNoteMask.Tap, FretAnchorGenerationService's
        // tap-run rule keys on HopoType.Tap and so was dead for every binary-format
        // tapping passage, and AdjustSustains re-derived every DESCENDING tapped
        // note into a pull-off.
        var tapHigh = FxBeat.Note(0, stringIndex: 0, fret: 7);
        tapHigh.BeatStringEffect = 1;
        var tapLow = FxBeat.Note(0, stringIndex: 0, fret: 5); // descending: the flip case
        tapLow.BeatStringEffect = 1;
        var plain = FxBeat.Note(0, stringIndex: 0, fret: 3);

        var measure = new FxMeasure().AddVoice(tapHigh, tapLow, plain, FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(3, notes.Count);

        Assert.Equal(HopoType.Tap, notes[0].Hopo);
        // The descending one stays a TAP: AdjustSustains only re-derives a
        // HammerOn, which is why Tap needs no tapOriginated exemption here (the
        // gpif path passes that set solely for its LEFT-hand taps, which it
        // normalises to hammer-ons deliberately).
        Assert.Equal(HopoType.Tap, notes[1].Hopo);
        // The control: an untapped beat claims nothing.
        Assert.Equal(HopoType.None, notes[2].Hopo);
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void StaccatoBit_BecomesAnAccent(string version)
    {
        // Note-effects fx2 bit 0x01 is GP's authored staccato. It was parsed and
        // DISCARDED (`if ((fx2 & 0x01) != 0) { /* staccato */ }`), so the
        // shortening the arranger wrote reached nothing at all. Rocksmith has no
        // staccato mark, so the charting convention is the ACCENT — playback is
        // already built on that equivalence (JuceAudioPlaybackService shortens
        // exactly the accented notes). It is ungated because every other accent on
        // both readers is: the note header's 0x40/0x02 bits here and gpif's
        // <Accent> element have no setting in front of them, and a rule applied on
        // one tab path only is what the legacy-vs-gpif parity rule forbids.
        var staccato = FxBeat.Note(0, stringIndex: 0, fret: 5);
        staccato.Notes[0] = new FxNote(0, 5, Staccato: true);
        var plain = FxBeat.Note(0, stringIndex: 0, fret: 7);
        var staccatoChord = FxBeat.Chord(0,
            new FxNote(0, 3, Staccato: true), new FxNote(1, 3, Staccato: true));

        var measure = new FxMeasure().AddVoice(staccato, plain, staccatoChord, FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        Assert.True(notes[0].Accent, "the staccato bit must reach the note");
        // The control: a note WITHOUT the bit is not accented — the flag has to be
        // read per note, not stamped onto the beat or leaked to the next one.
        Assert.False(notes[1].Accent);

        // ...and it must reach a STRUM's notes, which is how the chord-level
        // accent is rolled up.
        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, cn => Assert.True(cn.Accent));
        Assert.True(chord.Accent);
    }

    // ---- Chord diagrams: the 107-byte new-format block (v0.33 fix) ----

    [Theory]
    [InlineData("5.10")]
    [InlineData("5.00")]
    [InlineData("4.06")]
    public void ChordDiagramBeat_KeepsTheBeatStreamAligned(string version)
    {
        // A beat carrying a new-format chord diagram is followed by a plain
        // note. The diagram block is 106 bytes after its format byte; the
        // importer used to skip 105 (the 5-byte name→base-fret gap read as 4),
        // so every byte after the first diagram was read one off — the next
        // strings mask landed on the display-fingering byte and the parse blew
        // up (or silently produced garbage) further down the file.
        var diagramChord = FxBeat.Chord(0, new FxNote(0, 5), new FxNote(1, 7));
        diagramChord.ChordDiagramName = "E7sus2";
        var measure = new FxMeasure().AddVoice(
            diagramChord,
            FxBeat.Note(0, stringIndex: 0, fret: 9),
            FxBeat.RestBeat(0),
            FxBeat.RestBeat(0));

        var arr = ImportSingle(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var level = Assert.Single(arr.Levels);

        var chord = Assert.Single(level.Chords);
        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.Equal(5, chord.GetFretForString(0));
        Assert.Equal(7, chord.GetFretForString(1));

        var note = Assert.Single(level.Notes);
        Assert.Equal(9, note.Fret);
        Assert.Equal(0, note.StringIndex);
    }

    [Fact]
    public void RealGp5File_WithChordDiagrams_ParsesToTheEnd()
    {
        // The 156-bar v5.10 tab whose 26 chord diagrams crashed the importer
        // with EndOfStreamException before the 107-byte fix (soft skip when the
        // local-only TestData copy is absent).
        var path = LocalFixtures.TestData("TABS.gp5") ?? Path.Combine("TestData", "TABS.gp5");
        Assert.SkipWhen(!File.Exists(path),
            $"No chord-diagram fixture at '{path}' (TestData/ is git-ignored) — the 107-byte " +
            "chord-diagram regression net is DARK on this machine.");

        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(path);
        var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

        var arr = Assert.Single(result.Arrangements);
        Assert.Equal(156, result.Metadata.Bars.Count);
        var level = Assert.Single(arr.Levels);
        Assert.True(level.Notes.Count + level.Chords.Count > 500,
            $"expected the full tab's sounds, got {level.Notes.Count} notes + {level.Chords.Count} chords");
    }
}
