using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     "Pin all grips" (#1019): every unpinned fingered chord shape of the arrangement pinned in ONE undo step,
///     already-pinned, ghost and fingerless shapes untouched. A guessed grip is no finding, so no Fix offers it.
/// </summary>
public sealed class GripPinsTests
{
    private static readonly Dictionary<int, int> CMajor = new() { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 };

    private static readonly Dictionary<int, int> GMajor = new() { [0] = 3, [1] = 2, [2] = 0, [3] = 0, [4] = 0, [5] = 3 };

    private static readonly Dictionary<int, int> AMajor = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 2, [5] = 0 };

    private readonly ChordTemplate _c = Guessed(0, "C", CMajor);
    private readonly ChordTemplate _g = Guessed(1, "G", GMajor);
    private readonly ChordTemplate _a = Guessed(2, "A", AMajor);
    private readonly ChordTemplate _pinned = Guessed(3, "D", new() { [2] = 0, [3] = 2, [4] = 3, [5] = 2 });
    private readonly ChordTemplate _fingerless = new() { Id = 4, Name = "E5", Frets = new() { [0] = 0, [1] = 2 }, Fingers = new() { [1] = 1 }, Fingerless = true };
    private readonly ChordTemplate _bare = new() { Id = 5, Name = "Em", Frets = new() { [1] = 2, [2] = 2 } };
    private readonly ChordTemplate _ghost = Guessed(6, "F", new() { [0] = 1, [1] = 3, [2] = 3 });
    private readonly Arrangement _arrangement;

    public GripPinsTests()
    {
        _pinned.Pinned = true;
        _ghost.GhostMask = 1;
        _arrangement = Arrangement(_c, _g, _a, _pinned, _fingerless, _bare, _ghost);
    }

    [Fact]
    public void PinAll_PinsEveryUnpinnedFingeredShape_InOneUndoStep_AndLeavesTheRestAlone()
    {
        var history = new HistoryService(new FakeAppLogger());
        Assert.Equal([_c, _g, _a], GripPins.Unpinned(_arrangement));
        Assert.True(history.Do(GripPins.PinAll(_arrangement)!));

        Assert.All(new[] { _c, _g, _a, _pinned }, t => Assert.True(t.Pinned));
        Assert.False(_fingerless.Pinned);
        Assert.False(_bare.Pinned);
        Assert.False(_ghost.Pinned);
        Assert.Empty(GripPins.Unpinned(_arrangement));
        Assert.Null(GripPins.PinAll(_arrangement));

        history.Undo();
        Assert.Equal([false, false, false, true], new[] { _c, _g, _a, _pinned }.Select(t => t.Pinned));
        Assert.Equal([_c, _g, _a], GripPins.Unpinned(_arrangement));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void PinAll_IsNamedAsOneGesture_AndChangesNothingButThePin()
    {
        var frets = new Dictionary<int, int>(_c.Frets);
        var fingers = new Dictionary<int, int>(_c.Fingers);
        var action = Assert.IsType<CompositeAction>(GripPins.PinAll(_arrangement));
        Assert.Equal(GripPins.HistoryLabelKey, action.LabelKey);
        Assert.All(action.Actions, a => Assert.IsType<SetTemplatePinnedAction>(a));

        action.Execute();

        Assert.Equal(frets, _c.Frets);
        Assert.Equal(fingers, _c.Fingers);
        Assert.Equal(7, _arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void UnpinnedGuessedGrips_RaiseNoFinding()
    {
        Assert.NotEmpty(GripPins.Unpinned(_arrangement));

        Assert.DoesNotContain(Findings(_arrangement), i => i.Code is "D100");
    }

    private static ChordTemplate Guessed(int id, string name, Dictionary<int, int> frets)
    {
        var template = new ChordTemplate { Id = id, Name = name, Frets = new Dictionary<int, int>(frets) };
        ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        return template;
    }

    private static Arrangement Arrangement(params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        for (var i = 0; i < templates.Length; i++)
        {
            arrangement.ChordTemplates.Add(templates[i]);
            level.Chords.Add(new Chord { TemplateId = templates[i].Id, Position = new FractionalPosition(1 + i) });
        }

        return arrangement;
    }

    private static List<BeatMarker> Beats() => [.. Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500 })];

    private static List<ArrangementIssue> Findings(Arrangement arrangement) =>
        [.. new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, Beats())];
}
