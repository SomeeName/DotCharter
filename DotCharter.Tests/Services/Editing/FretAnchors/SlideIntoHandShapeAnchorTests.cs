using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     A single-note slide that LANDS on a hand shape whose anchor is already decided must be
///     played by the finger that lands. The shape's anchor wins at the landing (P1 over P2), so the walk
///     places an anchor at the slide START that puts the same finger on both ends, instead of leaving the
///     slide under the earlier anchor and changing finger mid-slide (I43 FingerChangeDuringSlide).
/// </summary>
public sealed class SlideIntoHandShapeAnchorTests(ITestOutputHelper output)
{
    private const int GString = 3;

    private static readonly string[] FixtureOwnCodes = ["I26", "D05", "D02", "D44", "L10", "D19"];

    private static (Arrangement Arr, ChartLevel Level) Riff(int repeats)
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);
        var templates = new List<ChordTemplate>
        {
            new() { Id = 0, Frets = new() { [2] = 10, [3] = 12 }, Fingers = new() { [2] = 1, [3] = 3 } }
        };
        for (var r = 0; r < repeats; r++)
        {
            var b = r * 8;
            // 7, 9 put the hand at 7; the slide 10 -> 12 starts on the pinky under that hand ...
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(b), StringIndex = GString, Fret = 7 });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(b + 1), StringIndex = GString, Fret = 9 });
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(b + 2),
                StringIndex = GString,
                Fret = 10,
                SlideTo = 12,
                Sustain = new FractionalPosition(1)
            });
            // ... and lands in a 10/12 shape (index on 10, ring on 12) whose anchor is 10.
            var chord = new Chord { Position = new FractionalPosition(b + 3), TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 10 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = GString, Fret = 12 });
            level.Chords.Add(chord);
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(b + 3),
                EndPosition = new FractionalPosition(b + 5),
                TemplateId = 0
            });
        }

        foreach (var t in templates) arr.ChordTemplates.Add(t);
        foreach (var a in new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates, capo: 0, tuning: arr))
            level.Anchors.Add(a);
        return (arr, level);
    }

    private static IReadOnlyList<ArrangementIssue> Validate(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arr,
            [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })]);

    [Fact]
    public void SlideLandingInADecidedShape_KeepsOneFingerOverTheSlide()
    {
        var (arr, level) = Riff(3);
        var issues = Validate(arr);

        output.WriteLine(string.Join(", ", level.Anchors.Select(a => $"{a.Position}:{a.Fret}w{a.Width}")));
        foreach (var issue in issues) output.WriteLine($"{issue.Code} {issue.Type}");
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingerChangeDuringSlide);
        // No new finding either: only the bare fixture's own (no phrases, no audio, no count-in, unlinked slides,
        // chords without sustain, guessed fingering), the same set the anchors before this rule left besides I43.
        Assert.All(issues, i => Assert.Contains(i.Code, FixtureOwnCodes));
        // The shape keeps its own anchor; the slide starts under 8, so the ring finger plays 10 and lands on 12.
        for (var r = 0; r < 3; r++)
        {
            Assert.Contains(level.Anchors, a => a.Position == new FractionalPosition(r * 8 + 2) && a.Fret == 8);
            Assert.Contains(level.Anchors, a => a.Position == new FractionalPosition(r * 8 + 3) && a.Fret == 10);
        }
    }
}
