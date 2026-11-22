using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>Fixed-seed chart inputs, independent of the importer's attachment implementation.</summary>
internal static class EofGraceNeighbourCases
{
    private static readonly int[] Gaps = [0, 1, 2, 10, 75];

    public static ChartProject Create(int seed)
    {
        var random = new Random(0x5EED + seed);
        var project = new ChartProject();
        var bpm = seed % 2 == 0 ? 100 : 200;
        for (var i = 0; i < 96; i++) project.Beats.Add(new BeatMarker { Time = i * 60000.0 / bpm });
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 1 + seed % 3; i++)
        {
            var start = 600 + i * 3600;
            var sustain = i == 0 || random.Next(2) == 0 ? 1200 : 0;
            var s = random.Next(6);
            var grace = Note(project, start, sustain, s, 7);
            grace.GraceFret = 5;
            Technique(grace, seed % 18, start, start + sustain, project);
            level.Notes.Add(grace);
            AddNeighbours(project, arrangement, level, random, start, sustain, s);
            AddContext(project, arrangement, level, start, s);
            AddOrdinaryLinkedPair(project, level, start + 2400, s);
        }
        return EofGraceExtendedCases.Extend(project, seed);
    }

    private static void AddNeighbours(ChartProject project, Arrangement arrangement, ChartLevel level,
        Random random, int start, int sustain, int s)
    {
        foreach (var before in new[] { true, false })
        {
            var gap = Gaps[random.Next(Gaps.Length)];
            var at = before ? start - gap : start + sustain + gap;
            var sameString = random.Next(2) == 0;
            var first = sameString ? s : (s + 1) % 6;
            // Concurrent attacks occupy different strings; a preceding attack on
            // the grace's own string must precede it. Zero-gap adjacent neighbours
            // and zero-gap same-string neighbours after the target end remain covered.
            if (before && gap == 0) first = (s + 1) % 6;
            var length = random.Next(2) == 0 ? 0 : random.Next(1, 301);
            if (random.Next(2) == 0)
            {
                var neighbour = Note(project, at, length, first, 3);
                Technique(neighbour, random.Next(19), at, at + length, project);
                level.Notes.Add(neighbour);
                continue;
            }
            var second = (first + 1) % 6;
            var template = new ChordTemplate { Id = arrangement.ChordTemplates.Count };
            template.Frets[first] = 3;
            template.Frets[second] = 5;
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { Position = At(project, at), TemplateId = template.Id };
            var low = new ChordNote { StringIndex = first, Fret = 3, Sustain = At(project, at + length) - chord.Position };
            var high = new ChordNote { StringIndex = second, Fret = 5, Sustain = low.Sustain };
            Technique(low, random.Next(19), at, at + length, project);
            Technique(high, random.Next(19), at, at + length, project);
            // Distinct slide distances force the exporter's per-string chord tech notes.
            low.SlideTo = 5;
            high.SlideTo = 8;
            if (random.Next(2) == 0)
            {
                chord.ChordNotes.Add(high);
                chord.ChordNotes.Add(low);
            }
            else
            {
                chord.ChordNotes.Add(low);
                chord.ChordNotes.Add(high);
            }
            level.Chords.Add(chord);
        }
    }

    private static void AddContext(ChartProject project, Arrangement arrangement, ChartLevel level, int start, int s)
    {
        var template = new ChordTemplate { Id = arrangement.ChordTemplates.Count, Arpeggio = s % 2 == 0 };
        template.Frets[s] = 7;
        template.Frets[(s + 1) % 6] = 9;
        arrangement.ChordTemplates.Add(template);
        level.HandShapes.Add(new HandShape
        {
            Position = At(project, start + 10), EndPosition = At(project, start + 1200), TemplateId = template.Id
        });
        level.Anchors.Add(new FretAnchor(At(project, start + 2), 3));
        arrangement.ToneChanges.Add(new ToneChange { Position = At(project, start + 1), ToneName = "lead" });
    }

    private static void AddOrdinaryLinkedPair(ChartProject project, ChartLevel level, int start, int s)
    {
        var length = (int)EofGraceTiming.LengthAt(At(project, start), project.Beats);
        var origin = Note(project, start, length, s, 3);
        origin.SlideTo = 5;
        origin.LinkNext = true;
        level.Notes.Add(origin);
        level.Notes.Add(Note(project, start + length, 0, s, 5));
    }

    private static void Technique(IGuitarSound sound, int technique, int start, int end, ChartProject project)
    {
        switch (technique)
        {
            case 0: sound.SlideTo = sound.Fret + 2; break;
            case 1: sound.SlideTo = sound.Fret + 2; sound.UnpitchedSlide = true; break;
            case 2:
                sound.BendValues.Add(new BendValue(At(project, start), 1.25));
                sound.BendValues.Add(new BendValue(At(project, start + (end - start) / 2), 2.5));
                sound.BendValues.Add(new BendValue(At(project, end), 0));
                break;
            case 3: sound.Tremolo = true; break;
            case 4: sound.Vibrato = true; sound.VibratoStrength = 80; break;
            case 5: sound.Harmonic = HarmonicType.Normal; break;
            case 6: sound.Harmonic = HarmonicType.Pinch; break;
            case 7: sound.Mute = MuteType.Palm; break;
            case 8: sound.Hopo = HopoType.HammerOn; break;
            case 9: sound.Hopo = HopoType.PullOff; break;
            case 10: sound.Hopo = HopoType.Tap; break;
            case 11: sound.Accent = true; break;
            case 12: sound.BassPicking = BassPickingType.Pop; break;
            case 13: sound.BassPicking = BassPickingType.Slap; break;
            case 14: sound.LinkNext = true; break;
            case 15: sound.Tremolo = true; sound.Vibrato = true; break;
            case 16: sound.Accent = true; sound.Hopo = HopoType.Tap; break;
            case 18: sound.Mute = MuteType.Full; break;
        }
    }

    // The extended assertion checks all difficulties and single/chord targets.
    public static IEnumerable<GuitarNote> Eligible(ChartProject project) =>
        project.Arrangements[0].Levels.SelectMany(l => l.Notes).Where(n => n.GraceFret != null &&
            Ms(project, n.EndPosition) - Ms(project, n.Position) >= EofGraceTiming.LengthAt(n.Position, project.Beats));

    public static List<string> Issues(ChartProject project, Arrangement imported, bool checkInvented = true, bool mainParser = false) =>
        EofGraceExtendedCases.Issues(project, imported, checkInvented, mainParser);

    public static double Ms(ChartProject project, FractionalPosition position) => position.GetPositionMs(project.Beats);
    public static FractionalPosition At(ChartProject project, int ms) => TimelineMath.PositionFromImportedMs(ms, project.Beats);

    private static GuitarNote Note(ChartProject project, int at, int length, int s, int fret) =>
        new() { StringIndex = s, Fret = fret, Position = At(project, at), Sustain = At(project, at + length) - At(project, at) };
}
