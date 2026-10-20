using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The "Apply to" ladder through the real panel (#484): each rung reaches
///     what it says, the whole gesture is one undo node, the status rail says
///     what changed — plus the two carry-overs from #483 and the empty name box
///     of #515.
/// </summary>
public sealed class ChordApplyLadderPanelTests
{
    /// <summary>The regression the issue asks for: "this chord" changes the
    /// chord the panel shows and no other template, however many share the grip.</summary>
    [Fact]
    public void ThisChord_NeverTouchesAnotherTemplate()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var alt = h.AddTemplate(1, "A5 (alt)", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        var chords = h.AddChords(a5, 3);
        var onAlt = h.AddChords(alt, 1);
        var before = h.Arrangement.ChordTemplates.Select(t => t.Clone()).ToList();

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        h.Selection.SelectedChords.Add(chords[1]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);

        vm.StringInputs[0].FingerText = "1";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, chords[0].TemplateId);
        Assert.Equal(0, chords[1].TemplateId);
        Assert.Equal(0, chords[2].TemplateId);
        Assert.Equal(1, onAlt[0].TemplateId);
        foreach (var (was, now) in before.Zip(new[] { a5, alt, b5 }))
        {
            Assert.Equal(was.Fingers, now.Fingers);
            Assert.Equal(was.Frets, now.Frets);
            Assert.Equal(was.Name, now.Name);
        }

        Assert.Single(h.History.GetUndoSteps());
    }

    /// <summary>"+ the same grip elsewhere" reaches the same frets on another
    /// template AND the grip moved along the neck, in ONE undo node — the old
    /// hidden propagation and the "Finger N alike shapes" button in one rung.</summary>
    /// <summary>
    ///     #572: the compact scope bar prints ONE count, the lit rung's, and every rung says its
    ///     own count in its tooltip — so the count a rung no longer prints is still a hover away
    ///     and still the one the planner writes through.
    /// </summary>
    [Fact]
    public void TheCompactBar_PrintsTheLitRungsReach_AndEveryRungsCountIsInItsTip()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(a5, 3);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        Assert.Equal(ChordApplyRung.Selected, vm.Ladder.Effective);
        Assert.Equal(vm.Ladder.Selected.Detail, vm.Ladder.EffectiveDetail);
        Assert.True(vm.Ladder.HasEffectiveDetail);

        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        Assert.Equal(vm.Ladder.EveryUse.Detail, vm.Ladder.EffectiveDetail);
        foreach (var row in vm.Ladder.Rows.Where(r => r.HasDetail))
        {
            Assert.StartsWith(row.Detail, row.ToolTipText);
            Assert.EndsWith(row.Tip, row.ToolTipText);
        }
    }

    [Fact]
    public void SameGrip_ReachesSameFretAndTransposedTemplates_InOneUndoNode()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var alt = h.AddTemplate(1, "A5 (alt)", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        b5.Fingers[0] = 2; // a guess the rung overwrites
        var chords = h.AddChords(a5, 2);
        h.AddChords(alt, 1);
        h.AddChords(b5, 2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        Assert.Equal(2, vm.Ladder.SameGrip.Reach.GripTemplates);

        vm.StringInputs[0].FingerText = "1";
        vm.StringInputs[1].FingerText = "3";
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        foreach (var template in new[] { a5, alt, b5 })
            Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }, template.Fingers);
        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
        Assert.Single(h.History.GetUndoSteps());
        Assert.Equal(Applied("Fingering", chords: 5, shapes: 3), h.State.MainDisplayMessage);

        h.History.Undo();
        Assert.Empty(a5.Fingers);
        Assert.Empty(alt.Fingers);
        Assert.Equal(new Dictionary<int, int> { [0] = 2 }, b5.Fingers);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>Carry-over 1 from #483: with NOTHING selected, Save fingering
    /// follows the ladder like any edit — "+ same grip" reaches the other grips;
    /// the default leaves them alone. It used to skip them silently.</summary>
    [Theory]
    [InlineData(ChordApplyRung.Selected, false)]
    [InlineData(ChordApplyRung.SameGrip, true)]
    public void NothingSelected_SaveFingering_FollowsTheLadder(ChordApplyRung rung, bool reachesSibling)
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var alt = h.AddTemplate(1, "A5 (alt)", 5);
        h.AddChords(a5, 2);

        var vm = h.CreateChordEditor();
        Assert.Same(a5, vm.Template);
        vm.ChooseApplyRungCommand.Execute(rung);

        // A complete fingering: only that one travels to the other grips.
        vm.StringInputs[0].FingerText = "1";
        vm.StringInputs[1].FingerText = "3";
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(1, a5.Fingers[0]);
        Assert.Equal(reachesSibling, alt.Fingers.ContainsKey(0));
    }

    /// <summary>Carry-over 2 from #483: with nothing selected, picking a
    /// template in the dropdown BROWSES to it. It used to do nothing.</summary>
    [Fact]
    public void NothingSelected_TheDropdownBrowsesToThePickedTemplate()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        var chords = h.AddChords(a5, 1);

        var vm = h.CreateChordEditor();
        vm.SelectedDropdownTemplate = vm.AvailableTemplates.Single(t => t.Template == b5);

        Assert.Same(b5, vm.Template);
        Assert.Equal("B5", vm.ChordName);
        Assert.Equal(0, chords[0].TemplateId);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void TheDropdown_OnEveryUse_MovesEveryUseOntoThePick()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        var chords = h.AddChords(a5, 3);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        vm.SelectedDropdownTemplate = vm.AvailableTemplates.Single(t => t.Template == b5);

        Assert.All(chords, c => Assert.Equal(2, c.TemplateId));
        Assert.All(chords, c => Assert.Equal(7, c.ChordNotes.Single(n => n.StringIndex == 0).Fret));
        Assert.Single(h.History.GetUndoSteps());
    }

    /// <summary>#515: selecting a chord shows its template's name, and renaming
    /// it reaches what the lit rung says.</summary>
    [Fact]
    public void SelectingAChord_ShowsItsName_AndARenameFollowsTheLadder()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(a5, 2);
        var vm = h.CreateChordEditor();

        h.Selection.SelectedChords.Add(chords[0]);
        Assert.Equal("A5", vm.ChordName);
        Assert.Contains("A5", vm.Names.KnownChordNames);

        // "selected" on a shared shape: the rename moves this chord alone.
        Assert.True(vm.Ladder.Selected.IsEffective);
        vm.ChordName = "A5 power";
        Assert.Equal("A5", a5.Name);
        Assert.Equal("A5 power", h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId).Name);
        Assert.Equal(0, chords[1].TemplateId);
        Assert.Equal("A5 power", vm.ChordName);

        // "every use": the rename is the shape's.
        h.Selection.SelectedChords.Clear();
        h.Selection.SelectedChords.Add(chords[1]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        vm.ChordName = "A5!";
        Assert.Equal("A5!", a5.Name);
    }

    /// <summary>A null written into the name (the box losing its selection) is
    /// never a rename.</summary>
    [Fact]
    public void ANullName_IsNotARename()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        h.AddChords(a5, 1);
        var vm = h.CreateChordEditor();

        vm.ChordName = null!;

        Assert.Equal("A5", a5.Name);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>A shape edit on the "same grip" rung stays on its shape, and the
    /// status rail says so rather than letting the lit rung imply otherwise.</summary>
    [Fact]
    public void AReshapeOnSameGrip_StaysOnItsShape_AndSaysSo()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        var chords = h.AddChords(a5, 2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        vm.StringInputs[2].FretText = "9";

        Assert.Equal(9, a5.Frets[2]);
        Assert.All(chords, c => Assert.Equal(9, c.ChordNotes.Single(n => n.StringIndex == 2).Fret));
        Assert.Equal(new Dictionary<int, int> { [0] = 7, [1] = 9, [2] = 9 }, b5.Frets);
        var loc = FakeLocalization.English();
        Assert.Equal(Applied("Shape", chords: 2, shapes: 1) + " " +
                     loc.Format("ChordTemplate_Applied_GripSkipped", loc["ChordTemplate_Applied_Shape"]),
            h.State.MainDisplayMessage);
    }

    /// <summary>
    ///     Review of #521, finding 1, through the real panel: A5 is fingered on one
    ///     string, B5 (the same grip two frets up) on all three. On "+ same grip"
    ///     neither the finger keybind nor "Save &amp; next missing" with nothing
    ///     typed may copy the partial fingering onto B5 — the copy overwrites, so
    ///     it would erase two of B5's fingers.
    /// </summary>
    [Fact]
    public void SameGrip_APartialFingering_NeverOverwritesACompleteGrip()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        a5.Fingers[0] = 1;
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }) b5.Fingers[s] = f;
        var complete = new Dictionary<int, int>(b5.Fingers);
        var chords = h.AddChords(a5, 2);
        h.AddChords(b5, 1);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        Assert.False(vm.CanSaveFingering);

        vm.SelectNextMissingFingeringCommand.Execute(null);
        Assert.Equal(complete, b5.Fingers);
        Assert.False(h.History.CanUndo);

        h.Selection.SelectedChords.Clear();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        Assert.True(vm.SetFingerForStrings(2, [0]));

        Assert.Equal(new Dictionary<int, int> { [0] = 2 }, a5.Fingers);
        Assert.Equal(complete, b5.Fingers);
        Assert.EndsWith(FakeLocalization.English()["ChordTemplate_Applied_GripNeedsEveryFinger"], h.State.MainDisplayMessage);
    }

    /// <summary>"Save &amp; next missing" saves what was TYPED. With nothing typed
    /// it walks on, even on "+ same grip" where Save itself has work (a grip
    /// fingered differently) — walking past a shape is not a copy.</summary>
    [Fact]
    public void SaveAndNextMissing_WithNothingTyped_CopiesNothing()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }) a5.Fingers[s] = f;
        b5.Fingers[0] = 2;
        var chords = h.AddChords(a5, 1);
        h.AddChords(b5, 1);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        Assert.True(vm.CanSaveFingering);

        vm.SelectNextMissingFingeringCommand.Execute(null);

        Assert.Equal(new Dictionary<int, int> { [0] = 2 }, b5.Fingers);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>The status rail counts what the "same grip" write CHANGED: a grip
    /// already fingered alike is not counted, and a hand shape on a grip it
    /// re-fingers is.</summary>
    [Fact]
    public void SameGrip_TheStatusRail_CountsOnlyTheGripsItChanged_WithTheirHandShapes()
    {
        var h = new Harness();
        var a5 = h.AddTemplate(0, "A5", 5);
        var alt = h.AddTemplate(1, "A5 (alt)", 5);
        var b5 = h.AddTemplate(2, "B5", 7);
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }) b5.Fingers[s] = f;
        var chords = h.AddChords(a5, 2);
        h.AddChords(alt, 1);
        h.Arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            TemplateId = alt.Id, Position = new FractionalPosition(100), EndPosition = new FractionalPosition(104)
        });
        h.AddChords(b5, 2);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        vm.StringInputs[0].FingerText = "1";
        vm.StringInputs[1].FingerText = "3";
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        var loc = FakeLocalization.English();
        var expected = loc.Format("ChordTemplate_Applied", loc["ChordTemplate_Applied_Fingering"],
            loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", 3) + " · " +
            loc.Plural("ChordTemplate_Reach_HandShapesOne", "ChordTemplate_Reach_HandShapes", 1),
            loc.Plural("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes", 2));
        Assert.Equal(expected, h.State.MainDisplayMessage);
    }

    /// <summary>The status line in the culture the panel is using right now.</summary>
    private static string Applied(string kind, int chords, int shapes)
    {
        var loc = FakeLocalization.English();
        return loc.Format("ChordTemplate_Applied", loc[$"ChordTemplate_Applied_{kind}"],
            loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", chords),
            loc.Plural("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes", shapes));
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        /// <summary>A three-string power-chord grip rooted at <paramref name="root" />.</summary>
        public ChordTemplate AddTemplate(int id, string name, int root)
        {
            var template = new ChordTemplate { Id = id, Name = name };
            template.Frets[0] = root;
            template.Frets[1] = root + 2;
            template.Frets[2] = root + 2;
            Arrangement.ChordTemplates.Add(template);
            return template;
        }

        public List<Chord> AddChords(ChordTemplate template, int count)
        {
            var result = new List<Chord>();
            for (var i = 0; i < count; i++)
            {
                var chord = new Chord { TemplateId = template.Id, Position = new FractionalPosition(Arrangement.Levels[0].Chords.Count * 4) };
                foreach (var (stringIndex, fret) in template.Frets)
                    chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
                Arrangement.Levels[0].Chords.Add(chord);
                result.Add(chord);
            }

            return result;
        }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), projectState: State);

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
    }
}
