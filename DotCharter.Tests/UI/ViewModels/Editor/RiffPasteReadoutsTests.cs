using System.Globalization;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Converters;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Riff library's live readouts (inspector design D, #751 P7b): "Pastes at" is the playhead snapped to the
///     beat grid exactly as the paste snaps it, re-read once per posted pass however fast the playhead moves;
///     "Selection" counts the selected notes and chords; each row stamps its length in beats.
/// </summary>
public sealed class RiffPasteReadoutsTests
{
    [Fact]
    public void PastesAt_IsThePlayheadSnappedToTheBeatGrid_ReadOncePerPostedPass()
    {
        var (readouts, state, transport, _, posted) = Build();
        transport.GridDivisor = 1; // whole beats
        posted.ForEach(run => run());
        posted.Clear();
        var reads = readouts.PastesAtReads;

        // a burst of playhead moves (250 Hz) asks for ONE read
        for (var ms = 4_100; ms < 4_200; ms += 4) state.CurrentTimeMs = ms;
        Assert.Single(posted);
        posted[0]();

        Assert.Equal(reads + 1, readouts.PastesAtReads);
        // beat 8 (4.0 s) is the grid point the paste lands on: bar 3 in 4/4
        Assert.Equal(FakeLocalization.English().Format("PhraseCatalog_PastesAtFmt", 3, ElapsedTimeLabel.Format(4_000, 1)),
            readouts.PastesAt);
    }

    [Fact]
    public void Selection_CountsTheSelectedNotesAndChords()
    {
        var (readouts, _, _, selection, _) = Build();
        Assert.Equal("Selection: 0 sounds", readouts.SelectionText);

        selection.SelectedNotes.Add(new GuitarNote());
        selection.SelectedChords.Add(new Chord());

        Assert.Equal("Selection: 2 sounds", readouts.SelectionText);
    }

    [Theory]
    [InlineData(1.0, "1 beat")]
    [InlineData(4.0, "4 beats")]
    [InlineData(2.5, "2.5 beats")]
    public void ARowStampsItsLengthInBeats(double beats, string expected) =>
        Assert.Equal(expected, RiffLengthConverter.Format(beats, FakeLocalization.English(), CultureInfo.InvariantCulture));

    private static (RiffPasteReadouts Readouts, ProjectState State, TransportViewModel Transport, SelectionService Selection,
        List<Action> Posted) Build()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var project = new ChartProject();
        for (var i = 0; i < 40; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        project.Arrangements.Add(new Arrangement { Name = "Lead" });
        state.ActiveProject = project;
        var selection = new SelectionService();
        var history = new HistoryService(logger);
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart
        };
        var posted = new List<Action>();
        var readouts = new RiffPasteReadouts(state, editor, transport, FakeLocalization.English(), posted.Add);
        return (readouts, state, transport, selection, posted);
    }
}
