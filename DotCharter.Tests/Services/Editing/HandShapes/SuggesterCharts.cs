namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     Deterministic charts shaped like a charter's real ones, for the suggester's equivalence and cost
///     tests: arpeggios over one grip, riffs that break grips, strums with and without chord notes,
///     chords spelled as notes sharing a position, fret-hand mutes, slides, sustains that carry a bar
///     across a gap, hand shapes over some phrases and anchors inside others. The notes and chords come
///     out position-sorted, as the editor keeps them.
/// </summary>
internal static class SuggesterCharts
{
    private static readonly int[] Denominators = [2, 3, 4, 4, 6];

    /// <summary>A chart of at least <paramref name="sounds" /> sounds, the same for the same arguments.</summary>
    public static Arrangement Build(int seed, int sounds, int stringCount = 6, int capo = 0,
        ArrangementType type = ArrangementType.Lead)
    {
        var rng = new Random(seed);
        var arrangement = new Arrangement { Name = "Lead", StringCount = stringCount, Capo = capo, ArrangementType = type };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        for (var t = 0; t < 40; t++) arrangement.ChordTemplates.Add(RandomTemplate(rng, t, stringCount));
        AddOddTemplates(arrangement);

        var cursor = new FractionalPosition(0);
        var emitted = 0;
        while (emitted < sounds)
        {
            var start = cursor;
            switch (rng.Next(6))
            {
                case 0 or 1: emitted += Arpeggio(rng, arrangement, level, ref cursor, stringCount); break;
                case 2: emitted += Riff(rng, level, ref cursor, stringCount); break;
                case 3: emitted += Strums(rng, arrangement, level, ref cursor, stringCount); break;
                case 4: emitted += Mixed(rng, arrangement, level, ref cursor, stringCount); break;
                default: cursor += 2 + rng.Next(5); break;
            }

            if (rng.Next(4) == 0 && cursor.CompareTo(start) > 0)
                level.HandShapes.Add(new HandShape { Position = start, EndPosition = cursor });
            if (rng.Next(3) == 0 && cursor.CompareTo(start) > 0)
                level.Anchors.Add(new FretAnchor { Position = start + rng.Next(1, 3), Fret = 1 + rng.Next(10) });
            cursor += rng.Next(3);
        }

        var anchors = level.Anchors.OrderBy(a => a.Position).ToList();
        level.Anchors.Clear();
        foreach (var anchor in anchors) level.Anchors.Add(anchor);
        return arrangement;
    }

    /// <summary>The templates a real chart carries besides plain shapes: arpeggio flavours (either
    /// flag), a ghost-gem template, and the same frets declared twice.</summary>
    private static void AddOddTemplates(Arrangement arrangement)
    {
        var templates = arrangement.ChordTemplates;
        for (var t = 0; t < 40; t++)
        {
            if (t % 7 == 3) templates[t].Arpeggio = true;
            if (t % 11 == 5) templates[t].ForceArpeggioInRs = true;
            if (t % 13 == 6) templates[t].GhostMask = 1;
            if (t % 9 != 4) continue;
            var twin = new ChordTemplate { Id = 100 + t, Name = "Twin" + t, Arpeggio = t % 2 == 0 };
            foreach (var (s, f) in templates[t].Frets) twin.Frets[s] = f;
            templates.Add(twin);
        }
    }

    private static FractionalPosition Step(Random rng, FractionalPosition from)
    {
        var denominator = Denominators[rng.Next(Denominators.Length)];
        return from + new Fraction(1 + rng.Next(denominator), denominator);
    }

    private static Dictionary<int, int> RandomGrip(Random rng, int stringCount)
    {
        var grip = new Dictionary<int, int>();
        var low = rng.Next(0, 10);
        var strings = rng.Next(2, Math.Min(stringCount, 6) + 1);
        var first = rng.Next(0, stringCount - strings + 1);
        for (var s = 0; s < strings; s++) grip[first + s] = rng.Next(4) == 0 ? 0 : low + rng.Next(0, 4);
        return grip;
    }

    private static ChordTemplate RandomTemplate(Random rng, int id, int stringCount)
    {
        var template = new ChordTemplate { Id = id, Name = "T" + id };
        foreach (var (s, f) in RandomGrip(rng, stringCount)) template.Frets[s] = f;
        return template;
    }

    private static GuitarNote Note(FractionalPosition at, int stringIndex, int fret, Random rng) => new()
    {
        Position = at, StringIndex = stringIndex, Fret = fret,
        Sustain = rng.Next(5) == 0 ? new FractionalPosition(0, new Fraction(1 + rng.Next(3), 2)) : FractionalPosition.Zero,
        Mute = rng.Next(12) == 0 ? MuteType.Full : MuteType.None,
        SlideTo = rng.Next(25) == 0 ? fret + 2 : null,
        UnpitchedSlide = rng.Next(60) == 0
    };

    private static int Arpeggio(Random rng, Arrangement arrangement, ChartLevel level, ref FractionalPosition cursor,
        int stringCount)
    {
        var grip = rng.Next(3) == 0 && arrangement.ChordTemplates.Count > 0
            ? new Dictionary<int, int>(arrangement.ChordTemplates[rng.Next(arrangement.ChordTemplates.Count)].Frets)
            : RandomGrip(rng, stringCount);
        var strings = grip.Keys.ToArray();
        if (strings.Length == 0) return 0;
        var length = rng.Next(3, 14);
        for (var i = 0; i < length; i++)
        {
            var s = strings[rng.Next(strings.Length)];
            level.Notes.Add(Note(cursor, s, grip[s], rng));
            // A chord spelled as notes: a second note on the same position.
            if (rng.Next(9) == 0)
            {
                var other = strings[rng.Next(strings.Length)];
                level.Notes.Add(Note(cursor, other, grip[other], rng));
            }

            cursor = Step(rng, cursor);
        }

        return length;
    }

    private static int Riff(Random rng, ChartLevel level, ref FractionalPosition cursor, int stringCount)
    {
        var length = rng.Next(4, 20);
        var box = rng.Next(0, 12);
        for (var i = 0; i < length; i++)
        {
            level.Notes.Add(Note(cursor, rng.Next(stringCount), box + rng.Next(0, rng.Next(3) == 0 ? 7 : 4), rng));
            cursor = Step(rng, cursor);
        }

        return length;
    }

    private static Chord StrumOf(Random rng, FractionalPosition at, Dictionary<int, int> grip, ChordTemplate? template)
    {
        var chord = new Chord { Position = at, TemplateId = template?.Id ?? -1 };
        // A repeat-box chord carries its template and no notes.
        if (template != null && rng.Next(4) == 0) return chord;
        foreach (var (s, f) in grip)
        {
            if (rng.Next(7) == 0) continue;
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s, Fret = f, Mute = rng.Next(15) == 0 ? MuteType.Full : MuteType.None,
                Sustain = rng.Next(4) == 0 ? new FractionalPosition(0, new Fraction(1, 2)) : FractionalPosition.Zero,
                SlideTo = rng.Next(30) == 0 ? f + 2 : null
            });
        }

        return chord;
    }

    private static int Strums(Random rng, Arrangement arrangement, ChartLevel level, ref FractionalPosition cursor,
        int stringCount)
    {
        var template = arrangement.ChordTemplates[rng.Next(arrangement.ChordTemplates.Count)];
        var length = rng.Next(1, 7);
        for (var i = 0; i < length; i++)
        {
            level.Chords.Add(StrumOf(rng, cursor, template.Frets, template));
            cursor = Step(rng, cursor);
        }

        return length;
    }

    private static int Mixed(Random rng, Arrangement arrangement, ChartLevel level, ref FractionalPosition cursor,
        int stringCount)
    {
        var template = arrangement.ChordTemplates[rng.Next(arrangement.ChordTemplates.Count)];
        var strings = template.Frets.Keys.ToArray();
        var length = rng.Next(3, 12);
        var emitted = 0;
        for (var i = 0; i < length; i++)
        {
            if (rng.Next(3) == 0)
            {
                // A chord and a note on the same position: ONE sound.
                level.Chords.Add(StrumOf(rng, cursor, template.Frets, template));
                if (strings.Length > 0 && rng.Next(3) == 0)
                {
                    var s = strings[rng.Next(strings.Length)];
                    level.Notes.Add(Note(cursor, s, template.Frets[s], rng));
                }
            }
            else if (strings.Length > 0)
            {
                var s = strings[rng.Next(strings.Length)];
                level.Notes.Add(Note(cursor, s, template.Frets[s], rng));
            }

            emitted++;
            cursor = Step(rng, cursor);
        }

        return emitted;
    }
}
