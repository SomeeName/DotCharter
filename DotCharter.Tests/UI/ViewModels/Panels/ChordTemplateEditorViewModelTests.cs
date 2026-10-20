using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
/// Regression tests for the chord-template inspector's selection→display and
/// edit→reconcile paths. The bug that motivated them: a freshly GP-imported
/// chart leaves every chord at TemplateId -1 (RscpTemplateManager assigns real
/// ids only on save), so selecting such a chord fell through to
/// ChordTemplates.First() and the inspector showed a completely unrelated
/// shape ("12 12 12 12" for a 14/15 chord).
/// </summary>
public sealed class ChordTemplateEditorViewModelTests
{
    [Fact]
    public void SelectingUntemplatedChord_ShowsItsOwnNotes_NotTheFirstTemplate()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;

        // A "wrong" first template — what the old First()-fallback showed.
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "wrong",
            Frets = new Dictionary<int, int> { { 2, 12 }, { 3, 12 }, { 4, 12 }, { 5, 12 } },
            Fingers = new Dictionary<int, int> { { 2, 1 }, { 3, 1 }, { 4, 1 }, { 5, 1 } }
        });

        var chord = UntemplatedChord((4, 14), (5, 15));
        arr.Levels[0].Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        Assert.Equal("14", vm.StringInputs[4].FretText);
        Assert.Equal("15", vm.StringInputs[5].FretText);
        Assert.True(string.IsNullOrEmpty(vm.StringInputs[2].FretText));
        Assert.True(string.IsNullOrEmpty(vm.StringInputs[3].FretText));
    }

    [Fact]
    public void SelectingChord_WithMismatchedTemplate_ShowsTheChordsNotes()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;

        // A real template the chord POINTS at but whose frets don't match it.
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "stale",
            Frets = new Dictionary<int, int> { { 2, 12 }, { 3, 12 }, { 4, 12 }, { 5, 12 } }
        });

        var chord = new Chord { TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 14 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 15 });
        arr.Levels[0].Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        Assert.Equal("14", vm.StringInputs[4].FretText);
        Assert.Equal("15", vm.StringInputs[5].FretText);
    }

    [Fact]
    public void EditingUntemplatedChordFret_ReshapesOnlyThatChord_LeavesOtherUntemplatedChordsAlone()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        var c1 = UntemplatedChord((4, 14), (5, 15));
        var c2 = UntemplatedChord((2, 5), (3, 7));
        c2.Position = new FractionalPosition(4);
        level.Chords.Add(c1);
        level.Chords.Add(c2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(c1);

        // Change c1's high-e fret 15 -> 16 via the inspector's fret field.
        vm.StringInputs[5].FretText = "16";

        // c1 reshaped and now carries a real matching template.
        Assert.Equal(16, c1.ChordNotes.First(n => n.StringIndex == 5).Fret);
        Assert.True(c1.TemplateId >= 0);
        var t1 = arr.ChordTemplates.First(t => t.Id == c1.TemplateId);
        Assert.Equal(16, t1.Frets[5]);
        Assert.Equal(14, t1.Frets[4]);

        // c2 — a DIFFERENT untemplated chord — must be completely untouched.
        Assert.Equal(-1, c2.TemplateId);
        Assert.Equal(5, c2.ChordNotes.First(n => n.StringIndex == 2).Fret);
        Assert.Equal(7, c2.ChordNotes.First(n => n.StringIndex == 3).Fret);
    }

    [Fact]
    public void EditingUntemplatedChord_ReusesAnEquivalentTemplate_WhenTheNewShapeAlreadyExists()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        // An existing template for the shape the edit will PRODUCE.
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 3, Name = "C5",
            Frets = new Dictionary<int, int> { { 2, 5 }, { 3, 5 } },
            Fingers = new Dictionary<int, int> { { 2, 1 }, { 3, 3 } }
        });

        var chord = UntemplatedChord((2, 5), (3, 7));
        level.Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        var templateCountBefore = arr.ChordTemplates.Count;

        // 7 -> 5 makes the chord exactly the existing C5 shape.
        vm.StringInputs[3].FretText = "5";

        Assert.Equal(3, chord.TemplateId);                       // bound to the existing template
        Assert.Equal(templateCountBefore, arr.ChordTemplates.Count); // no duplicate minted
        Assert.Equal(5, chord.ChordNotes.First(n => n.StringIndex == 3).Fret);
    }

    [Fact]
    public void NoSelection_KeepsBrowseMode_ShowingTheFirstTemplate()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "first",
            Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 }, { 2, 5 } }
        });

        var vm = h.CreateChordEditor();

        // Nothing selected: the dropdown-browse default still loads template 0.
        Assert.Equal("3", vm.StringInputs[0].FretText);
        Assert.Equal("5", vm.StringInputs[1].FretText);
        Assert.Equal("5", vm.StringInputs[2].FretText);
    }

    [Fact]
    public void EditingConsistentChordFret_StillPropagatesGloballyToSiblings()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "A5",
            Frets = new Dictionary<int, int> { { 1, 7 }, { 2, 7 } }
        });

        var c1 = TemplatedChord(0, (1, 7), (2, 7));
        var c2 = TemplatedChord(0, (1, 7), (2, 7));
        c2.Position = new FractionalPosition(4);
        level.Chords.Add(c1);
        level.Chords.Add(c2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(c1);

        // 0.36: the reshape scope DEFAULTS to "this chord only" (owner-confirmed,
        // 04-inspector-redesign.md §7.1) — a global reshape is a bulk edit and
        // has to be asked for. Picking the global half restores exactly the
        // behaviour this test has always pinned.
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        vm.StringInputs[2].FretText = "9";

        Assert.Equal(9, c1.ChordNotes.First(n => n.StringIndex == 2).Fret);
        Assert.Equal(9, c2.ChordNotes.First(n => n.StringIndex == 2).Fret);
    }

    [Fact]
    public void ReshapeDefaultsToThisChordOnly_AndLeavesItsSiblingsAlone()
    {
        // The other half of the flipped default, pinned so it cannot drift back:
        // with the scope untouched, reshaping ONE chord must not rewrite every
        // chord in the level that happens to share its shape. That silent
        // level-wide rewrite was the audit's §D.2/§D.3 finding.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "A5",
            Frets = new Dictionary<int, int> { { 1, 7 }, { 2, 7 } }
        });

        var c1 = TemplatedChord(0, (1, 7), (2, 7));
        var c2 = TemplatedChord(0, (1, 7), (2, 7));
        c2.Position = new FractionalPosition(4);
        level.Chords.Add(c1);
        level.Chords.Add(c2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(c1);

        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);
        vm.StringInputs[2].FretText = "9";

        Assert.Equal(9, c1.ChordNotes.First(n => n.StringIndex == 2).Fret);
        Assert.Equal(7, c2.ChordNotes.First(n => n.StringIndex == 2).Fret);
    }

    [Fact]
    public void SelectingHandShape_ShowsItsTemplateFingering()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "C5",
            Frets = new Dictionary<int, int> { { 2, 3 }, { 3, 5 } },
            Fingers = new Dictionary<int, int> { { 2, 1 }, { 3, 3 } }
        });
        level.Chords.Add(UntemplatedChord((2, 3), (3, 5)));
        var hs = new HandShape { TemplateId = 0, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
        level.HandShapes.Add(hs);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(hs);

        Assert.Equal("3", vm.StringInputs[2].FretText);
        Assert.Equal("5", vm.StringInputs[3].FretText);
        Assert.Equal("1", vm.StringInputs[2].FingerText);
        Assert.Equal("3", vm.StringInputs[3].FingerText);
    }

    [Fact]
    public void EditingHandShapeFret_ReshapesAndBindsItsEnclosedUntemplatedChords()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "C5",
            Frets = new Dictionary<int, int> { { 2, 3 }, { 3, 5 } },
            Fingers = new Dictionary<int, int> { { 2, 1 }, { 3, 3 } }
        });

        // Freshly-imported chords under the bar sit at TemplateId -1.
        var c1 = UntemplatedChord((2, 3), (3, 5));
        var c2 = UntemplatedChord((2, 3), (3, 5));
        c2.Position = new FractionalPosition(1);
        level.Chords.Add(c1);
        level.Chords.Add(c2);
        var hs = new HandShape { TemplateId = 0, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
        level.HandShapes.Add(hs);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(hs);

        // Move the handshape's string-3 fret 5 -> 7.
        vm.StringInputs[3].FretText = "7";

        // Both enclosed chords follow the shape edit AND get bound to the template,
        // so the highway note heads and the 3D fingering/handshape bar stay aligned.
        Assert.Equal(7, c1.ChordNotes.First(n => n.StringIndex == 3).Fret);
        Assert.Equal(7, c2.ChordNotes.First(n => n.StringIndex == 3).Fret);
        Assert.Equal(0, c1.TemplateId);
        Assert.Equal(0, c2.TemplateId);
    }

    [Fact]
    public void EditingHandShapeFret_LeavesUnrelatedChordsOutsideTheBarAlone()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "C5",
            Frets = new Dictionary<int, int> { { 2, 3 }, { 3, 5 } }
        });

        var inside = UntemplatedChord((2, 3), (3, 5));
        var outside = UntemplatedChord((2, 3), (3, 5));
        outside.Position = new FractionalPosition(10); // beyond the handshape span
        level.Chords.Add(inside);
        level.Chords.Add(outside);
        var hs = new HandShape { TemplateId = 0, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
        level.HandShapes.Add(hs);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(hs);
        vm.StringInputs[3].FretText = "7";

        Assert.Equal(7, inside.ChordNotes.First(n => n.StringIndex == 3).Fret);
        // The identical-shape chord OUTSIDE the bar must not be touched.
        Assert.Equal(5, outside.ChordNotes.First(n => n.StringIndex == 3).Fret);
        Assert.Equal(-1, outside.TemplateId);
    }

    private static Chord UntemplatedChord(params (int str, int fret)[] notes)
    {
        var c = new Chord { TemplateId = -1 };
        foreach (var (str, fret) in notes)
            c.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return c;
    }

    private static Chord TemplatedChord(int templateId, params (int str, int fret)[] notes)
    {
        var c = new Chord { TemplateId = templateId };
        foreach (var (str, fret) in notes)
            c.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return c;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    /// <summary>
    ///     Static perf audit 2026-08-29 P1 #13: <c>ChordName</c> is bound to a
    ///     TextBox and committed a full-chart undoable edit per KEYSTROKE — eight
    ///     undo nodes for an eight-letter rename, each one re-cloning every chord
    ///     on the template. The name edit now names a merge verb, so a typing run
    ///     inside the history's 900 ms merge window is ONE node whose undo lands
    ///     on the name the user started from — exactly what a sustain drag or a
    ///     wheel run already gets.
    /// </summary>
    [Fact]
    public void TypingAChordName_IsOneUndoNode_NotOnePerKeystroke()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "A5",
            Frets = new Dictionary<int, int> { { 1, 7 }, { 2, 7 } }
        });
        var c1 = new Chord { TemplateId = 0 };
        c1.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        c1.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        var c2 = new Chord { TemplateId = 0, Position = new FractionalPosition(2) };
        c2.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        c2.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        level.Chords.Add(c1);
        level.Chords.Add(c2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(c1);
        Assert.Equal("A5", vm.ChordName);
        // "All N" scope: a rename of a SHARED template is the global edit.
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        // One keystroke at a time, as the TextBox delivers it.
        foreach (var typed in new[] { "A", "Am", "Am7" })
            vm.ChordName = typed;

        Assert.Equal("Am7", arr.ChordTemplates[0].Name);
        Assert.Equal("Am7", vm.ChordName);
        // …and no template was minted along the way: the panel kept the REAL
        // template through every keystroke (it used to reload the clone).
        Assert.Single(arr.ChordTemplates);
        Assert.Same(arr.ChordTemplates[0], vm.Template);
        Assert.True(h.History.CanUndo);

        h.History.Undo();
        Assert.Equal("A5", arr.ChordTemplates[0].Name);
        Assert.False(h.History.CanUndo, "the three keystrokes left more than one undo node behind");
    }

    /// <summary>A shape edit is a discrete gesture and must NOT fold into a
    /// name run — two different verbs, two nodes.</summary>
    [Fact]
    public void AShapeEdit_DoesNotMergeIntoANameRun()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var level = arr.Levels[0];

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "A5",
            Frets = new Dictionary<int, int> { { 1, 7 }, { 2, 7 } }
        });
        var c1 = new Chord { TemplateId = 0 };
        c1.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        c1.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        level.Chords.Add(c1);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(c1);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.ChordName = "A";
        vm.ChordName = "Am";
        vm.StringInputs[2].FretText = "9";

        h.History.Undo();
        Assert.Equal(7, c1.ChordNotes.First(n => n.StringIndex == 2).Fret);
        Assert.True(h.History.CanUndo, "the name run and the reshape should be two nodes");
        h.History.Undo();
        Assert.Equal("A5", arr.ChordTemplates[0].Name);
        Assert.False(h.History.CanUndo);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher());
    }
}
