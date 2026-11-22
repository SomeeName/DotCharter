using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A fingering edit on one chord inside a hand shape reaches the whole hand
///     shape: every chord of the shown shape under the bar plus the bar's
///     own template, in ONE undo step. The same shape under ANOTHER bar is the
///     shape editor's "every use" scope and stays out; an arpeggio bar differs
///     chord by chord on purpose and is left alone.
/// </summary>
public sealed class HandShapeFingeringReachTests
{
    private const int Strums = 5;

    [Fact]
    public void FingeringChordOne_PutsTheWholeHandShapeOnOneTemplate_AndKeepsTheRepeatSet()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var chords = Enumerable.Range(0, Strums).Select(i => AddChord(arr, i * 4, 0)).ToList();
        var bar = AddHandShape(arr, 0, Strums * 4, 0);
        var repeatsBefore = RepeatIndexes(arr);
        Assert.NotEmpty(repeatsBefore);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        var ids = chords.Select(c => c.TemplateId).Append(bar.TemplateId).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == ids[0]).Fingers[2]);
        Assert.Equal(repeatsBefore, RepeatIndexes(arr));

        h.History.Undo();

        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void TheSameShapeUnderAnotherHandShape_IsLeftAlone()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var first = AddChord(arr, 0, 0);
        AddChord(arr, 4, 0);
        var elsewhere = AddChord(arr, 40, 0);
        AddHandShape(arr, 0, 8, 0);
        var otherBar = AddHandShape(arr, 40, 44, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(first);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, first.TemplateId);
        Assert.Equal(0, elsewhere.TemplateId);
        Assert.Equal(0, otherBar.TemplateId);
    }

    [Fact]
    public void AnArpeggioHandShape_IsNotSwept()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var arpeggio = Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]);
        arpeggio.Arpeggio = true;
        arr.ChordTemplates.Add(arpeggio);
        var first = AddChord(arr, 0, 0);
        var second = AddChord(arr, 4, 0);
        AddHandShape(arr, 0, 8, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(first);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(0, second.TemplateId);
    }

    [Fact]
    public void AChordOnTheBoundaryOfAbuttingHandShapes_BelongsToTheLaterOneOnly()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var inEarlier = AddChord(arr, 4, 0);
        var onBoundary = AddChord(arr, 8, 0);
        var inLater = AddChord(arr, 12, 0);
        var earlier = AddHandShape(arr, 0, 8, 0);
        var later = AddHandShape(arr, 8, 16, 0);
        AddChord(arr, 40, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(inEarlier);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, inEarlier.TemplateId);
        Assert.Equal(0, onBoundary.TemplateId);
        Assert.Equal(0, inLater.TemplateId);
        Assert.Equal(0, later.TemplateId);
        Assert.Equal(inEarlier.TemplateId, earlier.TemplateId);
    }

    [Fact]
    public void EditingTheChordOnTheBoundary_ReachesTheLaterHandShapeAndNotTheEarlierOne()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var inEarlier = AddChord(arr, 4, 0);
        var onBoundary = AddChord(arr, 8, 0);
        var inLater = AddChord(arr, 12, 0);
        var earlier = AddHandShape(arr, 0, 8, 0);
        var later = AddHandShape(arr, 8, 16, 0);
        AddChord(arr, 40, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(onBoundary);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, onBoundary.TemplateId);
        Assert.Equal(onBoundary.TemplateId, inLater.TemplateId);
        Assert.Equal(onBoundary.TemplateId, later.TemplateId);
        Assert.Equal(0, inEarlier.TemplateId);
        Assert.Equal(0, earlier.TemplateId);
    }

    [Fact]
    public void AChordExactlyAtTheHandShapeStart_IsIncluded()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var atStart = AddChord(arr, 8, 0);
        var selected = AddChord(arr, 12, 0);
        AddHandShape(arr, 8, 16, 0);
        AddChord(arr, 40, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(selected);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, selected.TemplateId);
        Assert.Equal(selected.TemplateId, atStart.TemplateId);
    }

    [Fact]
    public void AHandShapeThatEndsWhereItStarts_CoversNothingAndSweepsNothing()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var first = AddChord(arr, 0, 0);
        var later = AddChord(arr, 20, 0);
        var empty = AddHandShape(arr, 0, 0, 0);
        AddHandShape(arr, 0, 8, 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(first);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        // EndPosition is a non-nullable position: the domain has no open-ended
        // hand shape, so nothing here can reach the rest of the level.
        Assert.Equal(0, later.TemplateId);
        Assert.Equal(0, empty.TemplateId);
    }

    [Fact]
    public void AnotherDifficultyLevel_IsUntouched()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var first = AddChord(arr, 0, 0);
        AddChord(arr, 4, 0);
        AddHandShape(arr, 0, 8, 0);
        var otherLevel = new ChartLevel();
        arr.Levels.Add(otherLevel);
        var otherChord = new Chord { TemplateId = 0, Position = new FractionalPosition(4) };
        otherChord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 6 });
        otherChord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 6 });
        otherLevel.Chords.Add(otherChord);
        var otherBar = new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(8), TemplateId = 0
        };
        otherLevel.HandShapes.Add(otherBar);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(first);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, first.TemplateId);
        Assert.Equal(0, otherChord.TemplateId);
        Assert.Equal(0, otherBar.TemplateId);
    }

    [Fact]
    public void ASameNotesChordOnAnotherTemplate_IsRebound_AndThatTemplateKeepsItsOtherUsers()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        arr.ChordTemplates.Add(Shape(1, [(1, 6), (2, 6)], [(1, 2), (2, 2)]));
        var first = AddChord(arr, 0, 0);
        var sibling = AddChord(arr, 4, 1);
        var elsewhereOnOld = AddChord(arr, 40, 1);
        var keepsZero = AddChord(arr, 44, 0);
        AddHandShape(arr, 0, 8, 0);
        var oldTemplateBefore = Describe(arr.ChordTemplates.Single(t => t.Id == 1));

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(first);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, first.TemplateId);
        Assert.Equal(first.TemplateId, sibling.TemplateId);
        Assert.Equal(1, elsewhereOnOld.TemplateId);
        Assert.Equal(0, keepsZero.TemplateId);
        Assert.Equal(oldTemplateBefore, Describe(arr.ChordTemplates.Single(t => t.Id == 1)));
    }

    [Fact]
    public void Undo_RestoresTheTemplateListContentAndNotJustTheIds()
    {
        var h = new Harness();
        var (chords, _) = SeedBar(h);
        var arr = h.Editor.ActiveArrangement!;
        var before = arr.ChordTemplates.Select(Describe).ToList();

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);
        Assert.NotEqual(before, arr.ChordTemplates.Select(Describe).ToList());

        h.History.Undo();

        Assert.Equal(before, arr.ChordTemplates.Select(Describe).ToList());
    }

    [Fact]
    public void FingerButtons_AlsoReachTheWholeHandShape()
    {
        var h = new Harness();
        var (chords, _) = SeedBar(h);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        Assert.True(vm.SetFingerForStrings(3, [2]));

        Assert.Single(chords.Select(c => c.TemplateId).Distinct());
        Assert.NotEqual(0, chords[0].TemplateId);
    }

    [Fact]
    public void HoverTypedFinger_ThenSave_ReachesTheWholeHandShape()
    {
        var h = new Harness();
        var (chords, _) = SeedBar(h);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        vm.SetHoveredFinger(new DotCharter.UI.Controls.ChordBoxFingerEdit(2, 3));
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Single(chords.Select(c => c.TemplateId).Distinct());
        Assert.NotEqual(0, chords[0].TemplateId);
    }

    [Fact]
    public void Route1_TheHandShapeBarSelected_FingerButtonThenSave_ReachesEveryChordAndTheBar()
    {
        var h = new Harness();
        var (chords, bar) = SeedBar(h);
        var arr = h.Editor.ActiveArrangement!;
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(bar);

        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        var ids = chords.Select(c => c.TemplateId).Append(bar.TemplateId).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == ids[0]).Fingers[2]);
        h.History.Undo();
        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void Route1_TheHandShapeBarSelected_SetFingerButtons_ReachEveryChordAndTheBar()
    {
        var h = new Harness();
        var (chords, bar) = SeedBar(h);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(bar);

        Assert.True(vm.SetFingerForStrings(3, [2]));

        Assert.Single(chords.Select(c => c.TemplateId).Append(bar.TemplateId).Distinct());
        Assert.NotEqual(0, bar.TemplateId);
    }

    [Theory]
    [InlineData("unbound", true)]
    [InlineData("unbound", false)]
    [InlineData("unboundOnlyUse", true)]
    [InlineData("unboundOnlyUse", false)]
    [InlineData("ownTemplates", true)]
    [InlineData("ownTemplates", false)]
    [InlineData("noFingers", true)]
    [InlineData("noFingers", false)]
    [InlineData("noFingersUnbound", true)]
    [InlineData("noFingersUnbound", false)]
    public void RealisticCharts_BothRoutes_PutTheBarAndEveryChordOnTheNewFingering(string seed, bool barSelected)
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var fingered = !seed.StartsWith("noFingers");
        var unbound = seed.Contains("nbound");
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], fingered ? [(1, 1), (2, 1)] : []));
        var chords = new List<Chord>();
        for (var i = 0; i < Strums; i++)
        {
            var id = unbound ? -1 : 0;
            if (seed == "ownTemplates")
            {
                id = 10 + i;
                arr.ChordTemplates.Add(Shape(id, [(1, 6), (2, 6)], [(1, 1 + (i % 2)), (2, 1)]));
            }

            chords.Add(AddChord(arr, i * 4, id));
        }

        var bar = AddHandShape(arr, 0, Strums * 4, 0);
        var other = AddHandShape(arr, 80, 88, 0);
        var otherChord = AddChord(arr, 80, 0);
        if (seed is "unboundOnlyUse" or "noFingersUnbound") arr.Levels[0].HandShapes.Remove(other);
        if (seed == "unboundOnlyUse") arr.Levels[0].Chords.Remove(otherChord);
        var vm = h.CreateChordEditor();
        if (barSelected) h.Selection.SelectedHandShapes.Add(bar);
        else h.Selection.SelectedChords.Add(chords[0]);

        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        var ids = chords.Select(c => c.TemplateId).Append(bar.TemplateId).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == ids[0]).Fingers[2]);
        if (seed != "unboundOnlyUse") Assert.Equal(0, otherChord.TemplateId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedChordTemplate_AndABarOnAnotherTemplate_BothRoutesMoveTheBarAndEveryChordToOneNewTemplate(bool barSelected)
    {
        // The owner's Doloris bar: the five strums share a template with two chords
        // OUTSIDE the bar, and the bar itself points at a different template (one string
        // over). The edit must mint ONE template for the strums and the bar and leave the
        // two chords elsewhere on the old fingering.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        arr.ChordTemplates.Add(Shape(1, [(2, 6), (3, 6)], [(2, 1), (3, 1)]));
        var chords = Enumerable.Range(0, Strums).Select(i => AddChord(arr, i * 4, 0)).ToList();
        var bar = AddHandShape(arr, 0, Strums * 4, 1);
        var elsewhere = new[] { AddChord(arr, 80, 0), AddChord(arr, 84, 0) };
        var before = arr.ChordTemplates.Select(Describe).ToList();

        var vm = h.CreateChordEditor();
        if (barSelected) h.Selection.SelectedHandShapes.Add(bar);
        else h.Selection.SelectedChords.Add(chords[0]);
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        var ids = chords.Select(c => c.TemplateId).Append(bar.TemplateId).Distinct().ToList();
        Assert.Single(ids);
        Assert.NotEqual(0, ids[0]);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == ids[0]).Fingers[2]);
        Assert.All(elsewhere, c => Assert.Equal(0, c.TemplateId));

        h.History.Undo();

        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
        Assert.Equal(1, bar.TemplateId);
        Assert.Equal(before, arr.ChordTemplates.Select(Describe).ToList());
    }

    private static (List<Chord> Chords, HandShape Bar) SeedBar(Harness h)
    {
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, [(1, 6), (2, 6)], [(1, 1), (2, 1)]));
        var chords = Enumerable.Range(0, Strums).Select(i => AddChord(arr, i * 4, 0)).ToList();
        var bar = AddHandShape(arr, 0, Strums * 4, 0);
        AddChord(arr, 80, 0);
        return (chords, bar);
    }

    private static string Describe(ChordTemplate t) =>
        $"{t.Id}|{t.Name}|{string.Join(',', t.Frets.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))}|" +
        string.Join(',', t.Fingers.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));

    private static HashSet<int> RepeatIndexes(Arrangement arr)
    {
        var level = arr.Levels[0];
        var repeats = RepeatChords.Compute(arr.ChordTemplates, level.Notes, level.Chords, level.HandShapes, []);
        return [.. repeats.Select(c => level.Chords.IndexOf(c))];
    }

    private static ChordTemplate Shape(int id, (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[] fingers)
    {
        var template = new ChordTemplate { Id = id, Name = "F5" };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        foreach (var (stringIndex, finger) in fingers) template.Fingers[stringIndex] = finger;
        return template;
    }

    private static Chord AddChord(Arrangement arr, int position, int templateId)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(position) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 6 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 6 });
        arr.Levels[0].Chords.Add(chord);
        return chord;
    }

    private static HandShape AddHandShape(Arrangement arr, int start, int end, int templateId)
    {
        var shape = new HandShape
        {
            Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end), TemplateId = templateId
        };
        arr.Levels[0].HandShapes.Add(shape);
        return shape;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection,
                new TempoService(logger), new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), Config);
    }
}
