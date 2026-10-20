using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #511: the chord panel's two automatic fingerings, "Auto-fill fingerings &amp; names"
///     and the display seed of a chord that has no template yet, finger a power chord the
///     way the arrangement already does (here <c>1·4</c>, which the suggester alone never
///     gives). Driven through the real view model.
/// </summary>
public sealed class ChordTemplatePairFingeringStyleTests
{
    // A power chord on A–D at fret 7: the suggester alone fingers it 1·3.
    private static readonly (int String, int Fret)[] PowerChord = [(1, 7), (2, 9)];

    [Fact]
    public void AutoFill_FingersAPowerChordTheArrangementsWay()
    {
        var h = new Harness();
        var template = new ChordTemplate { Id = 2, Name = "A5", Frets = PowerChord.ToDictionary(f => f.String, f => f.Fret) };
        h.Arrangement.ChordTemplates.Add(template);
        var chord = h.AddChord(templateId: 2);
        var vm = h.Panel();
        h.Selection.SelectedChords.Add(chord);

        vm.GenerateAllFingeringsCommand.Execute(null);

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 4 }, template.Fingers);
    }

    /// <summary>#785: a PINNED template (authored, or holding ghost gems) is neither fingered
    /// nor named by Auto-fill; <see cref="AutoFill_FingersAPowerChordTheArrangementsWay" /> is
    /// the same setup unpinned, where it is.</summary>
    [Fact]
    public void AutoFill_LeavesAPinnedTemplateAsItIs()
    {
        var h = new Harness();
        var template = new ChordTemplate { Id = 2, Name = "", Pinned = true, Frets = PowerChord.ToDictionary(f => f.String, f => f.Fret) };
        h.Arrangement.ChordTemplates.Add(template);
        var chord = h.AddChord(templateId: 2);
        var vm = h.Panel();
        h.Selection.SelectedChords.Add(chord);

        vm.GenerateAllFingeringsCommand.Execute(null);

        Assert.Empty(template.Fingers);
        Assert.Equal("", template.Name);
    }

    [Fact]
    public void TheSeedOfAnUntemplatedChord_FollowsTheArrangement()
    {
        var h = new Harness();
        var chord = h.AddChord(templateId: -1);
        var vm = h.Panel();

        h.Selection.SelectedChords.Add(chord);

        Assert.NotNull(vm.Template);
        Assert.Equal(-1, vm.Template!.Id);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 4 }, vm.Template.Fingers);
    }

    private sealed class Harness
    {
        private readonly FakeConfigurationService _config = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var project = new ChartProject();
            Arrangement = new Arrangement
            {
                Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6,
                Tuning = ["E", "A", "D", "G", "B", "E"]
            };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            // The song plays its power chords 1·4: two templates say so.
            Arrangement.ChordTemplates.Add(new ChordTemplate
            {
                Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5 }, Fingers = { [0] = 1, [1] = 4 }
            });
            Arrangement.ChordTemplates.Add(new ChordTemplate
            {
                Id = 1, Name = "A5", Frets = { [0] = 5, [1] = 7 }, Fingers = { [0] = 1, [1] = 4 }
            });

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var transport = new TransportViewModel(Audio, State, _config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; }
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public Chord AddChord(int templateId)
        {
            var chord = new Chord { TemplateId = templateId };
            foreach (var (stringIndex, fret) in PowerChord)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Arrangement.Levels[0].Chords.Add(chord);
            return chord;
        }

        public ChordTemplateEditorViewModel Panel() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), _config);

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
