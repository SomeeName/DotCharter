using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     A Guitar Pro pick scrape imports as a chord split into notes, fully fret-hand
///     muted and ignored (#599). The inspector used to flag it twice over, both
///     wrongly: D11 "inside no hand shape, so it exports with no grip" — a split chord
///     exports as single notes with no chord frame, and D11's auto-fix (a hand shape)
///     would give it a chord box in the game — and L07 "no finger on the fretted
///     string" — a fully fret-hand-muted chord is no grip (EOF calls its fingering
///     valid, <c>rs.c:3628-3631</c>). Ordinary chords keep both findings.
/// </summary>
public sealed class SplitChordFindingsTests
{
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];

    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<ArrangementIssue> Run(Arrangement arrangement, IArrangementRule rule)
    {
        var issues = new List<ArrangementIssue>();
        rule.Validate(ArrangementValidationContext.Build(arrangement, Beats(), null), issues);
        return issues;
    }

    /// <summary>An unfingered G5 template the chord plays, with no hand shape over it.</summary>
    private static (Arrangement Arrangement, Chord Chord) UnfingeredChord(Action<Chord>? shape = null)
    {
        var arrangement = ChordHandShapePlannerTests.Arr();
        var chord = ChordHandShapePlannerTests.Chord(1, 0, G5);
        shape?.Invoke(chord);
        var template = new ChordTemplate { Id = 0, Name = "G5" };
        foreach (var cn in chord.ChordNotes) template.Frets[cn.StringIndex] = cn.Fret;
        arrangement.ChordTemplates.Add(template);
        chord.TemplateId = 0;
        arrangement.Levels[0].Chords.Add(chord);
        return (arrangement, chord);
    }

    [Fact]
    public void ImportedScrapes_RaiseNoL07AndNoD11()
    {
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu()));
        var scrapes = arrangement.Levels[0].Chords.Where(GpPickScrape.IsScrapeChord).ToList();
        Assert.Equal(4, scrapes.Count);

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ChordOutsideHandShape);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString);
        var onScrapes = issues.Where(i => i.Chord != null && scrapes.Contains(i.Chord)).ToList();
        Assert.True(onScrapes.Count == 0,
            "findings on a scrape: " + string.Join("; ", onScrapes.Select(i => $"{i.Type.GetCode()} {i.Message}")));
    }

    [Fact]
    public void AnOrdinaryChord_StillRaisesL07AndD11()
    {
        var (arrangement, chord) = UnfingeredChord();

        Assert.Equal(3, Run(arrangement, new TemplateAndOverlapRule())
            .Count(i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString && i.Chord == chord));
        Assert.Same(chord, Assert.Single(Run(arrangement, new ChordOutsideHandShapeRule())).Chord);
    }

    /// <summary>A chord split into notes ships with no chord frame: no hand shape is
    /// missing, whatever it plays.</summary>
    [Fact]
    public void ASplitChord_RaisesNoD11()
    {
        var (arrangement, _) = UnfingeredChord(c => c.SplitIntoNotes = true);

        Assert.Empty(Run(arrangement, new ChordOutsideHandShapeRule()));
    }

    /// <summary>EOF's rule: a chord whose strings are all fret-hand muted needs no
    /// fingering. One muted string of three is still a grip.</summary>
    [Fact]
    public void AFullyMutedChord_RaisesNoL07_APartlyMutedOneStillDoes()
    {
        var (full, _) = UnfingeredChord(c =>
        {
            foreach (var cn in c.ChordNotes) cn.Mute = MuteType.Full;
        });
        var (partly, _) = UnfingeredChord(c => c.ChordNotes[0].Mute = MuteType.Full);

        Assert.DoesNotContain(Run(full, new TemplateAndOverlapRule()),
            i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString);
        Assert.Equal(3, Run(partly, new TemplateAndOverlapRule())
            .Count(i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString));
    }

    /// <summary>A template a muted chord AND a hand shape both play is still judged,
    /// at the shape.</summary>
    [Fact]
    public void AFullyMutedChordsTemplate_IsStillJudgedWhereAHandShapePlaysIt()
    {
        var (arrangement, _) = UnfingeredChord(c =>
        {
            foreach (var cn in c.ChordNotes) cn.Mute = MuteType.Full;
        });
        arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = ChordHandShapePlannerTests.Pos(4), EndPosition = ChordHandShapePlannerTests.Pos(5),
            TemplateId = 0
        });

        Assert.Equal(3, Run(arrangement, new TemplateAndOverlapRule())
            .Count(i => i.Type == ArrangementIssueType.FingerMissingOnFrettedString && i.HandShape != null));
    }

    /// <summary>
    ///     Tools ▸ Hand shapes ▸ Generate hand shapes — and so D11's auto-fix, which
    ///     runs the same plan — never wraps a split chord, and a split chord between two
    ///     strums of one grip breaks their run instead of being swallowed by it.
    /// </summary>
    [Fact]
    public void GenerateHandShapes_NeverWrapsASplitChord()
    {
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu()));
        Assert.True(ArrangementHandShapeGenerator.Plan(arrangement).IsEmpty);

        var strums = ChordHandShapePlannerTests.Arr();
        var level = strums.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 1, G5));
        var split = ChordHandShapePlannerTests.Chord(1, 1, G5);
        split.SplitIntoNotes = true;
        level.Chords.Add(split);
        level.Chords.Add(ChordHandShapePlannerTests.Chord(2, 1, G5));

        var bars = ArrangementHandShapeGenerator.Plan(strums).Levels.SelectMany(l => l.HandShapes).ToList();

        Assert.Equal(2, bars.Count);
        Assert.DoesNotContain(bars, b => b.Chords.Contains(split));
        Assert.All(bars, b => Assert.True(
            b.EndPosition.CompareTo(split.Position) <= 0 || b.Position.CompareTo(split.Position) > 0,
            $"a bar {b.Position}–{b.EndPosition} spans the split chord"));
    }
}
