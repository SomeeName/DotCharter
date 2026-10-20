using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

public class EditingScopeTests
{
    [Fact]
    public void EmptyFallbacks_ArePerInstance()
    {
        // They used to be `static readonly`, so an action executed with no active
        // level added into a PROCESS-WIDE collection every other scope then saw —
        // and it persisted across test cases.
        var a = new EditingScope(new ProjectState());
        var b = new EditingScope(new ProjectState());

        a.Notes.Add(new GuitarNote { Fret = 5 });
        a.Chords.Add(new Chord());
        a.HandShapes.Add(new HandShape());
        a.Anchors.Add(new FretAnchor());
        a.EventPoints.Add(new EventPoint());

        Assert.Empty(b.Notes);
        Assert.Empty(b.Chords);
        Assert.Empty(b.HandShapes);
        Assert.Empty(b.Anchors);
        Assert.Empty(b.EventPoints);
    }

    [Fact]
    public void WithAnActiveLevel_ResolvesToTheLevelsOwnCollections()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var state = new ProjectState { ActiveArrangement = arrangement };

        var scope = new EditingScope(state);

        Assert.Same(level.Notes, scope.Notes);
        Assert.Same(arrangement.EventPoints, scope.EventPoints);
    }
}
