using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     EOF's Fingerless flag is a toggle on the chord template panel (#622). It is a
///     TEMPLATE edit like the Nop toggle: it reaches what the "Apply to" ladder shows,
///     resolves or mints rather than rewriting a shared shape in place, and is one undo
///     node. Turning it on clears the fingers; setting a finger turns it off again.
/// </summary>
public sealed class ChordTemplateFingerlessToggleTests
{
    [Fact]
    public void TheToggle_ShowsTheLoadedTemplatesFlag_AndTheStripSaysWhy()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5, fingered: false);
        shape.Fingerless = true;
        h.AddChords(shape, 1);

        var vm = h.CreateChordEditor();

        Assert.Same(shape, vm.Template);
        Assert.True(vm.IsFingerless);
        Assert.Equal(FakeLocalization.English()["ChordTemplate_FingerlessStripTip"], vm.FingerStripTip);
    }

    /// <summary>"Every use" rewrites the shape in place: the flag goes on, the fingers go,
    /// and ONE undo puts both back.</summary>
    [Fact]
    public void EveryUse_On_ClearsTheFingersInPlace_AsOneUndoNode()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        shape.FollowedFingering = true;
        var chords = h.AddChords(shape, 2);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.IsFingerless = true;

        Assert.True(shape.Fingerless);
        Assert.Empty(shape.Fingers);
        Assert.False(shape.FollowedFingering);
        Assert.All(chords, c => Assert.Equal(shape.Id, c.TemplateId));
        Assert.Single(h.History.GetUndoSteps());
        Assert.Equal(FakeLocalization.English()["ChordTemplate_FingerlessStripTip"], vm.FingerStripTip);

        h.History.Undo();

        Assert.False(shape.Fingerless);
        Assert.Equal(Harness.Grip, shape.Fingers);
        Assert.False(vm.IsFingerless);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void EveryUse_Off_ClearsTheFlag_AndLeavesTheShapeUnfingered()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5, fingered: false);
        shape.Fingerless = true;
        var chords = h.AddChords(shape, 2);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.IsFingerless = false;

        Assert.False(shape.Fingerless);
        Assert.Empty(shape.Fingers);
        Assert.Single(h.History.GetUndoSteps());
        Assert.Equal(FakeLocalization.English()["ChordTemplate_FingerTip"], vm.FingerStripTip);
        h.History.Undo();
        Assert.True(shape.Fingerless);
    }

    /// <summary>
    ///     "This chord" on a shared shape REBINDS that chord onto a minted Fingerless
    ///     template. The shared one keeps its fingers and its flag, and the other chords
    ///     stay on it. One undo takes the mint and the rebind back together.
    /// </summary>
    [Fact]
    public void ThisChord_OnASharedShape_MintsAFingerlessTemplate_AndNeverTouchesTheShared()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(shape, 3);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);

        vm.IsFingerless = true;

        Assert.False(shape.Fingerless);
        Assert.Equal(Harness.Grip, shape.Fingers);
        Assert.Equal(shape.Id, chords[1].TemplateId);
        Assert.Equal(shape.Id, chords[2].TemplateId);
        var minted = h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.NotSame(shape, minted);
        Assert.True(minted.Fingerless);
        Assert.Empty(minted.Fingers);
        Assert.Equal("A5", minted.Name);
        Assert.Same(minted, vm.Template);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.Equal(shape.Id, chords[0].TemplateId);
        Assert.DoesNotContain(minted, h.Arrangement.ChordTemplates);
        Assert.False(shape.Fingerless);
    }

    /// <summary>
    ///     A shared shape that has NO fingers yet differs from the Fingerless state only
    ///     in the flag. The "identical template" lookup must see it, or it resolves back
    ///     onto the very template the edit came from and the toggle is silently dropped.
    /// </summary>
    [Fact]
    public void ThisChord_OnAnUnfingeredSharedShape_StillMints()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5, fingered: false);
        var chords = h.AddChords(shape, 2);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);

        vm.IsFingerless = true;

        Assert.False(shape.Fingerless);
        Assert.NotEqual(shape.Id, chords[0].TemplateId);
        Assert.True(h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId).Fingerless);
        Assert.Equal(shape.Id, chords[1].TemplateId);
        Assert.True(vm.IsFingerless);
    }

    /// <summary>"Selected" on two of three chords moves both onto ONE minted template; the
    /// third keeps the shared shape as it was.</summary>
    [Fact]
    public void Selected_RebindsTheSelectionOntoOneTemplate()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(shape, 3);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        h.Selection.SelectedChords.Add(chords[1]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);

        vm.IsFingerless = true;

        Assert.False(shape.Fingerless);
        Assert.Equal(Harness.Grip, shape.Fingers);
        Assert.Equal(chords[0].TemplateId, chords[1].TemplateId);
        Assert.NotEqual(shape.Id, chords[0].TemplateId);
        Assert.Equal(shape.Id, chords[2].TemplateId);
        Assert.True(h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId).Fingerless);
        Assert.Single(h.History.GetUndoSteps());
    }

    /// <summary>The flag is a statement about THIS shape: on "+ the same grip" it stays on
    /// its own shape (as Nop does), and the status rail says so.</summary>
    [Fact]
    public void SameGrip_StaysOnItsOwnShape_AndSaysSo()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var moved = h.AddTemplate(1, "B5", 7);
        var chords = h.AddChords(shape, 1);
        h.AddChords(moved, 1);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);

        vm.IsFingerless = true;

        Assert.True(shape.Fingerless);
        Assert.False(moved.Fingerless);
        Assert.Equal(Harness.Grip, moved.Fingers);
        var loc = FakeLocalization.English();
        Assert.EndsWith(loc.Format("ChordTemplate_Applied_GripSkipped", loc["ChordTemplate_Applied_Fingerless"]),
            h.State.MainDisplayMessage);
    }

    /// <summary>Setting a finger on a Fingerless shape turns the flag off, and the toggle
    /// follows.</summary>
    [Fact]
    public void SettingAFinger_TurnsFingerlessOff()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5, fingered: false);
        shape.Fingerless = true;
        var chords = h.AddChords(shape, 1);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        Assert.True(vm.SetFingerForStrings(1, [0]));

        Assert.False(shape.Fingerless);
        Assert.Equal(new Dictionary<int, int> { [0] = 1 }, shape.Fingers);
        Assert.False(vm.IsFingerless);
    }

    /// <summary>Turning it on clears the fingers too, so the kind must be read off the flag
    /// BEFORE the fingers, or it would count as a fingering edit and reach other grips.</summary>
    [Fact]
    public void AFingerlessChange_IsItsOwnEditKind_EvenWithTheFingersCleared()
    {
        var before = new ChordTemplate { Name = "A5", Frets = { [0] = 5 }, Fingers = { [0] = 1 } };
        var after = before.Clone();
        after.Fingerless = true;
        after.Fingers.Clear();

        Assert.Equal(ChordTemplateEditKind.Fingerless, ChordTemplateEditPlanner.KindOf(before, after));
    }

    private sealed class Harness
    {
        public static readonly Dictionary<int, int> Grip = new() { [0] = 1, [1] = 3, [2] = 4 };

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

        public ChordTemplate AddTemplate(int id, string name, int root, bool fingered = true)
        {
            var template = new ChordTemplate { Id = id, Name = name };
            template.Frets[0] = root;
            template.Frets[1] = root + 2;
            template.Frets[2] = root + 2;
            if (fingered) template.Fingers = new Dictionary<int, int>(Grip);
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
