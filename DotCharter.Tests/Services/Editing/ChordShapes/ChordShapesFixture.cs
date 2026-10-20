namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     Two levels over shared templates: two plain "Am" grips (one with the suggested fingering,
///     one with a finger moved) with the same frets (duplicates),
///     an RS-XML arpeggio on the same frets (NOT a duplicate of either), a C with no fingering, an
///     open-string shape, and an unused G. Level 1 is the one on screen.
/// </summary>
internal sealed class ChordShapesFixture
{
    public static readonly Dictionary<int, int> AmFrets = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 };

    public ChordShapesFixture()
    {
        // Am wears exactly the suggester's fingering; AmAlt the same with one finger moved.
        Am = Template(0, "Am", AmFrets, []);
        ChordTemplateFingerSetter.SetSuggestedFingers(Am, 6);
        AmAlt = Template(1, "Am", AmFrets, new Dictionary<int, int>(Am.Fingers));
        AmAlt.Fingers[4] = AmAlt.Fingers[4] == 4 ? 3 : 4;
        AmArp = Template(2, "Am-arp", AmFrets, new() { [2] = 2, [3] = 3, [4] = 1 });
        AmArp.ForceArpeggioInRs = true;
        C = Template(3, "C", new() { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }, []);
        Open = Template(4, "E5 open", new() { [0] = 0, [1] = 0 }, []);
        G = Template(5, "G", new() { [0] = 3, [1] = 2, [2] = 0, [3] = 0, [4] = 0, [5] = 3 }, new() { [0] = 2, [1] = 1, [5] = 3 });

        Arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        foreach (var t in new[] { Am, AmAlt, AmArp, C, Open, G }) Arrangement.ChordTemplates.Add(t);
        Level0 = new ChartLevel { DifficultyIndex = 0 };
        Level1 = new ChartLevel { DifficultyIndex = 1 };
        Arrangement.Levels.Add(Level0);
        Arrangement.Levels.Add(Level1);

        // Level 0: one Am, one AmAlt, one hand shape on Am.
        Level0.Chords.Add(ChordOn(Am, 0));
        Level0.Chords.Add(ChordOn(AmAlt, 2));
        Level0.HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(1), TemplateId = Am.Id });

        // Level 1 (on screen): three Am, two AmAlt with a hand shape, a C and an arpeggio.
        Level1.Chords.Add(ChordOn(Am, 0));
        Level1.Chords.Add(ChordOn(Am, 4));
        Level1.Chords.Add(ChordOn(Am, 8));
        Level1.Chords.Add(ChordOn(AmAlt, 2));
        Level1.Chords.Add(ChordOn(AmAlt, 6));
        Level1.HandShapes.Add(new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), TemplateId = AmAlt.Id });
        Level1.Chords.Add(ChordOn(C, 10));
        Level1.Chords.Add(ChordOn(AmArp, 12));
        Level1.Chords.Add(ChordOn(Open, 14));
    }

    public Arrangement Arrangement { get; }
    public ChartLevel Level0 { get; }
    public ChartLevel Level1 { get; }
    public ChordTemplate Am { get; }
    public ChordTemplate AmAlt { get; }
    public ChordTemplate AmArp { get; }
    public ChordTemplate C { get; }
    public ChordTemplate Open { get; }
    public ChordTemplate G { get; }

    public static Chord ChordOn(ChordTemplate template, int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = template.Id };
        foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    private static ChordTemplate Template(int id, string name, Dictionary<int, int> frets, Dictionary<int, int> fingers) =>
        new() { Id = id, Name = name, Frets = new Dictionary<int, int>(frets), Fingers = new Dictionary<int, int>(fingers) };
}
