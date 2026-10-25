namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     A 7-string arrangement shaped like an old imported chart: one grip stored as three unnamed copies
///     fingered alike plus a fourth with another finger on one string, a named "A5" with an unnamed copy,
///     a unique "E5", and three shapes nothing uses. Ids are positions and the copies sit BETWEEN the
///     others, so every removal renumbers. Hand shapes and a notation-voice chord use copies too.
/// </summary>
internal sealed class MessyShapesFixture
{
    public MessyShapesFixture()
    {
        Grip0 = Template(0, "", new() { [2] = 9, [3] = 8, [4] = 11 }, new() { [2] = 2, [3] = 1, [4] = 4 });
        E5 = Template(1, "E5", new() { [0] = 0, [1] = 2, [2] = 2 }, new() { [1] = 1, [2] = 2 });
        Grip1 = Template(2, "", new() { [2] = 9, [3] = 8, [4] = 11 }, new() { [2] = 2, [3] = 1, [4] = 4 });
        A5Copy = Template(3, "", new() { [1] = 0, [2] = 2, [3] = 2 }, new() { [2] = 1, [3] = 2 });
        Orphan0 = Template(4, "", new() { [3] = 5, [4] = 7 }, new() { [3] = 1, [4] = 3 });
        Grip2 = Template(5, "", new() { [2] = 9, [3] = 8, [4] = 11 }, new() { [2] = 2, [3] = 1, [4] = 4 });
        A5 = Template(6, "A5", new() { [1] = 0, [2] = 2, [3] = 2 }, new() { [2] = 1, [3] = 2 });
        GripAlt = Template(7, "", new() { [2] = 9, [3] = 8, [4] = 11 }, new() { [2] = 2, [3] = 1, [4] = 3 });
        Orphan1 = Template(8, "Dm", new() { [2] = 0, [3] = 2, [4] = 3 }, new() { [3] = 1, [4] = 2 });
        Orphan2 = Template(9, "", new() { [0] = 1 }, new() { [0] = 1 });

        Arrangement = new Arrangement { Name = "Lead", StringCount = 7 };
        foreach (var t in new[] { Grip0, E5, Grip1, A5Copy, Orphan0, Grip2, A5, GripAlt, Orphan1, Orphan2 })
            Arrangement.ChordTemplates.Add(t);
        Level0 = new ChartLevel { DifficultyIndex = 0 };
        Level1 = new ChartLevel { DifficultyIndex = 1 };
        Arrangement.Levels.Add(Level0);
        Arrangement.Levels.Add(Level1);

        Use(Level0, Grip0, 0, handShape: true);
        Use(Level0, Grip0, 4);
        Use(Level0, Grip1, 8, handShape: true);
        Use(Level0, GripAlt, 12, handShape: true);
        Use(Level0, A5, 16);
        Use(Level1, Grip2, 0, handShape: true);
        Use(Level1, GripAlt, 4);
        Use(Level1, A5, 8);
        Use(Level1, A5Copy, 12, handShape: true);
        Use(Level1, E5, 16);
        var voice = new NotationVoice();
        voice.Chords.Add(ChordShapesFixture.ChordOn(Grip2, 20));
        Arrangement.NotationVoices.Add(voice);
    }

    public Arrangement Arrangement { get; }
    public ChartLevel Level0 { get; }
    public ChartLevel Level1 { get; }
    public ChordTemplate Grip0 { get; }
    public ChordTemplate Grip1 { get; }
    public ChordTemplate Grip2 { get; }
    public ChordTemplate GripAlt { get; }
    public ChordTemplate A5 { get; }
    public ChordTemplate A5Copy { get; }
    public ChordTemplate E5 { get; }
    public ChordTemplate Orphan0 { get; }
    public ChordTemplate Orphan1 { get; }
    public ChordTemplate Orphan2 { get; }

    public IEnumerable<Chord> AllChords =>
        Arrangement.Levels.SelectMany(l => l.Chords).Concat(Arrangement.NotationVoices.SelectMany(v => v.Chords));

    public IEnumerable<HandShape> AllHandShapes => Arrangement.Levels.SelectMany(l => l.HandShapes);

    /// <summary>Every use's template id, chords then hand shapes: what an undo must restore exactly.</summary>
    public List<int> UseIds() => [.. AllChords.Select(c => c.TemplateId), .. AllHandShapes.Select(h => h.TemplateId)];

    private static void Use(ChartLevel level, ChordTemplate template, int beat, bool handShape = false)
    {
        level.Chords.Add(ChordShapesFixture.ChordOn(template, beat));
        if (handShape)
        {
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 2), TemplateId = template.Id
            });
        }
    }

    private static ChordTemplate Template(int id, string name, Dictionary<int, int> frets, Dictionary<int, int> fingers) =>
        new() { Id = id, Name = name, Frets = frets, Fingers = fingers };
}
