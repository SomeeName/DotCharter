using System.Globalization;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     ONE answer to "does this chord show as the frame-only repeat box?" (#487):
///     for every chord, the render snapshot's <see cref="ChordFlags.Repeat" /> —
///     what the 2D timeline and the 3D preview draw — equals what the RS-XML
///     writer SHIPS, i.e. a <c>&lt;chord&gt;</c> with no <c>&lt;chordNote&gt;</c>
///     children (the game's repeat box; see <c>RsChordDensity</c>).
///     <para>
///         The snapshot is built exactly the way both app renderers build it:
///         <see cref="RepeatChordLatch" /> fills
///         <see cref="SnapshotUpdateContext.RepeatChords" /> and
///         <see cref="FrameSnapshot.BuildSnapshot" /> projects it. The writer is
///         the real one, so the comparison is against the FILE, not against a
///         restatement of the rule. Before #487 the 3D preview ran its own port of
///         RsCharter's <c>Level.shouldChordShowNotes</c> and the 2D timeline drew
///         every chord in full.
///     </para>
/// </summary>
public sealed class RepeatChordParityTests
{
    private const string NoCorpus =
        "No RS-XML corpus: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).";

    private static readonly (int StringIndex, int Fret)[][] ShapePool =
    [
        [(0, 3), (1, 5), (2, 5)],
        [(1, 2), (2, 4), (3, 4)],
        [(0, 7), (1, 9), (2, 9), (3, 8)],
        [(2, 10), (3, 12)]
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheSnapshotsRepeatFlag_IsWhatTheExportShips_OnASyntheticChart(int seed)
    {
        var project = SyntheticProject(seed, 240);

        var result = Compare(project);

        Assert.True(result.Mismatches.Count == 0,
            $"{result.Mismatches.Count} chord(s) where the snapshot and the export disagree: "
            + string.Join("; ", result.Mismatches.Take(8)));
        // Non-vacuity: the fixture reaches both answers, and the export really
        // writes the chords being compared.
        Assert.True(result.Repeats >= 20, $"only {result.Repeats} repeat chord(s) — the fixture is too sparse");
        Assert.True(result.Full >= 20, $"only {result.Full} full chord(s)");
    }

    /// <summary>The synthetic fixture exercises every path that makes the
    /// shipped answer differ from the bare density rule: a charter's
    /// <see cref="Chord.ForceNoNotes" />, a chord whose template id resolves to
    /// nothing (the export's synthesized template), and a link landing (written
    /// as loose notes, so no chord and no box).</summary>
    [Fact]
    public void TheSyntheticFixture_ReachesEveryExportSpecialCase()
    {
        var project = SyntheticProject(1, 240);
        var level = project.Arrangements[0].Levels[0];

        Assert.Contains(level.Chords, c => c.ForceNoNotes);
        Assert.Contains(level.Chords, c => c.TemplateId == UnresolvedTemplateId);
        Assert.Contains(level.Chords, c => c.ChordNotes.Any(cn => cn.LinkNext));

        var result = Compare(project);
        Assert.True(result.LinkLandings > 0, "no chord was written as a link landing");
    }

    [Fact]
    public void TheSnapshotsRepeatFlag_IsWhatTheExportShips_OnTheEofCorpus()
    {
        var files = CorpusFiles();
        Assert.SkipWhen(files.Count == 0, NoCorpus);

        var mismatches = new List<string>();
        var repeats = 0;
        var full = 0;
        foreach (var file in files)
        {
            var project = new RsXmlSongReader().Read(file);
            var result = Compare(project);
            repeats += result.Repeats;
            full += result.Full;
            mismatches.AddRange(result.Mismatches.Select(m => $"{Path.GetFileName(Path.GetDirectoryName(file))}: {m}"));
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} chord(s) disagree over {files.Count} file(s): " + string.Join("; ", mismatches.Take(8)));
        Assert.True(repeats > 0 && full > 0, $"corpus reached repeats={repeats}, full={full}");
    }

    // ── the comparison ───────────────────────────────────────────────────────

    private sealed record Result(List<string> Mismatches, int Repeats, int Full, int LinkLandings);

    private static Result Compare(ChartProject project)
    {
        var arrangement = project.Arrangements[0];
        var beats = project.Beats;
        var xml = new RsXmlSongWriter().Write(project, null);
        var mismatches = new List<string>();
        int repeats = 0, full = 0, landings = 0;

        foreach (var levelEl in xml.Root!.Element("levels")!.Elements("level"))
        {
            var difficulty = int.Parse(levelEl.Attribute("difficulty")!.Value, CultureInfo.InvariantCulture);
            var level = arrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == difficulty);
            if (level == null || level.Chords.Count == 0) continue;

            // What the file ships: chord time (whole ms) → "has no chordNote children".
            var shipped = new Dictionary<long, bool>();
            foreach (var chordEl in levelEl.Element("chords")!.Elements("chord"))
            {
                var ms = (long)Math.Round(double.Parse(chordEl.Attribute("time")!.Value,
                    CultureInfo.InvariantCulture) * 1000.0);
                shipped[ms] = !chordEl.Elements("chordNote").Any();
            }

            var snapshot = Snapshot(arrangement, level, beats);
            for (var i = 0; i < snapshot.ChordCount; i++)
            {
                var chord = snapshot.Chords[i];
                var flagged = (chord.Flags & ChordFlags.Repeat) != 0;
                var ms = (long)Math.Round(chord.Time);
                if (!shipped.TryGetValue(ms, out var shipsAsRepeat))
                {
                    // Not a <chord> in the file (a link landing written as loose
                    // notes, or a chord the writer drops): the game draws no box.
                    landings++;
                    if (flagged) mismatches.Add($"level {difficulty} @{ms} ms: flagged, but not shipped as a chord");
                    continue;
                }

                if (shipsAsRepeat) repeats++;
                else full++;
                if (flagged != shipsAsRepeat)
                {
                    mismatches.Add($"level {difficulty} @{ms} ms: snapshot {(flagged ? "repeat" : "full")}, "
                                   + $"export {(shipsAsRepeat ? "repeat" : "full")}");
                }
            }
        }

        return new Result(mismatches, repeats, full, landings);
    }

    /// <summary>The whole level in one window, built the way the app's two
    /// snapshot builders build it — the latch fills the repeat set.</summary>
    private static FrameSnapshot Snapshot(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats)
    {
        var ctx = new SnapshotUpdateContext
        {
            Arrangement = arrangement,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveHandShapes = level.HandShapes,
            ActiveAnchors = level.Anchors,
            ActiveBeats = beats,
            ActiveTemplates = arrangement.ChordTemplates,
            RepeatChords = new RepeatChordLatch().Get(arrangement.ChordTemplates, level.Notes, level.Chords,
                level.HandShapes, beats, contentVersion: 0, nowMs: 0)
        };

        var endMs = beats.Count > 0 ? beats[^1].Time + 60_000 : 600_000;
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(-1_000, endMs, 0, snapshot, ctx, [], 0);
        Assert.Equal(level.Chords.Count, snapshot.ChordCount);
        return snapshot;
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private const int UnresolvedTemplateId = 99;

    /// <summary>
    ///     Chords drawn from a small shape pool (so consecutive chords often
    ///     repeat), single notes sprinkled between them (a note breaks a repeat),
    ///     hand shapes over runs of chords, and the export's special cases:
    ///     forced no-notes, an unresolvable template id, a technique, and links
    ///     that land on the next chord.
    /// </summary>
    private static ChartProject SyntheticProject(int seed, int chordCount)
    {
        var rng = new Random(seed);
        var project = new ChartProject();
        var beatCount = chordCount * 2 + 16;
        for (var i = 0; i < beatCount; i++)
        {
            project.Beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        var arrangement = new Arrangement
        { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        for (var t = 0; t < ShapePool.Length; t++)
        {
            var template = new ChordTemplate { Id = t + 1, Name = $"S{t}" };
            foreach (var (stringIndex, fret) in ShapePool[t]) template.Frets[stringIndex] = fret;
            arrangement.ChordTemplates.Add(template);
        }

        arrangement.Phrases.AddRange(["COUNT", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(beatCount - 4), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };
        var beat = 1;
        Chord? previous = null;
        var shapeStart = beat;
        var linked = 0;
        for (var i = 0; i < chordCount; i++)
        {
            // Mostly repeat the previous shape, so the density rule has work.
            var shapeIndex = previous != null && rng.Next(3) != 0
                ? previous.TemplateId is >= 1 and <= 4 ? previous.TemplateId - 1 : rng.Next(ShapePool.Length)
                : rng.Next(ShapePool.Length);
            var position = new FractionalPosition(beat, new Fraction(rng.Next(0, 2), 2));
            var chord = new Chord
            {
                Position = position,
                TemplateId = rng.Next(10) == 0 ? UnresolvedTemplateId : shapeIndex + 1,
                ForceNoNotes = rng.Next(25) == 0
            };
            foreach (var (stringIndex, fret) in ShapePool[shapeIndex])
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = stringIndex,
                    Fret = fret,
                    Sustain = new FractionalPosition(0, new Fraction(1, 4)),
                    Vibrato = rng.Next(30) == 0
                });
            }

            // Now and then the previous chord links into this one: the export
            // writes this chord as loose notes.
            if (previous != null && rng.Next(20) == 0
                && previous.ChordNotes.FirstOrDefault(cn => chord.ChordNotes.Any(o => o.StringIndex == cn.StringIndex))
                    is { } link)
            {
                link.LinkNext = true;
                link.Sustain = position - previous.Position;
                linked++;
            }

            level.Chords.Add(chord);

            if (rng.Next(4) == 0)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = position + new FractionalPosition(0, new Fraction(3, 4)),
                    StringIndex = rng.Next(6),
                    Fret = rng.Next(1, 12)
                });
            }

            beat += rng.Next(1, 3);
            previous = chord;

            // Close a hand shape every few chords.
            if (rng.Next(4) == 0 || i == chordCount - 1)
            {
                level.HandShapes.Add(new HandShape
                {
                    Position = new FractionalPosition(shapeStart),
                    EndPosition = new FractionalPosition(beat),
                    TemplateId = shapeIndex + 1
                });
                shapeStart = beat;
            }
        }

        Assert.True(linked > 0, "the fixture produced no link landing");
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static List<string> CorpusFiles()
    {
        var root = FindTestDataRoot();
        if (root == null) return [];
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return [];

        return Directory.EnumerateFiles(eof, "*_RS2.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("VOCAL", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }
}
