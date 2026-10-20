using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     TWO flags spell arpeggio and each import path sets only one:
///     the package importer sets <c>Arpeggio</c> (psarc), <c>RsXmlSongReader</c>
///     sets <c>ForceArpeggioInRs</c> (a <c>-arp</c> displayName — every
///     DLC-Builder / EOF chart). <c>ChordTemplate.IsArpeggio</c> is the ONE
///     answer to "is this an arpeggio?" (editing-invariants.md), and
///     <c>ArpeggioFlavourTests.NoEditingCode_AsksAboutOneArpeggioFlag</c> is
///     scoped to <c>Services/Editing</c> — so the inspector panel, which lives in
///     <c>DotCharter.UI</c>, was outside it and read one flag everywhere.
/// </summary>
public sealed class ChordTemplateArpeggioFlagTests
{
    [Fact]
    public void AnRsXmlArpeggio_ShowsTheCheckboxTicked()
    {
        var h = new Harness();
        h.Editor.ActiveArrangement!.ChordTemplates.Add(RsXmlArpeggio());

        var vm = h.CreateChordEditor();

        Assert.True(vm.IsArpeggio);
    }

    /// <summary>
    ///     Un-ticking has to clear BOTH spellings. Clearing the SNG flag alone
    ///     left <c>ForceArpeggioInRs</c> standing, so the template was still an
    ///     arpeggio to <c>IsArpeggio</c>, to the find-or-create key and to the
    ///     RS-XML writer's <c>-arp</c> suffix — while the box said it was not.
    /// </summary>
    [Fact]
    public void UntickingAnRsXmlArpeggio_ClearsBothFlags()
    {
        var h = new Harness();
        var template = RsXmlArpeggio();
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        vm.IsArpeggio = false;

        Assert.False(template.Arpeggio);
        Assert.False(template.ForceArpeggioInRs);
        Assert.False(template.IsArpeggio);
    }

    /// <summary>Ticking writes the canonical flag and leaves the RS-specific one
    /// exactly as it found it, so a template that already exports under `-arp`
    /// keeps doing so.</summary>
    [Fact]
    public void TickingAPlainShape_SetsOnlyTheCanonicalFlag()
    {
        var h = new Harness();
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        vm.IsArpeggio = true;

        Assert.True(template.Arpeggio);
        Assert.False(template.ForceArpeggioInRs);
    }

    /// <summary>The undo node carries both flags too — it only ever copied the
    /// SNG one, so a changed <c>ForceArpeggioInRs</c> could neither be applied
    /// nor put back.</summary>
    [Fact]
    public void UndoingTheUntick_RestoresTheRsSpelling()
    {
        var h = new Harness();
        var template = RsXmlArpeggio();
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        vm.IsArpeggio = false;
        Assert.True(h.History.CanUndo);
        h.History.Undo();

        Assert.True(template.ForceArpeggioInRs);
        Assert.True(template.IsArpeggio);
    }

    private static ChordTemplate RsXmlArpeggio() => new()
    {
        Id = 0,
        Name = "Am",
        // Exactly what RsXmlSongReader produces for `displayName="Am-arp"`.
        Arpeggio = false,
        ForceArpeggioInRs = true,
        Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } },
        Fingers = new Dictionary<int, int> { { 4, 1 }, { 5, 1 } }
    };

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
