using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     Builds the representative in-memory chart the EOF export tests share:
///     a lead arrangement covering the technique surface (sustains, mutes,
///     HOPOs, harmonics, slides, bends, vibrato, tremolo, chords with
///     templates/fingers, anchors, handshapes, tone changes, RS events),
///     a capo rhythm arrangement, a 4-string bass arrangement and a vocal
///     track — all on an exact 120 BPM / 4-4 beat grid so positions
///     round-trip losslessly.
/// </summary>
internal static class EofExportTestCharts
{
    public static FractionalPosition Pos(int beat, int num = 0, int den = 192) => new(beat, new Fraction(num, den));

    public static ChartProject BuildRepresentativeProject()
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        project.Chart.SongName = "Round Trip";
        project.Chart.Artist = "The Exporters";
        project.Chart.AlbumName = "Parity";
        project.Chart.AlbumYear = "2026";
        project.Chart.Charter = "DotCharter";
        project.Chart.Genre = "Metal";

        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120.0,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0,
                IsAnchor = i is 0 or 16 // Charter-pinned beats (EOF_BEAT_FLAG_ANCHOR).
            });
        }

        project.Arrangements.Add(BuildLeadArrangement());
        project.Arrangements.Add(BuildCapoRhythmArrangement());
        project.Arrangements.Add(BuildBassArrangement());
        project.VocalTracks.Add(BuildVocalTrack());
        return project;
    }

    private static Arrangement BuildLeadArrangement()
    {
        var arrangement = new Arrangement
        {
            Name = "Lead",
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            StartingTone = "clean"
        };
        arrangement.TuningPitches.Clear();
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };

        // Plain sustained note.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), Sustain = Pos(1), StringIndex = 2, Fret = 5
        });
        // Palm-muted hammer-on.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(2, 96), StringIndex = 1, Fret = 3,
            Mute = MuteType.Palm, Hopo = HopoType.HammerOn
        });
        // Fully muted note.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(3), StringIndex = 0, Fret = 2, Mute = MuteType.Full
        });
        // Tapped harmonic with accent.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(4), StringIndex = 3, Fret = 12,
            Hopo = HopoType.Tap, Harmonic = HarmonicType.Normal, Accent = true
        });
        // Pinch harmonic pull-off with link-next.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(5), StringIndex = 3, Fret = 10,
            Hopo = HopoType.PullOff, Harmonic = HarmonicType.Pinch, LinkNext = true
        });
        // Pitched slide up 5 → 7.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(6), Sustain = Pos(1), StringIndex = 2, Fret = 5, SlideTo = 7
        });
        // Pitched slide down 9 → 6.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(7), Sustain = Pos(1), StringIndex = 2, Fret = 9, SlideTo = 6
        });
        // Unpitched slide out to fret 3.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(8), Sustain = Pos(0, 96), StringIndex = 4, Fret = 7,
            UnpitchedSlide = true, SlideTo = 3
        });
        // Whole-step bend (importer produces the two-point 0 → step curve).
        var bendNote = new GuitarNote
        {
            Position = Pos(9), Sustain = Pos(1), StringIndex = 2, Fret = 7
        };
        bendNote.BendValues.Add(new BendValue(Pos(9), 0));
        bendNote.BendValues.Add(new BendValue(Pos(10), 2.0));
        level.Notes.Add(bendNote);
        // Half-step (quarter-step-encoded) bend.
        var quarterBend = new GuitarNote
        {
            Position = Pos(11), Sustain = Pos(0, 96), StringIndex = 2, Fret = 8
        };
        quarterBend.BendValues.Add(new BendValue(Pos(11), 0));
        quarterBend.BendValues.Add(new BendValue(Pos(11, 96), 1.5));
        level.Notes.Add(quarterBend);
        // Vibrato (importer normalizes strength to 80).
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(12), Sustain = Pos(1), StringIndex = 5, Fret = 5,
            Vibrato = true, VibratoStrength = 80
        });
        // Tremolo run.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(13), Sustain = Pos(0, 96), StringIndex = 0, Fret = 3, Tremolo = true
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(13, 96), Sustain = Pos(0, 96), StringIndex = 0, Fret = 3, Tremolo = true
        });

        // Power chord with fingers.
        var template = new ChordTemplate
        {
            Id = 0,
            Name = "E5",
            Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 1 }, { 1, 3 } }
        };
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = Pos(16), TemplateId = 0, Accent = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Sustain = Pos(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = Pos(1) });
        level.Chords.Add(chord);

        // Chord with a thumb finger + uniform slide (distance +2 on every string).
        var slideTemplate = new ChordTemplate
        {
            Id = 1,
            Name = "Slide",
            Frets = new Dictionary<int, int> { { 0, 3 }, { 2, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 0 }, { 2, 2 } }
        };
        arrangement.ChordTemplates.Add(slideTemplate);
        var slideChord = new Chord { Position = Pos(18), TemplateId = 1, Ignore = true };
        slideChord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Sustain = Pos(1), SlideTo = 5 });
        slideChord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Sustain = Pos(1), SlideTo = 7 });
        level.Chords.Add(slideChord);

        // Anchors (fret hand positions).
        level.Anchors.Add(new FretAnchor(Pos(1), 3));
        level.Anchors.Add(new FretAnchor(Pos(16), 5));

        // Handshape over the power chord.
        level.HandShapes.Add(new HandShape
        {
            Position = Pos(16), EndPosition = Pos(17), TemplateId = 0, Name = "Arpeggio"
        });

        arrangement.Levels.Add(level);

        // Second difficulty so multi-level charts are exercised.
        var hard = new ChartLevel { DifficultyIndex = 1 };
        hard.Notes.Add(new GuitarNote { Position = Pos(1), Sustain = Pos(1), StringIndex = 2, Fret = 5 });
        // Ignore lives in the EOF note's EXTENDED flags (eflags bit 0). It used to be
        // dropped on export with a report claiming it was not representable, so the
        // parity gate could not carry it: the lossless assert tripped first.
        hard.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 3, Fret = 7, Ignore = true });
        arrangement.Levels.Add(hard);

        // Tone changes: default (starting) tone plus a switch.
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(0), ToneName = "clean" });
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(16), ToneName = "lead" });
        arrangement.ToneNames.Add("clean");
        arrangement.ToneNames.Add("lead");

        // RS sections/phrases/events.
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(0), Section = "intro", Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(8), Section = "verse", Phrase = "verse_1" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(16), Phrase = "solo_phrase" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(24), Events = "B0" });
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("verse_1");
        arrangement.Phrases.Add("solo_phrase");
        arrangement.PhraseInfos["solo_phrase"] = new PhraseInfo(0, true);

        return arrangement;
    }

    private static Arrangement BuildCapoRhythmArrangement()
    {
        var arrangement = new Arrangement
        {
            Name = "Rhythm",
            ArrangementType = ArrangementType.Rhythm,
            StringCount = 6,
            Capo = 2
        };
        arrangement.TuningPitches.Clear();
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };
        // Domain frets are capo-inclusive; EOF stores them capo-relative.
        level.Notes.Add(new GuitarNote { Position = Pos(1), Sustain = Pos(1), StringIndex = 0, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 4 });
        level.Anchors.Add(new FretAnchor(Pos(1), 5));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static Arrangement BuildBassArrangement()
    {
        var arrangement = new Arrangement
        {
            Name = "Bass",
            ArrangementType = ArrangementType.Bass,
            StringCount = 4,
            // BOTH halves, because a chart states its tuning twice. This left
            // Arrangement's six-name E-standard DEFAULT in place beside a
            // four-string drop-D offset vector — i.e. it named two strings that
            // do not exist, which is the exact fault the EOF reader's derived
            // letters now fix. A fixture that carries the bug cannot pin it.
            Tuning = ["D", "A", "D", "G"]
        };
        arrangement.TuningPitches.Clear();
        arrangement.TuningPitches.Add(new TuningOffset(-2)); // Drop D low string.
        for (var s = 1; s < 4; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(1), Sustain = Pos(2), StringIndex = 0, Fret = 0,
            BassPicking = BassPickingType.Slap
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(3), StringIndex = 1, Fret = 5, BassPicking = BassPickingType.Pop
        });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static VocalTrack BuildVocalTrack()
    {
        var track = new VocalTrack();
        var phrase = new LyricPhrase { Position = Pos(4), EndPosition = Pos(8) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(4), EndPosition = Pos(5), Text = "Hel", IsWordPart = true, Note = 60
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(5), EndPosition = Pos(6), Text = "lo", Note = 62, IsPhraseEnd = true
        });
        track.Phrases.Add(phrase);
        return track;
    }
}
