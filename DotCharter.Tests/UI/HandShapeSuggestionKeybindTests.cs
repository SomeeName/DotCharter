using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     The two accept verbs of the suggested hand shapes have keys (#639): <c>Ctrl+Shift+U</c> accepts
///     the suggestions holding the selection, <c>Shift+Alt+U</c> accepts them as arpeggios — the Alt
///     row of the same letter, as Generate arpeggio is of Generate hand shape. Driven through the REAL
///     registrar, keybind handler and editing coordinator: the key press is ONE undo step, and the
///     Undo takes the bar and its minted template back. The collision check is
///     <see cref="KeybindDefaultsTests.NoTwoDefaultChordsCollide" />.
/// </summary>
public sealed class HandShapeSuggestionKeybindTests
{
    private static readonly (int String, int Fret)[] COpenArpeggio =
        [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1), (3, 0), (2, 2)];

    private static readonly (int String, int Fret)[] AmAt5 = [(1, 7), (2, 7), (3, 5), (4, 5)];

    [Fact]
    public void TheDefaults_AreCtrlShiftU_AndItsAltRow()
    {
        var config = new KeybindConfig();

        Assert.Equal(new KeyChord(Key.U, KeyModifiers.Control | KeyModifiers.Shift), config.AcceptSuggestedHandShapes);
        Assert.Equal(new KeyChord(Key.U, KeyModifiers.Shift | KeyModifiers.Alt), config.AcceptSuggestedArpeggios);
    }

    [Fact]
    public void CtrlShiftU_AcceptsTheSuggestionHoldingTheSelection_AsOneUndoStep()
    {
        var h = new Harness();
        h.Selection.SelectedNotes.Add(h.Level.Notes[9]); // inside the Am run, not the C one

        Assert.Equal(nameof(KeybindConfig.AcceptSuggestedHandShapes),
            h.Keys.DescribeBinding(h.State.Keybinds.AcceptSuggestedHandShapes));
        Assert.True(h.Keys.HandleKeyPress(Key.U, ctrl: true, shift: true, alt: false));

        var bar = Assert.Single(h.Level.HandShapes);
        Assert.Equal(h.Level.Notes[8].Position, bar.Position);
        var template = h.Arrangement.ChordTemplates.Single(t => t.Id == bar.TemplateId);
        Assert.False(template.IsArpeggio);

        h.History.Undo();

        Assert.Empty(h.Level.HandShapes);
        Assert.Empty(h.Arrangement.ChordTemplates);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ShiftAltU_AcceptsItAsAnArpeggio_AsOneUndoStep()
    {
        var h = new Harness();
        h.Selection.SelectedNotes.Add(h.Level.Notes[2]); // inside the C run

        Assert.Equal(nameof(KeybindConfig.AcceptSuggestedArpeggios),
            h.Keys.DescribeBinding(h.State.Keybinds.AcceptSuggestedArpeggios));
        Assert.True(h.Keys.HandleKeyPress(Key.U, ctrl: false, shift: true, alt: true));

        var bar = Assert.Single(h.Level.HandShapes);
        Assert.Equal(h.Level.Notes[0].Position, bar.Position);
        Assert.True(h.Arrangement.ChordTemplates.Single(t => t.Id == bar.TemplateId).IsArpeggio);

        h.History.Undo();

        Assert.Empty(h.Level.HandShapes);
        Assert.Empty(h.Arrangement.ChordTemplates);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void WithNothingSelected_TheKeyAcceptsNothing_AndPushesNoStep()
    {
        var h = new Harness();

        Assert.True(h.Keys.HandleKeyPress(Key.U, ctrl: true, shift: true, alt: false));

        Assert.Empty(h.Level.HandShapes);
        Assert.False(h.History.CanUndo);
        Assert.Equal(2, HandShapeSuggester.Suggest(h.Arrangement, h.Level).Count);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService { Keybinds = new KeybindConfig() };
            State.Keybinds = new KeybindConfig();
            var scope = new EditingScope(State);
            var highway = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 32; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);
            Level = Arrangement.Levels[0];
            Pick(Level, 0, COpenArpeggio);
            Pick(Level, 6, AmAt5);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Level;

            var tracks = new TrackContextRegistry(State, Selection);
            var catalogDir = Path.Combine(Path.GetTempPath(), "dotcharter-suggestion-keybind-tests-no-such-dir");
            var coordinator = new GlobalEditingCoordinator(State, Selection, History,
                new ClipboardService(logger), new TempoService(logger), new BeatScaleService(logger),
                highway, config, new SelectLikeService(logger), new FingeringGenerationService(),
                tracks, new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger), logger,
                FakeLocalization.English());

            Keys = new ChartKeybindHandler(() => false, logger);
            // Registration only captures its collaborators; these keys reach the coordinator alone.
            var registrar = new AppKeybindRegistrar(Keys, State, null!, null!, coordinator, null!, null!,
                config, null!, null!, null!, null!, null!, null!, new MatchPatternStore(config));
            registrar.RegisterAll();
            Keys.RefreshBinds();
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public Arrangement Arrangement { get; }
        public ChartLevel Level { get; }
        public ChartKeybindHandler Keys { get; }

        private static void Pick(ChartLevel level, double startBeat, (int String, int Fret)[] notes)
        {
            for (var i = 0; i < notes.Length; i++)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = ChordHandShapePlannerTests.Pos(startBeat + i * 0.5),
                    StringIndex = notes[i].String,
                    Fret = notes[i].Fret
                });
            }
        }
    }
}
