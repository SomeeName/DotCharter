using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Editing;

internal sealed class PasteReplacementHarness
{
    public PasteReplacementHarness()
    {
        var logger = Logger;
        History = new HistoryService(logger);
        Config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
        Config.AppConfig.MinNoteDistanceMs = 0;
        var project = new ChartProject();
        for (var i = 0; i < 64; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        Arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(Arrangement);
        State.ActiveProject = project;
        State.ActiveArrangement = Arrangement;
        State.ActiveLevel = Level;
        Track = new TrackEditorContext(Arrangement, Selection) { Project = project, Level = Level };
        var scope = new EditingScope(State);
        var highway = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
            Config, scope, new EditTargetService(Selection, scope, Config), logger);
        var clipboard = new ClipboardService(logger);
        var tempo = new TempoService(logger);
        Clipboard = new EditingClipboardCoordinator(clipboard, Config, highway, Selection, tempo, logger);
        var tracks = new TrackContextRegistry(State, Selection);
        Coordinator = new GlobalEditingCoordinator(State, Selection, History, clipboard, tempo,
            new BeatScaleService(logger), highway, Config, new SelectLikeService(logger),
            new FingeringGenerationService(), tracks,
            new PhraseCatalogService(Path.Combine(Path.GetTempPath(), "dotcharter-paste-replacement-empty"),
                new RscpChartSerializer(), logger), logger, FakeLocalization.English());
    }

    public FakeAppLogger Logger { get; } = new();
    public FakeConfigurationService Config { get; } = new();
    public ProjectState State { get; } = new();
    public SelectionService Selection { get; } = new();
    public HistoryService History { get; }
    public Arrangement Arrangement { get; } = new() { Name = "Lead" };
    public ChartLevel Level => Arrangement.Levels[0];
    public TrackEditorContext Track { get; }
    public EditingClipboardCoordinator Clipboard { get; }
    public GlobalEditingCoordinator Coordinator { get; }

    public void Copy() => Clipboard.Copy(Track, EditorFocusArea.NoteHighway);

    public void PasteTo(Arrangement target, string mode, int targetBeat = 0)
    {
        if (mode == "Send")
        {
            Coordinator.SendSelectionToArrangement(target);
            return;
        }

        Copy();
        Selection.ClearAll();
        State.ActiveArrangement = target;
        State.ActiveLevel = target.Levels[0];
        var track = new TrackEditorContext(target, Selection) { Project = State.ActiveProject, Level = target.Levels[0] };
        var timeMs = State.ActiveProject!.Beats[targetBeat].Time;
        switch (mode)
        {
            case "Paste": Clipboard.Paste(track, timeMs, 4); break;
            case "Mouse": Coordinator.Paste(timeMs, 4); break;
            case "SameType": Clipboard.PasteSpecial(track, timeMs, 4, PasteSpecialMode.OverwriteSameType); break;
            case "All": Clipboard.PasteSpecial(track, timeMs, 4, PasteSpecialMode.OverwriteAll); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public Arrangement AddTarget()
    {
        var target = new Arrangement { Name = "Rhythm" };
        target.Levels.Add(new ChartLevel());
        State.ActiveProject!.Arrangements.Add(target);
        return target;
    }

    public static GuitarNote Note(int beat, int str = 1, int fret = 7) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = str, Fret = fret
    };

    public static Chord Chord(int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        return chord;
    }
}
