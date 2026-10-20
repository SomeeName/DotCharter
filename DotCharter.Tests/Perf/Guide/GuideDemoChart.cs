using DotCharter.Tests.Perf.InspectorCapture;

namespace DotCharter.Tests.Perf.Guide;

/// <summary>
///     The usage guide's song: the Inspector review's project (title, artist, lyrics, annotations,
///     <see cref="InspectorCaptureFixture.Seed" />) with its synthetic notes replaced by a small,
///     playable chart, so the 3D highway and the timeline in the guide look like a real song.
///     120 BPM in 4/4. A G-minor riff with G5/C5 power chords in the fret-3 position (anchor 3-6),
///     a chorus of open G, Em, C and D chords (anchor 2-5, 1-4 under C), a solo in the fret-5 box (anchor 5-8),
///     sustains, linked slides, bends, vibrato and palm mutes, after a one-bar count-in. Every fretted note sits inside its anchor.
///     String 0 is the low E.
/// </summary>
internal static class GuideDemoChart
{
    private const double Bpm = 120;
    private const int BeatCount = 240;

    private static readonly (string Name, Dictionary<int, int> Frets)[] Shapes =
    [
        ("G5", new() { [0] = 3, [1] = 5, [2] = 5 }),
        ("C5", new() { [1] = 3, [2] = 5, [3] = 5 }),
        ("G", new() { [0] = 3, [1] = 2, [2] = 0, [3] = 0, [4] = 0, [5] = 3 }),
        ("Em", new() { [0] = 0, [1] = 2, [2] = 2, [3] = 0, [4] = 0, [5] = 0 }),
        ("C", new() { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }),
        ("D", new() { [2] = 0, [3] = 2, [4] = 3, [5] = 2 })
    ];

    private static readonly (int Beat, string Section, int Anchor, string Tone)[] Sections =
    [
        (4, "Intro", 3, ""), (16, "Verse", 3, ""), (48, "Chorus", 2, ""), (80, "Verse", 3, ""),
        (112, "Chorus", 2, ""), (144, "Solo", 5, "Lead"), (176, "Outro", 3, "Rhythm")
    ];

    public static ChartLevel Seed(ProjectState state)
    {
        var scratch = new ProjectState();
        InspectorCaptureFixture.Seed(scratch);
        var project = scratch.ActiveProject!;
        var arrangement = scratch.ActiveArrangement!;
        var level = arrangement.Levels[0];

        project.Beats.Clear();
        for (var b = 0; b <= BeatCount; b++)
            project.Beats.Add(new BeatMarker { Time = b * 60_000.0 / Bpm, Bpm = Bpm, IsMeasure = b % 4 == 0, BeatsInMeasure = 4 });

        level.Notes.Clear();
        level.Chords.Clear();
        level.HandShapes.Clear();
        level.Anchors.Clear();
        arrangement.ChordTemplates.Clear();
        arrangement.EventPoints.Clear();
        arrangement.ToneChanges.Clear();
        for (var i = 0; i < Shapes.Length; i++)
        {
            var template = new ChordTemplate { Id = i, Name = Shapes[i].Name, Frets = new Dictionary<int, int>(Shapes[i].Frets) };
            ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
            arrangement.ChordTemplates.Add(template);
        }

        arrangement.ToneBase = "Rhythm";
        arrangement.ToneA = "Rhythm";
        arrangement.ToneB = "Lead";
        arrangement.ToneChanges.Add(new ToneChange { Position = FractionalPosition.Zero, ToneName = "Rhythm" });

        // Bar 1 is the count-in; the band comes in on bar 2.
        var chart = new Writer(level, arrangement);
        chart.Chord(4, "G5", 1.75);
        chart.Chord(6, "C5", 1.75);
        Turnaround(chart, 8);
        Verse(chart, 16);
        Chorus(chart, 48);
        Verse(chart, 80);
        Chorus(chart, 112);
        for (var bar = 0; bar < 4; bar++) Solo(chart, 144 + (bar * 8), bar % 2 == 1);
        RiffA(chart, 176, bend: false);
        RiffA(chart, 184, bend: true);
        RiffA(chart, 192, bend: false);
        chart.Chord(200, "G5", 4);

        // A tone switch lands on the free beat before its section, never on a note.
        foreach (var (beat, _, _, tone) in Sections.Where(s => s.Tone.Length > 0))
            arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(beat - 1), ToneName = tone });

        // A phrase every two bars, each restating its hand position (the chorus moves it per chord).
        foreach (var b in new[] { 4 }.Concat(Enumerable.Range(2, 24).Select(i => i * 8)))
        {
            var section = Sections.Last(s => s.Beat <= b);
            arrangement.EventPoints.Add(new EventPoint
            {
                Position = new FractionalPosition(b),
                Phrase = section.Section.ToLowerInvariant(),
                Section = section.Beat == b ? section.Section : ""
            });
            if (section.Section != "Chorus") level.Anchors.Add(new FretAnchor(new FractionalPosition(b), section.Anchor));
        }

        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(204), Phrase = "end", Section = "noguitar" });
        SeedPhrases(arrangement);
        chart.Sort();

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        return level;
    }

    /// <summary>Two bars in the fret-3 position: G5, a run up the D and G strings, C5, a slide and
    /// (every other time) a whole-step bend on the B string.</summary>
    private static void RiffA(Writer chart, int at, bool bend)
    {
        chart.Chord(at, "G5", 1);
        chart.Note(at + 1.5, 2, 3);
        chart.Note(at + 2, 2, 5);
        chart.Note(at + 2.5, 3, 3);
        chart.Note(at + 3, 3, 5, 0.5);
        chart.Chord(at + 4, "C5", 1);
        chart.Note(at + 5.5, 3, 3, 0.5, slideTo: 5);
        chart.Note(at + 6, 3, 5);
        if (bend)
        {
            chart.Note(at + 6.5, 4, 6, 1, bendTo: 2);
        }
        else
        {
            chart.Note(at + 6.5, 4, 3);
            chart.Note(at + 7, 3, 5, 0.5);
        }
    }

    /// <summary>The riff's second time round ends on a pushed G5 into the verse. The guide's shots
    /// look at the note on beat 14 (<c>GuideStages.Select</c>: the first fretted note after 6 s), so
    /// two notes and a chord come up the highway right behind it.</summary>
    private static void Turnaround(Writer chart, int at)
    {
        chart.Chord(at, "G5", 1);
        chart.Note(at + 1.5, 2, 3);
        chart.Note(at + 2, 2, 5);
        chart.Note(at + 2.5, 3, 3);
        chart.Note(at + 3, 3, 5, 0.5);
        chart.Chord(at + 4, "C5", 1.75);
        chart.Note(at + 6, 3, 3);
        chart.Note(at + 6.5, 3, 5);
        chart.Chord(at + 7, "G5", 0.75);
    }

    /// <summary>Palm-muted G on the low E under the riff, twice.</summary>
    private static void Verse(Writer chart, int at)
    {
        for (var half = 0; half < 2; half++)
        {
            var b = at + (half * 16);
            chart.Chord(b, "G5", 0.5);
            chart.Note(b + 1, 0, 3, mute: true);
            chart.Note(b + 1.5, 0, 3, mute: true);
            chart.Note(b + 2, 0, 6);
            chart.Note(b + 2.5, 0, 3, mute: true);
            chart.Note(b + 3, 1, 3);
            chart.Note(b + 3.5, 1, 5);
            chart.Chord(b + 4, "C5", 1.5);
            chart.Note(b + 6, 2, 3);
            chart.Note(b + 6.5, 2, 5);
            chart.Note(b + 7, 3, 3, 0.5);
            RiffA(chart, b + 8, bend: half == 1);
        }
    }

    /// <summary>Open chords, two strums a bar: G, Em, C, D, twice.</summary>
    private static void Chorus(Writer chart, int at)
    {
        string[] bars = ["G", "Em", "C", "D", "G", "Em", "C", "D"];
        for (var bar = 0; bar < bars.Length; bar++)
        {
            var b = at + (bar * 4);
            chart.Chord(b, bars[bar], 1.5, shapeBeats: 3.75);
            chart.Chord(b + 2, bars[bar], 1.5, shape: false);
            chart.Anchor(b, bars[bar] == "C" ? 1 : 2);
        }
    }

    /// <summary>Two bars of the minor-pentatonic box at fret 5: a descending run, a bend on the G
    /// string, a slide on the D string and a held, vibrato note.</summary>
    private static void Solo(Writer chart, int at, bool answer)
    {
        chart.Note(at, 5, 8);
        chart.Note(at + 0.5, 5, 5);
        chart.Note(at + 1, 4, 8);
        chart.Note(at + 1.5, 4, 5);
        chart.Note(at + 2, 3, 7, 1, bendTo: answer ? 1 : 2);
        chart.Note(at + 3.5, 3, 5);
        chart.Note(at + 4, 2, 7);
        chart.Note(at + 4.5, 2, 5, 0.5, slideTo: 7);
        chart.Note(at + 5, 2, 7);
        chart.Note(at + 5.5, 3, 5);
        chart.Note(at + 6, 3, 7, 1.5, vibrato: true);
    }

    private static void SeedPhrases(Arrangement arrangement)
    {
        arrangement.Phrases.Clear();
        arrangement.PhraseIterations.Clear();
        arrangement.Phrases.Add("COUNT");
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        foreach (var point in arrangement.EventPoints.Where(p => p.Phrase.Length > 0))
        {
            var id = arrangement.Phrases.IndexOf(point.Phrase);
            if (id < 0)
            {
                id = arrangement.Phrases.Count;
                arrangement.Phrases.Add(point.Phrase);
            }

            arrangement.PhraseIterations.Add(new PhraseIteration(point.Position, id));
        }
    }

    /// <summary>Beat positions in quarter beats.</summary>
    private static FractionalPosition At(double beats)
    {
        var whole = (int)Math.Floor(beats);
        var quarters = (int)Math.Round((beats - whole) * 4);
        return quarters == 0 ? new FractionalPosition(whole) : new FractionalPosition(whole, new Fraction(quarters, 4));
    }

    private sealed class Writer(ChartLevel level, Arrangement arrangement)
    {
        public void Note(double beat, int stringIndex, int fret, double sustain = 0, int? slideTo = null, double bendTo = 0,
            bool mute = false, bool vibrato = false)
        {
            var note = new GuitarNote
            {
                Position = At(beat),
                StringIndex = stringIndex,
                Fret = fret,
                Sustain = sustain > 0 ? At(sustain) : FractionalPosition.Zero,
                SlideTo = slideTo,
                LinkNext = slideTo is not null,
                Mute = mute ? MuteType.Palm : MuteType.None,
                Vibrato = vibrato
            };
            if (bendTo > 0)
            {
                note.BendValues.Add(new BendValue(At(beat + (sustain / 2)), bendTo));
                note.BendValues.Add(new BendValue(At(beat + sustain), bendTo));
            }

            level.Notes.Add(note);
        }

        public void Chord(double beat, string name, double sustain, bool shape = true, double shapeBeats = 0)
        {
            var template = arrangement.ChordTemplates.Single(t => t.Name == name);
            var chord = new Chord { Position = At(beat), TemplateId = template.Id };
            foreach (var (s, f) in template.Frets.OrderBy(p => p.Key))
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = At(sustain) });
            level.Chords.Add(chord);
            if (shape)
            {
                level.HandShapes.Add(new HandShape
                {
                    Position = At(beat),
                    EndPosition = At(beat + Math.Max(sustain, shapeBeats)),
                    Name = name,
                    TemplateId = template.Id
                });
            }
        }

        public void Anchor(double beat, int fret) => level.Anchors.Add(new FretAnchor(At(beat), fret));

        /// <summary>The renderers binary-search by position: keep every list in time order.</summary>
        public void Sort()
        {
            var notes = level.Notes.OrderBy(n => n.Position).ToList();
            level.Notes.Clear();
            foreach (var n in notes) level.Notes.Add(n);
            var chords = level.Chords.OrderBy(c => c.Position).ToList();
            level.Chords.Clear();
            foreach (var c in chords) level.Chords.Add(c);
            var anchors = level.Anchors.OrderBy(a => a.Position).ToList();
            level.Anchors.Clear();
            foreach (var a in anchors) level.Anchors.Add(a);
            var shapes = level.HandShapes.OrderBy(h => h.Position).ToList();
            level.HandShapes.Clear();
            foreach (var h in shapes) level.HandShapes.Add(h);
        }
    }
}
