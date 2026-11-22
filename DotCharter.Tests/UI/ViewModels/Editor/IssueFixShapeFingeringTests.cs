using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A chord fingered 1/1 on frets 6/6 under FHP 5, inside a hand shape that plays the same frets
///     fingered 2/2 (what the old Note-tab save left behind). E04 is right about the chord, but moving the
///     anchor to fret 6 is the wrong fix: the hand shape already states the fingering that fits the anchor.
///     The primary fix re-points the chord to the hand shape's template (no template is edited), the anchor
///     move stays as the alternative, and the finding's text says the hand shape is fingered differently.
/// </summary>
public sealed class IssueFixShapeFingeringTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>Template 0 = 6/6 fingered 1/1 (the chord's), template 1 = 6/6 fingered 2/2 (the shape's).</summary>
    private static (Arrangement Arr, ChartLevel Level, Chord Chord, HandShape Shape, FretAnchor Anchor) Build()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        foreach (var (id, finger) in new[] { (0, 1), (1, 2) })
        {
            arr.ChordTemplates.Add(new ChordTemplate
            {
                Id = id,
                Frets = new Dictionary<int, int> { [0] = 6, [1] = 6 },
                Fingers = new Dictionary<int, int> { [0] = finger, [1] = finger }
            });
        }

        var anchor = new FretAnchor(new FractionalPosition(0), 5, 4);
        level.Anchors.Add(anchor);
        var shape = new HandShape { TemplateId = 1, Position = new FractionalPosition(1), EndPosition = new FractionalPosition(3) };
        level.HandShapes.Add(shape);
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 6 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 6 });
        level.Chords.Add(chord);
        return (arr, level, chord, shape, anchor);
    }

    private static List<ArrangementIssue> E04s(Arrangement arr) =>
        [.. new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret)];

    [Fact]
    public void TheFinding_SaysTheHandShapeIsFingeredDifferently()
    {
        var (arr, _, chord, _, _) = Build();

        var finding = Assert.Single(E04s(arr));

        Assert.Same(chord, finding.Chord);
        Assert.Equal("Validation_IndexFingerNotOnAnchorFret_ShapeFingering", finding.MessageKey);
    }

    [Fact]
    public void TheDefaultFix_UsesTheHandShapesFingering_InOneUndoStep()
    {
        var (arr, level, chord, shape, anchor) = Build();
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());
        var loc = FakeLocalization.English();

        vm.Offer(Assert.Single(E04s(arr)), arr, level);

        Assert.Equal(loc.Format("Inspector_IssueFix_UseShapeFingering", "2-2"), vm.DefaultLabel);
        Assert.True(vm.HasAlternative);
        Assert.Equal(loc.Format("Inspector_IssueFix_MoveAnchor", 6), vm.AlternativeLabel);

        vm.ApplyDefaultCommand.Execute(null);

        Assert.Equal(1, chord.TemplateId);
        Assert.Equal(1, shape.TemplateId);
        Assert.Equal(5, anchor.Fret);
        Assert.Equal(1, arr.ChordTemplates[0].Fingers[0]);
        Assert.Equal(2, arr.ChordTemplates[1].Fingers[0]);
        Assert.Empty(E04s(arr));

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(5, anchor.Fret);
        Assert.Single(E04s(arr));
    }

    [Fact]
    public void FixAll_MakesTheSameChoice()
    {
        var (arr, level, chord, _, anchor) = Build();

        var fix = IssueFixViewModel.DefaultFix(Assert.Single(E04s(arr)), arr, level, Beats());
        Assert.NotNull(fix);
        fix.Execute();

        Assert.Equal(1, chord.TemplateId);
        Assert.Equal(5, anchor.Fret);
        Assert.Empty(E04s(arr));
    }
}
