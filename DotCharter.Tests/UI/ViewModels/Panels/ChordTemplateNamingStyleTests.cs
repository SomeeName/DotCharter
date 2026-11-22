using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>Chord offers and auto-fill share the selected naming style and key;
/// tuning spelling is the fallback without a resolved naming key.</summary>
public sealed class ChordTemplateNamingStyleTests
{
    // x,1,3,1,2,x — Bbm7 at sounding pitch in standard tuning.
    private static readonly (int String, int Fret)[] Bbm7 = [(1, 1), (2, 3), (3, 1), (4, 2)];

    [Theory]
    [InlineData(ChordNamingStyle.Eof, "A#min7")]
    [InlineData(ChordNamingStyle.Musical, "Bbm7")]
    public void TheSuggestPills_FollowTheStyle(ChordNamingStyle style, string expectedFirst)
    {
        var h = new Harness(style, keyAccidentals: -5);
        var (vm, template) = h.LoadedShape(Bbm7, name: "Grip");

        vm.LoadTemplate(template, 6);

        Assert.Equal(expectedFirst, vm.Names.SuggestedChordNames[0]);
    }

    [Theory]
    [InlineData(ChordNamingStyle.Eof, "A#min7")]
    [InlineData(ChordNamingStyle.Musical, "Bbm7")]
    public void AutoFill_NamesEmptyTemplatesInTheStyle(ChordNamingStyle style, string expected)
    {
        var h = new Harness(style, keyAccidentals: -5);
        var (vm, template) = h.LoadedShape(Bbm7, name: "");

        vm.GenerateAllFingeringsCommand.Execute(null);

        Assert.Equal(expected, template.Name);
    }

    [Theory]
    [InlineData(ChordNamingStyle.Eof, 0)]
    [InlineData(ChordNamingStyle.Musical, -5)]
    [InlineData(ChordNamingStyle.Musical, 4)]
    public void PillAndAutoFill_Agree_WithoutEnharmonicDuplicates(ChordNamingStyle style, int keyAccidentals)
    {
        var h = new Harness(style, keyAccidentals);
        var (vm, template) = h.LoadedShape(Bbm7, name: "");
        var pill = vm.Names.SuggestedChordNames[0];
        var identities = vm.Names.SuggestedChordNames.Select(name => TuningDisplayNames.ChordSuggestion(name, h.Editor.ActiveArrangement!));
        Assert.Equal(vm.Names.SuggestedChordNames.Count, identities.Distinct(StringComparer.Ordinal).Count());
        vm.GenerateAllFingeringsCommand.Execute(null);
        Assert.Equal(template.Name, pill);
    }

    [Fact]
    public void SharpProjectKey_WinsOverFlatTuning_AndMatchesAutoFill()
    {
        var h = new Harness(ChordNamingStyle.Musical, keyAccidentals: 4);
        var arrangement = h.Editor.ActiveArrangement!;
        arrangement.Tuning = TuningUtilities.GetTuningNames(6, [-1, -1, -1, -1, -1, -1]);
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        var (vm, template) = h.LoadedShape([(0, 5), (1, 7), (2, 7), (3, 6), (4, 5), (5, 5)], name: "");
        Assert.Equal("G#", vm.Names.SuggestedChordNames[0]);
        Assert.DoesNotContain("Ab", vm.Names.SuggestedChordNames);
        vm.GenerateAllFingeringsCommand.Execute(null);
        Assert.Equal(template.Name, vm.Names.SuggestedChordNames[0]);
    }

    [Fact]
    public void NoKey_FallsBackToTuning_AndMatchesAutoFill()
    {
        var h = new Harness(ChordNamingStyle.Eof, keyAccidentals: 0);
        var arrangement = h.Editor.ActiveArrangement!;
        arrangement.Tuning = TuningUtilities.GetTuningNames(6, [-1, -1, -1, -1, -1, -1]);
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        var (vm, template) = h.LoadedShape([(0, 5), (1, 7), (2, 7), (3, 6), (4, 5), (5, 5)], name: "");
        Assert.Equal(new[] { "Ab" }, vm.Names.SuggestedChordNames);
        vm.GenerateAllFingeringsCommand.Execute(null);
        Assert.Equal("Ab", template.Name);
    }

    [Fact]
    public void ClearingNoName_RestoresTheSameNameThePillOffers()
    {
        var h = new Harness(ChordNamingStyle.Musical, keyAccidentals: -5);
        var (vm, template) = h.LoadedShape(Bbm7, name: "");
        template.Unnamed = true;
        vm.LoadTemplate(template, 6);
        var suggested = vm.Names.SuggestedChordNames[0];

        vm.IsUnnamed = false;

        Assert.False(template.Unnamed);
        Assert.Equal(suggested, template.Name);
    }

    private sealed class Harness
    {
        private readonly Arrangement _arr;
        private readonly FakeConfigurationService _config = new();

        public Harness(ChordNamingStyle style, int keyAccidentals)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            _config.AppConfig.ChordNamingStyle = style;
            var project = new ChartProject();
            project.Chart.KeyAccidentals = keyAccidentals;
            _arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            _arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(_arr);

            State.ActiveProject = project;
            State.ActiveArrangement = _arr;
            State.ActiveLevel = _arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, _config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public (ChordTemplateEditorViewModel Vm, ChordTemplate Template) LoadedShape(
            (int String, int Fret)[] frets, string name)
        {
            var template = new ChordTemplate
            {
                Id = 0, Name = name,
                Frets = frets.ToDictionary(f => f.String, f => f.Fret)
            };
            _arr.ChordTemplates.Add(template);

            var chord = new Chord { TemplateId = 0 };
            foreach (var (stringIdx, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
            _arr.Levels[0].Chords.Add(chord);

            var vm = new ChordTemplateEditorViewModel(Editor, History, new FingeringGenerationService(), FakeLocalization.English(),
                new InlineDispatcher(), _config);
            Selection.SelectedChords.Add(chord);
            return (vm, template);
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
}
