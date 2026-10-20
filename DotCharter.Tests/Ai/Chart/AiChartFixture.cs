using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     A small hand-built chart for the AI chart pass tests: 8 bars of 4 beats (one beat =
///     500 ms, position = beat index), standard tuning, two phrases ("intro" at beat 0,
///     "verse" at beat 16), power chords on a SHARED template, an open chord, single notes,
///     anchors and a hand shape.
/// </summary>
internal sealed class AiChartFixture
{
    public const int PowerC = 0;   // x 3 5 5 x x  fingers x 1 3 4 x x
    public const int PowerG = 1;   // 3 5 5 x x x  fingers 1 3 4 x x x
    public const int OpenEm = 2;   // 0 2 2 0 0 0  fingers x 2 3 x x x

    public AiChartFixture(int capo = 0)
    {
        for (var beat = 0; beat <= 32; beat++)
            Beats.Add(new BeatMarker { Time = beat * 500.0, IsMeasure = beat % 4 == 0, BeatsInMeasure = 4 });
        foreach (var b in Beats) Project.Beats.Add(b);

        Arrangement.Capo = capo;
        Arrangement.ChordTemplates.Add(Template(PowerC, "C5", new() { [1] = 3, [2] = 5, [3] = 5 }, new() { [1] = 1, [2] = 3, [3] = 4 }));
        Arrangement.ChordTemplates.Add(Template(PowerG, "G5", new() { [0] = 3, [1] = 5, [2] = 5 }, new() { [0] = 1, [1] = 3, [2] = 4 }));
        Arrangement.ChordTemplates.Add(Template(OpenEm, "Emin",
            new() { [0] = 0, [1] = 2, [2] = 2, [3] = 0, [4] = 0, [5] = 0 }, new() { [1] = 2, [2] = 3 }));
        Arrangement.EventPoints.Add(new EventPoint { Position = At(0), Phrase = "intro" });
        Arrangement.EventPoints.Add(new EventPoint { Position = At(16), Phrase = "verse" });

        // intro: notes on beats 0-7, power chords on 8 10 12 (C5) and 14 (G5)
        for (var beat = 0; beat < 8; beat++) Level.Notes.Add(new GuitarNote { Position = At(beat), StringIndex = 2, Fret = beat % 2 == 0 ? 5 : 7 });
        foreach (var beat in new[] { 8, 10, 12 }) Level.Chords.Add(ChordOn(beat, PowerC));
        Level.Chords.Add(ChordOn(14, PowerG));
        // verse: C5 on 16 18, notes 20-23, Em on 24, C5 on 26
        foreach (var beat in new[] { 16, 18 }) Level.Chords.Add(ChordOn(beat, PowerC));
        for (var beat = 20; beat < 24; beat++) Level.Notes.Add(new GuitarNote { Position = At(beat), StringIndex = 3, Fret = 5 + beat - 20 });
        Level.Chords.Add(ChordOn(24, OpenEm));
        Level.Chords.Add(ChordOn(26, PowerC));

        Level.Anchors.Add(new FretAnchor(At(0), 5, 4));
        Level.Anchors.Add(new FretAnchor(At(8), 3, 4));
        Level.Anchors.Add(new FretAnchor(At(16), 3, 4));
        Level.Anchors.Add(new FretAnchor(At(24), 1, 4));
        Level.HandShapes.Add(new HandShape { Position = At(8), EndPosition = At(13), TemplateId = PowerC });

        Arrangement.Levels.Add(Level);
        Project.Arrangements.Add(Arrangement);
    }

    public ChartProject Project { get; } = new();
    public Arrangement Arrangement { get; } = new() { Name = "Lead", Tuning = ["E", "A", "D", "G", "B", "e"] };
    public ChartLevel Level { get; } = new();
    public List<BeatMarker> Beats { get; } = [];
    public AiChartSongFacts Facts => new(Arrangement, false, null);

    public static FractionalPosition At(int beat) => new(beat);

    public ChordTemplate TemplateById(int id) => Arrangement.ChordTemplates.First(t => t.Id == id);

    public IReadOnlyList<AiChartChunk> Chunks(AiChartChunkOptions? options = null, FractionalPosition? from = null,
        FractionalPosition? to = null)
        => AiChartChunker.Plan(Arrangement, Level, Beats, from, to, options ?? PerPhrase);

    /// <summary>One chunk per phrase in this fixture.</summary>
    public static AiChartChunkOptions PerPhrase { get; } = new(MinSounds: 1, MaxSounds: 64, ContextBefore: 3, ContextAfter: 3);

    /// <summary>The reply that restates the chunk exactly as it is now.</summary>
    public ReplyDoc EchoReply(AiChartChunk chunk, AiChartJob job = AiChartJob.FhpAndFingering)
    {
        var templates = Arrangement.ChordTemplates.ToDictionary(t => t.Id);
        var doc = new ReplyDoc();
        foreach (var a in Level.Anchors.Where(a => chunk.Contains(a.Position)).OrderBy(a => a.Position))
        {
            var at = a.Position == chunk.Start ? AiChartPrompt.StartToken : chunk.Core.First(s => s.Position == a.Position).Id;
            doc.Anchors.Add(new ReplyAnchor { At = at, Fret = a.Fret, Width = a.Width });
        }

        if (doc.Anchors.Count == 0 || doc.Anchors[0].At != AiChartPrompt.StartToken)
        {
            var inForce = Level.Anchors.Where(a => a.Position <= chunk.Start).OrderBy(a => a.Position).Last();
            doc.Anchors.Insert(0, new ReplyAnchor { At = AiChartPrompt.StartToken, Fret = inForce.Fret, Width = inForce.Width });
        }

        foreach (var s in chunk.Core.Where(s => AiChartPrompt.IsFixable(s, templates)))
        {
            var t = templates[s.Chord!.TemplateId];
            doc.Chords.Add(new ReplyChord
            {
                Id = s.Id, Ms = s.Ms, Frets = AiChartPrompt.FretArray(t, 6), Fingers = AiChartPrompt.FingerArray(t, 6),
                Name = t.Name
            });
        }

        doc.Job = job;
        return doc;
    }

    private static ChordTemplate Template(int id, string name, Dictionary<int, int> frets, Dictionary<int, int> fingers)
        => new() { Id = id, Name = name, Frets = frets, Fingers = fingers };

    private Chord ChordOn(int beat, int templateId)
    {
        var chord = new Chord { Position = At(beat), TemplateId = templateId };
        foreach (var (s, f) in Arrangement.ChordTemplates.First(t => t.Id == templateId).Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    internal sealed class ReplyDoc
    {
        public AiChartJob Job { get; set; }
        public List<ReplyAnchor> Anchors { get; } = [];
        public List<ReplyChord> Chords { get; } = [];

        public ReplyChord Chord(string id) => Chords.Single(c => c.Id == id);

        public string Json()
        {
            var anchors = Anchors.Select(a => new { at = a.At, fret = a.Fret, width = a.Width });
            var fingered = Chords.Select(c => new { id = c.Id, ms = c.Ms, frets = c.Frets, fingers = c.Fingers });
            object body = Job switch
            {
                AiChartJob.FhpAndFingering => new { anchors, chords = fingered },
                AiChartJob.FhpOnly => new { anchors },
                AiChartJob.FingeringOnly => new { chords = fingered },
                _ => new { chords = Chords.Select(c => new { id = c.Id, ms = c.Ms, frets = c.Frets, name = c.Name }) }
            };
            return JsonSerializer.Serialize(body);
        }

        public AiChartReply Decode()
        {
            Assert.True(AiChartReplyReader.TryRead(Json(), Job, out var reply, out var why), why);
            return reply!;
        }
    }

    internal sealed class ReplyAnchor
    {
        public string At { get; set; } = "";
        public int Fret { get; set; }
        public int Width { get; set; }
    }

    internal sealed class ReplyChord
    {
        public string Id { get; set; } = "";
        public int Ms { get; set; }
        public int[] Frets { get; set; } = [];
        public int[] Fingers { get; set; } = [];
        public string Name { get; set; } = "";
    }
}

/// <summary>A model that answers each prompt through a test-supplied function; never a network.</summary>
internal sealed class FakeChartModel(Func<ToneDesignPrompt, int, string> answer) : IToneDesignModel
{
    private int _calls;

    public List<ToneDesignPrompt> Prompts { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public AiProviderKind Kind => AiProviderKind.ClaudeCli;

    public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int call;
        lock (Prompts)
        {
            Prompts.Add(prompt);
            call = _calls++;
        }

        return Task.FromResult(new ToneDesignModelReply(answer(prompt, call), new AiUsage("fake-model", 100, 50)));
    }

    public Task<AiProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new AiProbeResult(true, "ok", "fake-model"));
}
