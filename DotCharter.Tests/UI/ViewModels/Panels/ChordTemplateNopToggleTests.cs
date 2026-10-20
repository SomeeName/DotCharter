using System.Globalization;
using System.Resources;
using DotCharter.Localization;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     Rocksmith's Nop chord flag is a named toggle on the chord template panel
///     (#489) instead of a "-nop" typed into the name. It is an edit like every
///     other on the panel: it reaches what the "Apply to" ladder shows and says so
///     on the status rail.
/// </summary>
public sealed class ChordTemplateNopToggleTests
{
    [Fact]
    public void TheToggle_ShowsTheLoadedTemplatesFlag()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        shape.NopInRs = true;
        h.AddChords(shape, 1);

        var vm = h.CreateChordEditor();

        Assert.Same(shape, vm.Template);
        Assert.True(vm.IsNop);
    }

    /// <summary>
    ///     "This chord" on a shared shape REBINDS the one chord onto a template that
    ///     carries the flag and leaves the shape every other chord uses alone. The
    ///     panel's "identical template" key has to see the flag for this: without it
    ///     the lookup returned the very template the edit came from and the toggle
    ///     was silently dropped.
    /// </summary>
    [Fact]
    public void ThisChord_RebindsOneChordOntoANopTemplate_AndSaysSo()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(shape, 3);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);

        vm.IsNop = true;

        Assert.False(shape.NopInRs);
        Assert.NotEqual(shape.Id, chords[0].TemplateId);
        var minted = h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.True(minted.NopInRs);
        Assert.Equal("A5", minted.Name);
        Assert.Equal(shape.Id, chords[1].TemplateId);
        Assert.Equal(shape.Id, chords[2].TemplateId);
        Assert.Equal(Applied(chords: 1, shapes: 1), h.State.MainDisplayMessage);
    }

    /// <summary>"Every use" rewrites the shape in place, keeps its name, and one
    /// undo puts the flag back.</summary>
    [Fact]
    public void EveryUse_SetsTheFieldInPlace_AndUndoesIt()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(shape, 2);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.IsNop = true;

        Assert.True(shape.NopInRs);
        Assert.Equal("A5", shape.Name);
        Assert.All(chords, c => Assert.Equal(shape.Id, c.TemplateId));

        h.History.Undo();
        Assert.False(shape.NopInRs);
    }

    /// <summary>A "-nop" typed into the name is the toggle's flag, not name text.</summary>
    [Fact]
    public void TypingTheSuffix_SetsTheToggle_AndLeavesTheNameClean()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", 5);
        var chords = h.AddChords(shape, 1);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.ChordName = "A5-nop";

        Assert.Equal("A5", shape.Name);
        Assert.True(shape.NopInRs);
        Assert.True(vm.IsNop);
        Assert.Equal("A5", vm.ChordName);
    }

    /// <summary>The status rail names an edit by <c>ChordTemplate_Applied_{kind}</c>,
    /// a key built at run time that no static lint sees: every kind has one.</summary>
    [Fact]
    public void EveryEditKind_HasItsStatusRailWord()
    {
        var resources = new ResourceManager("DotCharter.Localization.Resources", typeof(LocalizationService).Assembly);
        foreach (var kind in Enum.GetValues<ChordTemplateEditKind>())
        {
            var key = $"ChordTemplate_Applied_{kind}";
            Assert.False(string.IsNullOrEmpty(resources.GetString(key, CultureInfo.InvariantCulture)), $"{key} is missing");
        }
    }

    [Fact]
    public void ANopOnlyChange_IsItsOwnEditKind()
    {
        var before = new ChordTemplate { Name = "A5", Frets = { [0] = 5 } };
        var after = before.Clone();
        after.NopInRs = true;

        Assert.Equal(ChordTemplateEditKind.Nop, ChordTemplateEditPlanner.KindOf(before, after));
    }

    private static string Applied(int chords, int shapes)
    {
        var loc = FakeLocalization.English();
        return loc.Format("ChordTemplate_Applied", loc["ChordTemplate_Applied_Nop"],
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
