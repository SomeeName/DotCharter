namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>Representable musical inputs for a complete, independent EOF round-trip oracle.</summary>
internal static class EofGraceEmissionCases
{
    public static ChartProject Create(int seed)
    {
        var project = new ChartProject();
        for (var i = 0; i < 64; i++) project.Beats.Add(new BeatMarker { Time = i <= 16 ? i * 600 : 9600 + (i - 16) * 300 });
        var arrangement = new Arrangement { Name = "Lead", Capo = seed % 2 * 2, StringCount = 6 + seed % 3, StartingTone = "clean" };
        project.Arrangements.Add(arrangement);
        var ghost = new ChordTemplate { Id = 0, Frets = { [0] = 7, [1] = 9 }, GhostMask = ChordTemplate.GhostBit(1), Fingers = { [1] = 2 } };
        var count = 2 + seed % 5;
        var template = new ChordTemplate { Id = 1, Name = EofGraceChordCases.Names[seed % 5], NopInRs = seed % 2 == 0, Arpeggio = seed % 2 != 0, Fingerless = seed % 7 == 0 };
        for (var i = 0; i < count; i++)
        {
            template.Frets[i] = 7 + i * 2;
            if (!template.Fingerless) template.Fingers[i] = 1 + i % 4;
        }
        arrangement.ChordTemplates.Add(ghost);
        arrangement.ChordTemplates.Add(template);
        for (var difficulty = 0; difficulty < 2; difficulty++)
        {
            var level = new ChartLevel { DifficultyIndex = difficulty };
            arrangement.Levels.Add(level);
            var note = new GuitarNote { StringIndex = 0, Fret = 7, GraceFret = 5, Position = At(project, 600), Sustain = At(project, 1800) - At(project, 600), VibratoStrength = 0, Accent = seed % 3 == 0, Ignore = seed % 11 == 0, PassOtherNotes = seed % 13 == 0 };
            Apply(note, project, 600, seed % 16);
            level.Notes.Add(note);
            var next = seed % 16 == 13 ? 1800 : 2400;
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 9, Position = At(project, next), Sustain = At(project, next + 600) - At(project, next), Tremolo = note.Tremolo, VibratoStrength = 0 });
            var chord = new Chord { TemplateId = 1, Position = At(project, 3600), Accent = seed % 3 == 0, Ignore = seed % 11 == 0, PassOtherNotes = seed % 13 == 0, SplitIntoNotes = seed % 17 == 0 };
            var grace = seed / 5 % count;
            for (var i = 0; i < count; i++)
            {
                var tone = new ChordNote
                {
                    StringIndex = i, Fret = 7 + i * 2, GraceFret = i == grace ? 5 + i * 2 : null,
                    Sustain = At(project, 4800) - chord.Position, VibratoStrength = 0
                };
                Apply(tone, project, 3600, seed / 3 % 16);
                if (seed % 4 < 2) tone.SlideTo = tone.Fret + (i == grace ? 3 : 2);
                // A bend curve and a mixed slide on one string cannot both ride
                // EOF's last-applicable-tech override; that lossy combination is
                // deliberately absent from this lossless corpus, never seed-excluded.
                if (seed % 4 < 2 && tone.BendValues.Count > 2) tone.BendValues.Clear();
                if (seed % 4 < 2) tone.UnpitchedSlide = false;
                if (seed / 3 % 16 == 15 && i != grace && i == (grace + 1) % count) tone.Mute = MuteType.Full;
                chord.ChordNotes.Add(tone);
            }
            if (seed % 2 != 0) chord.ChordNotes = [.. chord.ChordNotes.Reverse()];
            level.Chords.Add(chord);
            if (seed % 4 < 2)
            {
                var copy = chord.Clone();
                var delta = At(project, 3650) - chord.Position;
                copy.Position += delta;
                foreach (var tone in copy.ChordNotes)
                {
                    for (var i = 0; i < tone.BendValues.Count; i++)
                        tone.BendValues[i] = new BendValue(tone.BendValues[i].Position + delta, tone.BendValues[i].Step);
                }
                level.Chords.Add(copy);
            }
            level.HandShapes.Add(new HandShape { Position = At(project, 640), EndPosition = At(project, 1800), TemplateId = 0 });
            level.HandShapes.Add(new HandShape { Position = At(project, 3640), EndPosition = At(project, 4800), TemplateId = 1 });
            level.Anchors.Add(new FretAnchor(At(project, 600), 7));
        }
        arrangement.ToneChanges.Add(new ToneChange { Position = At(project, 600), ToneName = "clean" });
        arrangement.ToneChanges.Add(new ToneChange { Position = At(project, 3600), ToneName = "drive" });
        arrangement.EventPoints.Add(new EventPoint { Position = At(project, 600), Phrase = "verse", Section = "verse", Events = "dna_none" });
        arrangement.Phrases.Add("solo");
        arrangement.PhraseInfos["solo"] = new PhraseInfo(0, true);
        arrangement.PhraseIterations.Add(new PhraseIteration(At(project, 2400), 0));
        if (seed % 4 < 2)
            arrangement.EventPoints.Add(new EventPoint { Position = At(project, 3650), Phrase = "riff", Section = "riff" });
        return project;
    }

    private static FractionalPosition At(ChartProject project, int ms) => EofGraceNeighbourCases.At(project, ms);

    private static void Apply(IGuitarSound sound, ChartProject project, int at, int variant)
    {
        switch (variant)
        {
            case 0: sound.SlideTo = sound.Fret + 2; break;
            case 1: sound.SlideTo = sound.Fret + 2; sound.UnpitchedSlide = true; break;
            case 2:
                sound.BendValues.Add(new BendValue(At(project, at), 1));
                sound.BendValues.Add(new BendValue(At(project, at + 600), 2.5));
                sound.BendValues.Add(new BendValue(At(project, at + 1200), 0));
                break;
            case 3: sound.Tremolo = true; break;
            case 4: sound.Vibrato = true; sound.VibratoStrength = 80; break;
            case 5: sound.Harmonic = HarmonicType.Normal; break;
            case 6: sound.Harmonic = HarmonicType.Pinch; break;
            case 7: sound.Mute = MuteType.Palm; break;
            case 8: sound.Hopo = HopoType.HammerOn; break;
            case 9: sound.Hopo = HopoType.PullOff; break;
            case 10: sound.Hopo = HopoType.Tap; break;
            case 11: sound.BassPicking = BassPickingType.Pop; break;
            case 12: sound.BassPicking = BassPickingType.Slap; break;
            case 13: sound.LinkNext = true; break;
            case 14:
                sound.BendValues.Add(new BendValue(At(project, at), 0));
                sound.BendValues.Add(new BendValue(At(project, at + 1200), 2));
                break;
        }
    }
}
