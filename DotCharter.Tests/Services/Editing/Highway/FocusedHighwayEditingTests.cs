using System.Xml.Linq;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class FocusedHighwayEditingTests
{
    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(5, 0, 3)]
    public void PlacementUsesTheFocusedArrangementsCapo(int activeCapo, int focusedCapo, int expected)
    {
        var h = new ChordKeyboardMutationTests.Harness();
        using var service = h.Service;
        h.Arrangement.Capo = activeCapo;
        var bass = new Arrangement { Capo = focusedCapo, Tuning = ["E", "A", "D", "G"] };
        var level = new ChartLevel();
        bass.Levels.Add(level);
        h.State.ActiveProject!.Arrangements.Add(bass);
        h.Scope.SetResolver(() => new EditingTarget(bass, level));
        h.State.PreviewNoteFret = 3;
        var before = ChartBytes(h.State.ActiveProject);

        h.Service.ApplyPlacement(new FractionalPosition(2), 0);

        Assert.Equal(expected, Assert.Single(level.Notes).Fret);
        Assert.Empty(h.Arrangement.Levels[0].Notes);
        var after = ChartBytes(h.State.ActiveProject);
        h.History.Undo();
        Assert.Empty(level.Notes);
        Assert.Equal(before, ChartBytes(h.State.ActiveProject));
        h.History.Redo();
        Assert.Equal(expected, Assert.Single(level.Notes).Fret);
        Assert.Equal(after, ChartBytes(h.State.ActiveProject));
    }

    [Fact]
    public void AFretKeyAfterFocusingAnEmptyTrackDoesNotEditThePreviousTrack()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        using var service = h.Service;
        using var tracks = new TrackContextRegistry(h.State, h.Selection);
        var bass = new Arrangement { Tuning = ["E", "A", "D", "G"] };
        bass.Levels.Add(new ChartLevel());
        h.State.ActiveProject!.Arrangements.Add(bass);
        h.Scope.SetResolver(() => tracks.FocusedTrack is { Level: { } level } focused
            ? new EditingTarget(focused.Arrangement, level)
            : null);
        var leadNote = h.AddNote(2, 0, 7);
        h.Selection.SelectedNotes.Add(leadNote);

        tracks.Focus(bass);
        h.Service.SetSelectedFret(3);

        Assert.Equal(7, leadNote.Fret);
        Assert.Empty(bass.Levels[0].Notes);
        Assert.False(h.History.CanUndo);
    }

    internal static byte[] ChartBytes(ChartProject project)
    {
        var document = new XDocument();
        new RscpWriter().WriteDocument(document, project);
        using var stream = new MemoryStream();
        RscpChartSerializer.WriteDocument(stream, document);
        return stream.ToArray();
    }
}
