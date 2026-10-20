using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     A real chart editor over a 16-beat, 120 BPM grid (one arrangement, one
///     level): the <c>IChartEditorSurface</c> / <c>IEditorTransport</c> the chart
///     dialogs write through, with the real undo history and tempo service.
/// </summary>
internal sealed class ChartEditHarness
{
    public ChartEditHarness()
    {
        var logger = new FakeAppLogger();
        // Scoped like the app's, so a committed edit bumps the arrangement it reached (#720).
        History = new HistoryService(logger, new EditingScope(State));
        Tempo = new TempoService(logger);
        var config = new FakeConfigurationService();

        Project = new ChartProject { Bpm = 120 };
        for (var i = 0; i < 16; i++)
        {
            Project.Beats.Add(new BeatMarker
            { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        }

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        Project.Arrangements.Add(arrangement);

        State.ActiveProject = Project;
        State.ActiveArrangement = arrangement;
        State.ActiveLevel = arrangement.Levels[0];

        Transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly())
        {
            Bpm = 120
        };
        var selection = new SelectionService();
        Editor = new ChartEditorViewModel(State, Transport, History, selection, Tempo,
            new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = Project.Chart,
            ActiveArrangement = arrangement
        };

        Services = new ChartEditDialogServices(Editor, History, State, config, FakeLocalization.English(), logger);
    }

    public ChartProject Project { get; }

    public ProjectState State { get; } = new();

    public TransportViewModel Transport { get; }

    public ChartEditorViewModel Editor { get; }

    public HistoryService History { get; }

    public TempoService Tempo { get; }

    public ChartEditDialogServices Services { get; }

    /// <summary>The dialog's answer; a dialog that has not answered fails the test rather than hanging it.</summary>
    public static T Answer<T>(Task<T> result)
    {
        Assert.True(result.IsCompleted, "the dialog has not answered");
        return result.Result;
    }
}
