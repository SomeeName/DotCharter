using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     <see cref="NewChordNames" /> is process-wide, so the tests that turn it off run alone:
///     a parallel test minting a chord would otherwise see the switch mid-flip.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChordAutoNamingSerializedCollection
{
    public const string Name = "ChordAutoNamingSerialized";
}

/// <summary>
///     "Name new chords automatically" (Settings ▸ Editor, <c>AppConfig.AutoNameNewChords</c>):
///     off, every path that MAKES a chord template leaves its name empty; on, each names it as
///     before. A name a source carries is kept either way, and a template the charter marked
///     No name is never filled. The switch is wired the way the app wires it — a read of the
///     config — so flipping the setting is what flips the behaviour.
/// </summary>
[Collection(ChordAutoNamingSerializedCollection.Name)]
public sealed class ChordAutoNamingTests : IDisposable
{
    // x 0 2 2 1 0: A minor, which the suggester always names.
    private static readonly (int String, int Fret)[] Am = [(1, 0), (2, 2), (3, 2), (4, 1), (5, 0)];

    private readonly FakeConfigurationService _config = new();

    public ChordAutoNamingTests() => NewChordNames.Configure(() => _config.AppConfig.AutoNameNewChords);

    public void Dispose() => NewChordNames.Configure(static () => true);

    private static Dictionary<int, int> Frets((int String, int Fret)[] grip) => grip.ToDictionary(g => g.String, g => g.Fret);

    private static Arrangement Guitar()
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static Chord ChordOf((int String, int Fret)[] grip, int templateId = -1, int beat = 0)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1) };
        foreach (var (stringIndex, fret) in grip)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret, Sustain = new FractionalPosition(0, new Fraction(1, 1)) });
        return chord;
    }

    /// <summary>Each creation path, run once; returns the name the template it made got.</summary>
    public static TheoryData<string> Paths => ["resolver", "guitarPro", "reshape", "generateFingerings", "panelSeed"];

    private string MakeAChordThrough(string path)
    {
        switch (path)
        {
            case "resolver":
            {
                var created = new List<ChordTemplate>();
                return ChordTemplateResolver.Resolve(Guitar(), Frets(Am), ArpeggioFlavour.Plain, null, created).Name;
            }
            case "guitarPro":
            {
                var arrangement = Guitar();
                arrangement.Levels[0].Chords.Add(ChordOf(Am));
                ChordHandShapeBuilder.BuildAndTrim(arrangement.Levels[0], arrangement);
                return arrangement.ChordTemplates.Single().Name;
            }
            case "reshape":
            {
                var h = new Harness(_config);
                // One fret off A minor; moving it back reshapes the chord onto a new template.
                var chord = ChordOf([(1, 0), (2, 2), (3, 2), (4, 1), (5, 1)]);
                h.Arrangement.Levels[0].Chords.Add(chord);
                var vm = h.ChordEditor();
                h.Selection.SelectedChords.Add(chord);
                vm.StringInputs[5].FretText = "0";
                return h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId).Name;
            }
            case "generateFingerings":
            {
                var h = new Harness(_config);
                var chord = ChordOf(Am);
                h.Arrangement.Levels[0].Chords.Add(chord);
                var vm = h.ChordEditor();
                vm.GenerateAllFingeringsCommand.Execute(null);
                return h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId).Name;
            }
            case "panelSeed":
            {
                var h = new Harness(_config);
                var chord = ChordOf(Am);
                h.Arrangement.Levels[0].Chords.Add(chord);
                var vm = h.ChordEditor();
                h.Selection.SelectedChords.Add(chord);
                return vm.ChordName;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void WithTheSettingOff_EveryCreationPath_LeavesTheNameEmpty(string path)
    {
        _config.AppConfig.AutoNameNewChords = false;

        Assert.Equal(string.Empty, MakeAChordThrough(path));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void WithTheSettingOn_EveryCreationPath_NamesTheChord(string path)
    {
        _config.AppConfig.AutoNameNewChords = true;

        Assert.Equal("Amin", MakeAChordThrough(path));
    }

    /// <summary>A name the Guitar Pro file carries is the arranger's, kept with naming off.</summary>
    [Fact]
    public void WithTheSettingOff_AGuitarProChordKeepsTheNameItsFileCarries()
    {
        _config.AppConfig.AutoNameNewChords = false;
        var arrangement = Guitar();
        arrangement.Levels[0].Chords.Add(ChordOf(Am));
        var names = new GpAuthoredChordNames();
        names.Set(new FractionalPosition(0), "Am/C");

        ChordHandShapeBuilder.BuildAndTrim(arrangement.Levels[0], arrangement, authoredChordNames: names);

        Assert.Equal("Am/C", arrangement.ChordTemplates.Single().Name);
    }

    /// <summary>A paste's source name is the charter's, kept with naming off.</summary>
    [Fact]
    public void WithTheSettingOff_APreferredNameIsKept()
    {
        _config.AppConfig.AutoNameNewChords = false;

        var template = ChordTemplateResolver.Resolve(Guitar(), Frets(Am), ArpeggioFlavour.Plain, "Dmin", []);

        Assert.Equal("Dmin", template.Name);
    }

    /// <summary>Generate fingerings names every EMPTY name — except one the charter left out on purpose.</summary>
    [Fact]
    public void GenerateFingerings_NeverFillsATemplateMarkedNoName()
    {
        var h = new Harness(_config);
        var unnamed = new ChordTemplate { Id = 0, Name = "", Unnamed = true, Frets = Frets(Am) };
        var empty = new ChordTemplate { Id = 1, Name = "", Frets = Frets([(0, 3), (1, 2), (2, 0), (3, 0), (4, 0), (5, 3)]) };
        h.Arrangement.ChordTemplates.Add(unnamed);
        h.Arrangement.ChordTemplates.Add(empty);
        h.Arrangement.Levels[0].Chords.Add(ChordOf(Am, templateId: 0));

        h.ChordEditor().GenerateAllFingeringsCommand.Execute(null);

        Assert.Equal("", unnamed.Name);
        Assert.True(unnamed.Unnamed);
        Assert.NotEqual("", empty.Name);
    }

    /// <summary>The app wires the switch to the setting; a setting nothing reads is a dead feature.</summary>
    [Fact]
    public void TheAppWiresTheSwitchToTheSetting()
    {
        var ui = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");

        Assert.Matches(@"NewChordNames\.Configure\(\(\) => \w+\.LoadAppConfig\(\)\.AutoNameNewChords\)",
            File.ReadAllText(Path.Combine(ui, "App.Statics.cs")));
        Assert.Contains("AppStatics.Wire(Services);", File.ReadAllText(Path.Combine(ui, "App.axaml.cs")));
    }

    private sealed class Harness
    {
        private readonly FakeConfigurationService _config;

        public Harness(FakeConfigurationService config)
        {
            _config = config;
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            Arrangement = Guitar();
            project.Arrangements.Add(Arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; }
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel ChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), _config);
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
