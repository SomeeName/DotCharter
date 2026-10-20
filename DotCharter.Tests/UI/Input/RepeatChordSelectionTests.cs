using System.Text.RegularExpressions;
using Avalonia;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A repeat chord (#487, #564) is a chord like any other to the editor: a click on its head selects it,
///     a marquee over it collects it, and nothing that selects, navigates or edits asks whether it is a
///     repeat. The repeat decision is a LOOK — <c>ChordFlags.Repeat</c>, read by the two renderers — so
///     these build a level where <see cref="RepeatChords" /> really answers "repeat" for the second strum
///     and drive the same pure hit testers the timeline's pointer handlers call. That the selected repeat
///     DRAWS its selection is <c>Rendering/NoteLayerRepeatChordTests.ASelectedRepeatChord_ShowsTheFullSelection</c>.
/// </summary>
public sealed partial class RepeatChordSelectionTests
{
    private const double ViewAnchorMs = 450.0; // pixelsPerMs 1 + PlayheadX 450 => viewStartMs 0
    private const double PixelsPerMs = 1.0;
    private const double Width = 1600.0;
    private const double Height = 400.0;
    private const double TopMargin = 40.0;
    private const double BottomMargin = 40.0;
    private const int StringCount = 6;

    private static readonly List<BeatMarker> Beats =
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static double LaneY(int stringIndex) =>
        ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, stringIndex, leftyFlip: false);

    /// <summary>Shape A at beat 1 (500 ms) and again at beat 2 (1000 ms): the second is a repeat.</summary>
    private static (List<Chord> Chords, Chord Repeat) Level()
    {
        var template = new ChordTemplate { Id = 1, Name = "A" };
        template.Frets[0] = 3;
        template.Frets[1] = 5;
        template.Frets[2] = 5;
        var chords = new List<Chord> { Strum(1), Strum(2) };
        var repeats = RepeatChords.Compute([template], [], chords, [], Beats);
        Assert.True(repeats.Count == 1 && repeats.Contains(chords[1]),
            "fixture: the export rule must call the second strum a repeat, or these cases prove nothing");
        return (chords, chords[1]);

        static Chord Strum(int beat)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 1 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
            return chord;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AClickOnARepeatChordsHead_SelectsIt(int stringIndex)
    {
        var (chords, repeat) = Level();

        var hit = ChartHitTester.HitTestChord(new Point(1000, LaneY(stringIndex)), chords, ViewAnchorMs, PixelsPerMs,
            Width, Height, TopMargin, BottomMargin, StringCount, Beats, leftyFlip: false, out var exact);

        Assert.Same(repeat, hit);
        Assert.True(exact, "a click dead on the repeat's head is an exact hit, not a near miss");
    }

    [Fact]
    public void AMarqueeOverARepeatChord_CollectsIt()
    {
        var (chords, repeat) = Level();
        var results = new List<Chord>();

        var top = Math.Min(LaneY(0), LaneY(2)) - 20;
        var bottom = Math.Max(LaneY(0), LaneY(2)) + 20;
        MarqueeHitTester.CollectChords(new Rect(980, top, 40, bottom - top), chords,
            ViewAnchorMs, PixelsPerMs, Width, Height, TopMargin, BottomMargin, StringCount, Beats, results,
            leftyFlip: false);

        Assert.Equal([repeat], results);
    }

    [Fact]
    public void AMarqueeOverTheWholeRun_CollectsTheOriginalAndTheRepeat()
    {
        var (chords, _) = Level();
        var results = new List<Chord>();

        MarqueeHitTester.CollectChords(new Rect(400, TopMargin, 700, Height - TopMargin - BottomMargin), chords,
            ViewAnchorMs, PixelsPerMs, Width, Height, TopMargin, BottomMargin, StringCount, Beats, results,
            leftyFlip: false);

        Assert.Equal(chords, results);
    }

    /// <summary>
    ///     Structural: input, selection and editing never ask whether a chord is a repeat, so keyboard
    ///     navigation, select-uses and every verb treat a repeat like any chord. The repeat set reaches the
    ///     app through the two renderers (<c>ChartRenderer</c>, <c>Preview3DRenderDriver</c>) and the
    ///     inspector's read-only line, and nowhere else.
    /// </summary>
    [Fact]
    public void NothingThatSelectsOrEdits_ReadsTheRepeatDecision()
    {
        var root = TestPaths.RepoRoot();
        var scanned = new[]
            {
                Path.Combine(root, "DotCharter.UI", "Input"),
                Path.Combine(root, "DotCharter.Services", "Editing"),
                Path.Combine(root, "DotCharter.Services", "State")
            }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .ToList();
        Assert.True(scanned.Count > 50, $"only {scanned.Count} file(s) scanned — the lint is pointed at the wrong place");

        var offenders = scanned
            .Where(f => RepeatDecision().IsMatch(LineComment().Replace(File.ReadAllText(f), string.Empty)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "reads the repeat decision: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("if ((chord.Flags & ChordFlags.Repeat) != 0) return;")]
    [InlineData("var set = RepeatChords.Compute(t, n, c, h, b);")]
    [InlineData("var v = RepeatChords.Explain(t, n, c, h, b);")]
    [InlineData("_latch = new RepeatChordLatch();")]
    public void Probe_ARepeatReadIsCaught(string code) => Assert.Matches(RepeatDecision(), code);

    [GeneratedRegex(@"ChordFlags\.Repeat\b|\bRepeatChords\.|\bRepeatChordLatch\b")]
    private static partial Regex RepeatDecision();

    /// <summary>A <c>//</c> or <c>///</c> comment to the end of its line — a doc comment may name the latch.</summary>
    [GeneratedRegex("//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [Fact]
    public void Probe_ACommentNamingTheLatch_IsNotARead() =>
        Assert.DoesNotMatch(RepeatDecision(),
            LineComment().Replace("/// <c>RepeatChordLatch</c> is the same contract.", string.Empty));
}
