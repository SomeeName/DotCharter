using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A fingering typed on the chord panel is the charter's (#508): whatever rung
///     of the "Apply to" ladder it goes through, the template it lands on reads
///     <see cref="ChordTemplate.FollowedFingering" /> false, so Generate FHP never
///     re-fingers it. Edits that leave the fingering alone keep the mark.
/// </summary>
public sealed class ChordTemplateFollowedFingeringTests
{
    private static ChordTemplate Minted(Harness h, int id, string name)
    {
        var template = h.AddTemplate(id, name, 5);
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 2, [1] = 4, [2] = 4 }) template.Fingers[s] = f;
        template.FollowedFingering = true;
        return template;
    }

    [Fact]
    public void EveryUse_AFingerKeybind_MarksTheTemplateAuthored_AndUndoPutsTheMarkBack()
    {
        var h = new Harness();
        var shape = Minted(h, 0, "A5");
        var chords = h.AddChords(shape, 2);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        Assert.True(vm.SetFingerForStrings(1, [0]));

        Assert.Same(shape, h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId));
        Assert.Equal(1, shape.Fingers[0]);
        Assert.False(shape.FollowedFingering);

        h.History.Undo();
        Assert.Equal(2, shape.Fingers[0]);
        Assert.True(shape.FollowedFingering);
    }

    [Fact]
    public void ThisChord_TheTemplateTheChordMovesTo_IsAuthored_AndTheSharedOneKeepsItsMark()
    {
        var h = new Harness();
        var shape = Minted(h, 0, "A5");
        var chords = h.AddChords(shape, 3);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);

        Assert.True(vm.SetFingerForStrings(1, [0]));

        var moved = h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.NotSame(shape, moved);
        Assert.Equal(1, moved.Fingers[0]);
        Assert.False(moved.FollowedFingering);
        Assert.True(shape.FollowedFingering);
    }

    /// <summary>
    ///     The panel resolves a typed fingering to a template that already holds
    ///     it, and the provenance flag is in no such key — so the typed grip can
    ///     land on one the generator minted. It is then the charter's: that template
    ///     turns authored in the same undo node.
    /// </summary>
    [Fact]
    public void ATypedFingering_ThatLandsOnAMintedTemplate_MakesItAuthored_InTheSameUndoNode()
    {
        var h = new Harness();
        var authored = h.AddTemplate(0, "A5", 5);
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 1, [1] = 4, [2] = 4 }) authored.Fingers[s] = f;
        var minted = Minted(h, 1, "A5");
        var chords = h.AddChords(authored, 2);
        h.AddChords(minted, 1);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);
        Assert.Same(authored, vm.Template);

        Assert.True(vm.SetFingerForStrings(2, [0]));

        Assert.Equal(minted.Id, chords[0].TemplateId);
        Assert.False(minted.FollowedFingering);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal(authored.Id, chords[0].TemplateId);
        Assert.True(minted.FollowedFingering);
    }

    [Fact]
    public void ARename_LeavesTheFingeringsMarkAlone()
    {
        var h = new Harness();
        var shape = Minted(h, 0, "A5");
        var chords = h.AddChords(shape, 1);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.ChordName = "A power";

        Assert.Equal("A power", shape.Name);
        Assert.True(shape.FollowedFingering);
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
                var chord = new Chord
                {
                    TemplateId = template.Id,
                    Position = new FractionalPosition(Arrangement.Levels[0].Chords.Count * 4)
                };
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
