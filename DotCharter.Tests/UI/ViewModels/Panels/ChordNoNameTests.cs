using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A chord with no name ON PURPOSE (<see cref="ChordTemplate.Unnamed" />): the chord panel's
///     No name box clears the name and puts it back, one undo node each way; the choice survives
///     a fingering edit and a save and reload, and exports as an empty name.
/// </summary>
public sealed class ChordNoNameTests : IDisposable
{
    // 3 2 0 0 0 3: G major.
    private static readonly (int String, int Fret)[] G = [(0, 3), (1, 2), (2, 0), (3, 0), (4, 0), (5, 3)];

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_chord_noname_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TickingNoName_ClearsTheName_AndUntickingPutsItBack()
    {
        var h = new Harness();
        var (vm, template) = h.Loaded(G, "Gmaj");

        vm.IsUnnamed = true;

        Assert.Equal("", template.Name);
        Assert.True(template.Unnamed);
        Assert.Equal("", vm.ChordName);

        vm.IsUnnamed = false;

        Assert.Equal("Gmaj", template.Name);
        Assert.False(template.Unnamed);
    }

    [Fact]
    public void NoName_IsOneUndoNode_AndRedoAgrees()
    {
        var h = new Harness();
        var (vm, template) = h.Loaded(G, "Gmaj");

        vm.IsUnnamed = true;
        h.History.Undo();

        Assert.Equal("Gmaj", template.Name);
        Assert.False(template.Unnamed);

        h.History.Redo();

        Assert.Equal("", template.Name);
        Assert.True(template.Unnamed);
    }

    /// <summary>A grip opened already unnamed has no name to restore: unticking suggests one.</summary>
    [Fact]
    public void UntickingAShapeThatHadNoName_SuggestsOne()
    {
        var h = new Harness();
        var (vm, template) = h.Loaded(G, "", unnamed: true);
        Assert.True(vm.IsUnnamed);

        vm.IsUnnamed = false;

        Assert.False(template.Unnamed);
        Assert.Equal("G", template.Name);
    }

    [Fact]
    public void AFingeringEdit_KeepsTheShapeUnnamed()
    {
        var h = new Harness();
        var (vm, _) = h.Loaded(G, "", unnamed: true);

        vm.StringInputs[0].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        // The grip the chord now plays, whether edited in place or minted.
        var grip = vm.Template!;
        Assert.Equal(3, grip.Fingers[0]);
        Assert.True(grip.Unnamed);
        Assert.Equal("", grip.Name);
    }

    [Fact]
    public void SettingAName_ClearsNoName_AndUndoBringsItBack()
    {
        var template = new ChordTemplate { Name = "", Unnamed = true };
        var action = new SetTemplateNameAction(template, "", "G", RecordingProxy.Create<IChartEditorSurface>().Proxy);

        action.Execute();
        Assert.Equal("G", template.Name);
        Assert.False(template.Unnamed);

        action.Undo();
        Assert.Equal("", template.Name);
        Assert.True(template.Unnamed);
    }

    [Fact]
    public void AnUnnamedShape_SurvivesASaveAndReload_AndExportsAnEmptyName()
    {
        var project = ProjectWith(new ChordTemplate { Id = 0, Name = "", Unnamed = true, Frets = Frets(G) },
            new ChordTemplate { Id = 1, Name = "C", Frets = Frets([(1, 3), (2, 2), (3, 0), (4, 1), (5, 0)]) });
        var path = Path.Combine(_dir, "project.rscp");

        new RscpChartSerializer().Save(path, project);
        var reloaded = new RscpChartSerializer().Load(path);

        var templates = reloaded.Arrangements[0].ChordTemplates;
        Assert.True(templates[0].Unnamed);
        Assert.Equal("", templates[0].Name);
        Assert.False(templates[1].Unnamed);
        var written = new RsXmlSongWriter().Write(reloaded).Descendants("chordTemplate").First();
        Assert.Equal("", written.Attribute("chordName")!.Value);
    }

    [Fact]
    public void UntickingNoName_OnOneStrum_PreservesTheOtherStrumAndUndoRedo()
    {
        var h = new Harness();
        var (vm, original) = h.Loaded([(0, 3), (1, 5)], "", unnamed: true);
        var arrangement = h.State.ActiveArrangement!;
        var level = arrangement.Levels[0];
        var selected = Assert.Single(level.Chords);
        original.Id = 7;
        selected.TemplateId = 7;
        var other = selected.Clone();
        other.Position = new FractionalPosition(2);
        level.Chords.Add(other);
        var kept = new ChordTemplate { Id = 19, Name = "kept", Frets = Frets(G) };
        arrangement.ChordTemplates.Insert(0, kept);
        var existing = new Chord { TemplateId = 19, Position = new FractionalPosition(4) };
        foreach (var (str, fret) in G)
            existing.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        level.Chords.Add(existing);
        vm.LoadTemplate(original, 6);
        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);
        var before = ChartProjectCloner.CloneArrangement(arrangement);

        vm.IsUnnamed = false;

        Assert.NotEqual(original.Id, selected.TemplateId);
        var edited = arrangement.FindChordTemplate(selected.TemplateId)!;
        Assert.False(edited.Unnamed);
        Assert.Equal("", edited.Name);
        Assert.True(original.Unnamed);
        Assert.Equal(7, other.TemplateId);
        Assert.Equal(19, existing.TemplateId);
        var after = ChartProjectCloner.CloneArrangement(arrangement);
        h.History.Undo();
        Assert.Empty(DeepCompare.Diff("Undo", before, arrangement, new HashSet<string>()));
        h.History.Redo();
        Assert.Empty(DeepCompare.Diff("Redo", after, arrangement, new HashSet<string>()));
    }

    private static Dictionary<int, int> Frets((int String, int Fret)[] grip) => grip.ToDictionary(g => g.String, g => g.Fret);

    private static ChartProject ProjectWith(params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        arrangement.Levels.Add(new ChartLevel());
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        project.Beats.Add(new BeatMarker { Time = 0, Bpm = 120 });
        project.Beats.Add(new BeatMarker { Time = 500, Bpm = 120 });
        return project;
    }

    private sealed class Harness
    {
        private readonly Arrangement _arrangement;

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            _arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
            _arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(_arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = _arrangement;
            State.ActiveLevel = _arrangement.Levels[0];
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
            Config = config;
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }
        private FakeConfigurationService Config { get; }

        public (ChordTemplateEditorViewModel Vm, ChordTemplate Template) Loaded(
            (int String, int Fret)[] grip, string name, bool unnamed = false)
        {
            var template = new ChordTemplate { Id = 0, Name = name, Unnamed = unnamed, Frets = Frets(grip) };
            _arrangement.ChordTemplates.Add(template);
            var chord = new Chord { TemplateId = 0 };
            foreach (var (stringIndex, fret) in grip)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            _arrangement.Levels[0].Chords.Add(chord);

            var vm = new ChordTemplateEditorViewModel(Editor, History, new FingeringGenerationService(),
                FakeLocalization.English(), new InlineDispatcher(), Config);
            Selection.SelectedChords.Add(chord);
            return (vm, template);
        }
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
}
