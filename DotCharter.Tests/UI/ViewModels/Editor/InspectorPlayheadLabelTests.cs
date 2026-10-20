using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Sound tab's "At the playhead" line read 0:00.000 in the live app with the playhead at
///     1:37 (2026-09-23): <c>PlayheadLabel</c> was a computed property nothing ever raised, so it
///     kept the time the view first bound. The inspector cannot be built in a test (its transport
///     starts the audio engine), so the decision is pinned on the two pure predicates and the wiring
///     by a source lint.
/// </summary>
public sealed class InspectorPlayheadLabelTests
{
    [Fact]
    public void ThePlayheadMovingWhileStopped_RefreshesTheLine() => Assert.True(InspectorViewModel.ProjectStateChangeRefreshesPlayheadLabel("CurrentTimeMs", isPlaying: false));

    /// <summary>During playback the line holds still: the status rail runs the live clock, and
    /// re-formatting this string every frame buys nothing.</summary>
    [Fact]
    public void ThePlayheadMovingDuringPlayback_LeavesTheLineAlone() => Assert.False(InspectorViewModel.ProjectStateChangeRefreshesPlayheadLabel("CurrentTimeMs", isPlaying: true));

    [Fact]
    public void AnUnrelatedProjectChange_LeavesTheLineAlone() => Assert.False(InspectorViewModel.ProjectStateChangeRefreshesPlayheadLabel("ActiveArrangement", isPlaying: false));

    /// <summary>Playback stopping is where the held line catches up; the grid is printed in it.</summary>
    [Theory]
    [InlineData("IsPlaying", true)]
    [InlineData("GridDivisor", true)]
    [InlineData("Volume", false)]
    public void TheTransport_RefreshesTheLineOnPlaybackAndGridChanges(string property, bool refreshes) => Assert.Equal(refreshes, InspectorViewModel.TransportChangeRefreshesPlayheadLabel(property));

    [Fact]
    public void TheInspector_RaisesTheLineFromBothHandlers_AndUnsubscribes()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var source = string.Concat(UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs").Select(UiSourceTree.ReadAllText));

        Assert.Contains("ProjectStateChangeRefreshesPlayheadLabel(e.PropertyName, Transport.IsPlaying)", source);
        Assert.Contains("TransportChangeRefreshesPlayheadLabel(e.PropertyName)", source);
        Assert.Equal(2, Regex.Matches(source, @"OnPropertyChanged\(PlayheadLabelChangedArgs\)").Count);
        Assert.Contains("Transport.PropertyChanged += OnTransportChanged;", source);
        Assert.Contains("Transport.PropertyChanged -= OnTransportChanged;", source);
    }
}
