using DotCharter.Services.Editing;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #149: a stacked-track row over a missing service used to construct and then
///     throw from its getters — the UI sweep built one reflectively (it fills
///     interface parameters with null) and logged "binding IsChecked to
///     IsGhostVisible … Object reference not set" on every run. The row now refuses
///     the missing service at construction, and a row built properly reads its
///     ghost state without throwing.
/// </summary>
public sealed class MultiTrackEntryContractTests
{
    private static object?[] ValidArgs()
    {
        var state = new ProjectState();
        var arrangement = new Arrangement { Name = "Lead" };
        var context = new TrackContextRegistry(state, new SelectionService()).GetOrCreate(arrangement);
        return
        [
            arrangement, context, false, new GhostArrangementsService(), new StackedArrangementsService(),
            RecordingProxy.Create<IGlobalEditingCoordinator>().Proxy, state, new FakeAppLogger(),
        ];
    }

    private static MultiTrackEntry Build(object?[] a) => new(
        (Arrangement)a[0]!, (TrackEditorContext)a[1]!, (bool)a[2]!, (IGhostArrangementsService)a[3]!,
        (IStackedArrangementsService)a[4]!, (IGlobalEditingCoordinator)a[5]!, (ProjectState)a[6]!,
        (IAppLogger)a[7]!);

    [Fact]
    public void ARowBuiltWithItsServices_ReadsAndWritesItsGhostState()
    {
        var args = ValidArgs();
        var entry = Build(args);
        Assert.False(entry.IsGhostVisible);
        entry.IsGhostVisible = true;
        Assert.True(((IGhostArrangementsService)args[3]!).IsVisible(entry.Arrangement));
        Assert.True(entry.IsGhostVisible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AMissingDependency_IsRefusedAtConstruction(int index)
    {
        var args = ValidArgs();
        args[index] = null;
        Assert.Throws<ArgumentNullException>(() => Build(args));
    }
}
