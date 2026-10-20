namespace DotCharter.Tests.Services.State;

public class ChartDocumentStateTests
{
    [Fact]
    public void Beats_WithoutProject_ReturnsEmptyPerInstance()
    {
        var a = new ChartDocumentState();
        var b = new ChartDocumentState();

        Assert.Empty(a.Beats);
        Assert.Empty(b.Beats);

        // Per-instance: mutating one fallback must not leak to another instance.
        a.Beats.Add(new BeatMarker { Time = 1f });
        Assert.Empty(b.Beats);
    }

    [Fact]
    public void PlacedNotes_WithoutActiveLevel_IsEmpty()
    {
        var state = new ChartDocumentState();
        Assert.Empty(state.PlacedNotes);
    }

    [Fact]
    public void PlacedNotes_WithActiveLevel_ReturnsLevelNotes()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Fret = 3 });
        var state = new ChartDocumentState { ActiveLevel = level };

        Assert.Single(state.PlacedNotes);
        Assert.Equal(3, state.PlacedNotes[0].Fret);
    }

    [Fact]
    public void EventPoints_DelegateToArrangement()
    {
        var arr = new Arrangement();
        arr.EventPoints.Add(new EventPoint());
        var state = new ChartDocumentState { ActiveArrangement = arr };
        Assert.Single(state.EventPoints);
    }

    [Fact]
    public void Beats_DelegateToProject()
    {
        var proj = new ChartProject();
        proj.Beats.Add(new BeatMarker { Time = 100f });
        var state = new ChartDocumentState { ActiveProject = proj };
        Assert.Single(state.Beats);
    }

    [Fact]
    public void DefaultPrimitives_HaveEmptyStrings()
    {
        var state = new ChartDocumentState();
        Assert.Equal("", state.CurrentProjectPath);
        Assert.Equal("", state.CurrentAudioFileName);
        Assert.Equal(0.0, state.TotalAudioLengthMs);
        Assert.Null(state.WaveformData);
    }
}
