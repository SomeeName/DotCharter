using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>Inspector refreshes driven through the real session and its history notifications.</summary>
[Collection("HeadlessUI")]
public sealed class InspectorStaleStateUiPerfSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheInspector_ForwardsChordAndMixedSelectionsToTheTechniqueCards(bool mixed) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var state = services.GetRequiredService<ProjectState>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var editor = services.GetRequiredService<ChartEditorViewModel>();
            var arrangement = ArrangementWithPhrase("verse");
            var project = new ChartProject();
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            editor.Selection.ClearSelections();
            for (var i = 0; i < 2; i++)
            {
                var chord = new Chord { Position = new FractionalPosition(i) };
                chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
                arrangement.Levels[0].Chords.Add(chord);
                editor.Selection.SelectedChords.Add(chord);
            }
            if (mixed)
            {
                var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 };
                arrangement.Levels[0].Notes.Add(note);
                editor.Selection.SelectedNotes.Add(note);
            }
            vm.Sections.ChooseCommand.Execute(SoundSection.Technique);
            Assert.True(vm.Sections.HasChords);
            Assert.True(vm.Sections.ShowChordTechnique);
            Assert.Equal(mixed, vm.Sections.ShowNoteTechnique);
        }, CancellationToken.None);

    [Fact]
    public async Task TheInspectorsHistoryResync_ReReadsTheHandShapeCards() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var state = services.GetRequiredService<ProjectState>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var editor = services.GetRequiredService<ChartEditorViewModel>();
            var history = services.GetRequiredService<IHistoryService>();
            var arrangement = ArrangementWithPhrase("verse");
            var shape = new HandShape { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(5) };
            arrangement.Levels[0].HandShapes.Add(shape);
            var project = new ChartProject();
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            editor.Selection.ClearSelections();
            editor.Selection.SelectedHandShapes.Add(shape);
            arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
            vm.HandShapeEditor.SyncHandShapeProperties();
            Assert.Equal(1, vm.HandShapeEditor.HandShapeEnclosedCount);
            history.Do(new SetHandShapeSpanAction(arrangement.Levels[0].HandShapes, shape, shape.Position, shape.EndPosition,
                new FractionalPosition(3), shape.EndPosition));
            Assert.Equal(0, vm.HandShapeEditor.HandShapeEnclosedCount);
            history.Undo();
            Assert.Equal(1, vm.HandShapeEditor.HandShapeEnclosedCount);
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnArrangementOrProjectSwitch_MakesTheOpenSongTabsPhrasesCurrent(bool projectSwitch) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var state = services.GetRequiredService<ProjectState>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var first = ArrangementWithPhrase("verse");
            var next = ArrangementWithPhrase("chorus");
            var project = new ChartProject();
            project.Arrangements.Add(first);
            project.Arrangements.Add(next);
            state.ActiveProject = project;
            state.ActiveArrangement = first;
            vm.SelectedTabIndex = InspectorViewModel.SongTabIndex;
            Assert.Equal("verse", Assert.Single(vm.Phrases.Phrases).Name);
            if (projectSwitch)
            {
                var other = new ChartProject();
                other.Arrangements.Add(next);
                state.ActiveProject = other;
            }
            state.ActiveArrangement = next;
            Assert.Equal("chorus", Assert.Single(vm.Phrases.Phrases).Name);
        }, CancellationToken.None);

    [Fact]
    public async Task AProjectSwitch_ReannouncesTheBeatMapLock() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var state = services.GetRequiredService<ProjectState>();
            var vm = services.GetRequiredService<MainWindowViewModel>();
            state.ActiveProject = new ChartProject { IsBeatMapLocked = false };
            var raised = new List<string?>();
            System.ComponentModel.PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
            vm.PropertyChanged += handler;
            try
            {
                state.ActiveProject = new ChartProject { IsBeatMapLocked = true };
                Assert.True(vm.IsBeatMapLocked);
                Assert.Contains(nameof(MainWindowViewModel.IsBeatMapLocked), raised);
            }
            finally { vm.PropertyChanged -= handler; }
        }, CancellationToken.None);

    private static Arrangement ArrangementWithPhrase(string name)
    {
        var arrangement = new Arrangement { Name = name };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Phrases.Add(name);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        return arrangement;
    }
}
