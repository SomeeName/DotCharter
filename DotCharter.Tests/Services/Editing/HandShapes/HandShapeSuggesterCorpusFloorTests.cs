using System.Text.Json;
using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The suggester's precision and recall against the owner's finished charts, in the DEFAULT gate
///     (#491). The corpus is owner-local, so <c>Fixtures/hand-shape-suggestion-floor.json</c> holds 24
///     windows of 64 sound positions from TRAIN-split guitar charts, written by
///     <c>tools/chart-eval/HandShapeEval.cs --fixture</c>: frets, fret-hand mutes, slides, sustains,
///     the charter's anchors and hand-shape bars, at positions relative to the window — nothing that
///     names a song. Each window is scored the way the tool scores a chart: the bars are the answer
///     key, the anchors and the chords' templates stay, and every single note counts once.
///     <para>
///         The floors are what the fixture measured when it was written, rounded down a point; a
///         change that LOSES precision or recall fails here. A change that gains raises them in the
///         same commit (the test prints the counts); a trade is measured on the whole corpus first
///         (the held-out number is in <c>tools/chart-eval/README.md</c>).
///     </para>
/// </summary>
public sealed class HandShapeSuggesterCorpusFloorTests(ITestOutputHelper output)
{
    // Measured 2026-10-09 on the fixture of the same commit: precision 95.6 %, recall 53.5 %
    // (the windows open on a charter's bar over single notes, so they hold more grips than a chart).
    private const double PrecisionFloor = 94.5;
    private const double RecallFloor = 52.5;

    private static readonly Lazy<(int Tp, int Fp, int Fn, int Windows)> Measured = new(Measure);

    internal static string FixturePath { get; } = Path.Combine(AppContext.BaseDirectory, "Services", "Editing",
        "HandShapes", "Fixtures", "hand-shape-suggestion-floor.json");

    [Fact]
    public void Precision_HoldsItsFloor()
    {
        var (tp, fp, fn, windows) = Measured.Value;
        var precision = 100.0 * tp / Math.Max(1, tp + fp);
        output.WriteLine($"{windows} windows: tp {tp} fp {fp} fn {fn} — precision {precision:0.0}%");
        Assert.True(precision >= PrecisionFloor, $"precision {precision:0.0}% fell below {PrecisionFloor}%");
    }

    [Fact]
    public void Recall_HoldsItsFloor()
    {
        var (tp, fp, fn, windows) = Measured.Value;
        var recall = 100.0 * tp / Math.Max(1, tp + fn);
        output.WriteLine($"{windows} windows: tp {tp} fp {fp} fn {fn} — recall {recall:0.0}%");
        Assert.True(recall >= RecallFloor, $"recall {recall:0.0}% fell below {RecallFloor}%");
    }

    [Fact]
    public void TheFixtureHoldsWhatItIsScoredOn()
    {
        var (tp, _, fn, windows) = Measured.Value;
        Assert.Equal(24, windows);
        Assert.True(tp + fn > 200, $"only {tp + fn} charted single notes under a bar; the fixture is thin");
    }

    private static (int Tp, int Fp, int Fn, int Windows) Measure()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        int tp = 0, fp = 0, fn = 0, windows = 0;
        foreach (var window in doc.RootElement.GetProperty("windows").EnumerateArray())
        {
            windows++;
            var (arrangement, level, reference) = Load(window);
            var proposals = HandShapeSuggester.Suggest(arrangement, level);
            foreach (var note in level.Notes)
            {
                var charted = reference.Exists(h => h.Covers(note.Position));
                var proposed = proposals.Exists(p => p.Covers(note.Position));
                if (charted && proposed) tp++;
                else if (proposed) fp++;
                else if (charted) fn++;
            }
        }

        return (tp, fp, fn, windows);
    }

    internal static (Arrangement Arrangement, ChartLevel Level, List<HandShape> Reference) Load(JsonElement window)
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = window.GetProperty("strings").GetInt32(),
            Capo = window.GetProperty("capo").GetInt32()
        };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        foreach (var entry in window.GetProperty("notes").EnumerateArray())
        {
            // "<position>|<string>:<fret>[x][s]|<sustain>"
            var parts = entry.GetString()!.Split('|');
            var (stringIndex, fret, muted, slides) = Sound(parts[1]);
            level.Notes.Add(new GuitarNote
            {
                Position = FractionalPosition.Parse(parts[0]), StringIndex = stringIndex, Fret = fret,
                Sustain = FractionalPosition.Parse(parts[2]), Mute = muted ? MuteType.Full : MuteType.None,
                SlideTo = slides ? fret + 2 : null
            });
        }

        // The chart's chord templates are part of what the suggester meets: one per chord shape.
        foreach (var entry in window.GetProperty("chords").EnumerateArray())
        {
            // "<position>|<string>:<fret>[x][s]/<sustain>,…"
            var parts = entry.GetString()!.Split('|');
            var chord = new Chord { Position = FractionalPosition.Parse(parts[0]) };
            foreach (var noteText in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var halves = noteText.Split('/', 2);
                var (stringIndex, fret, muted, slides) = Sound(halves[0]);
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = stringIndex, Fret = fret, Sustain = FractionalPosition.Parse(halves[1]),
                    Mute = muted ? MuteType.Full : MuteType.None, SlideTo = slides ? fret + 2 : null
                });
            }

            var frets = chord.ChordNotes.Where(n => n.Mute != MuteType.Full)
                .GroupBy(n => n.StringIndex).ToDictionary(g => g.Key, g => g.First().Fret);
            var template = arrangement.ChordTemplates.FirstOrDefault(t => t.HasFrets(frets));
            if (template == null && frets.Count > 0)
            {
                template = new ChordTemplate { Id = arrangement.ChordTemplates.Count, Frets = frets };
                arrangement.ChordTemplates.Add(template);
            }

            chord.TemplateId = template?.Id ?? -1;
            level.Chords.Add(chord);
        }

        foreach (var entry in window.GetProperty("anchors").EnumerateArray())
            level.Anchors.Add(new FretAnchor { Position = FractionalPosition.Parse(entry.GetString()!), Fret = 1 });

        var reference = window.GetProperty("bars").EnumerateArray().Select(entry =>
        {
            var span = entry.GetString()!.Split('|');
            return new HandShape { Position = FractionalPosition.Parse(span[0]), EndPosition = FractionalPosition.Parse(span[1]) };
        }).ToList();

        return (arrangement, level, reference);
    }

    private static (int String, int Fret, bool Muted, bool Slides) Sound(string text)
    {
        var colon = text.IndexOf(':');
        var tail = text[(colon + 1)..];
        var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
        return (int.Parse(text[..colon]), int.Parse(digits), tail.Contains('x'), tail.Contains('s'));
    }
}
