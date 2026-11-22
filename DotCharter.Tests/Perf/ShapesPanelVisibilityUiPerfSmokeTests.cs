using Avalonia.Controls;
using DotCharter.Tests.Presentation.Editor;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class ShapesPanelVisibilityUiPerfSmokeTests
{
    [Fact]
    public Task AHiddenAncestorParksTheCatalogueUntilTheViewIsShown() => HeadlessSessionFixture.Dispatch(() =>
    {
        var rig = new ShapesSwitchRig();
        var view = new ShapesPanelView { DataContext = rig.Screen };
        var host = new Grid { Children = { view } };
        var window = NarrowWidthLayout.NewWindow(600, 524, host);
        window.Show();
        window.UpdateLayout();
        Assert.True(view.Bounds is { Width: > 0, Height: > 0 });
        Assert.Equal(64, rig.Screen.Rows.Count);
        var rows = rig.Screen.Rows;

        host.IsVisible = false;
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        rig.Dispatcher.Drain();
        window.UpdateLayout();

        Assert.Same(rows, rig.Screen.Rows);

        host.IsVisible = true;
        window.UpdateLayout();
        rig.Dispatcher.Drain();

        Assert.True(view.Bounds is { Width: > 0, Height: > 0 });
        Assert.NotSame(rows, rig.Screen.Rows);
        Assert.Same(rig.Chart.Project.Arrangements[1].ChordTemplates[0], rig.Screen.AllRows[0].Template);
    }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     A float or redock builds the pane's new view on the same screen while the old view still
    ///     exists: whichever of the two reports last, the screen is live while one of them shows it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AScreenShownByASecondViewStaysLiveWhenTheFirstViewLeaves(bool oldViewIsHiddenFirst) => HeadlessSessionFixture.Dispatch(() =>
    {
        var rig = new ShapesSwitchRig();
        var old = new ShapesPanelView { DataContext = rig.Screen };
        var host = new Grid { Children = { old } };
        var window = NarrowWidthLayout.NewWindow(600, 524, host);
        window.Show();
        window.UpdateLayout();
        var fresh = new ShapesPanelView { DataContext = rig.Screen };

        host.Children.Add(fresh);
        window.UpdateLayout();
        if (oldViewIsHiddenFirst) old.IsVisible = false;
        window.UpdateLayout();
        host.Children.Remove(old);
        window.UpdateLayout();
        var rows = rig.Screen.Rows;
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        rig.Dispatcher.Drain();

        Assert.True(fresh.Bounds is { Width: > 0, Height: > 0 });
        Assert.NotSame(rows, rig.Screen.Rows);

        host.Children.Remove(fresh);
        window.UpdateLayout();
        rows = rig.Screen.Rows;
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[2];
        rig.Dispatcher.Drain();

        Assert.Same(rows, rig.Screen.Rows);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task AHiddenSecondViewDoesNotParkTheScreenTheFirstViewShows() => HeadlessSessionFixture.Dispatch(() =>
    {
        var rig = new ShapesSwitchRig();
        var shown = new ShapesPanelView { DataContext = rig.Screen };
        var host = new Grid { Children = { shown } };
        var window = NarrowWidthLayout.NewWindow(600, 524, host);
        window.Show();
        window.UpdateLayout();

        host.Children.Add(new ShapesPanelView { DataContext = rig.Screen, IsVisible = false });
        window.UpdateLayout();
        var rows = rig.Screen.Rows;
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        rig.Dispatcher.Drain();

        Assert.NotSame(rows, rig.Screen.Rows);
    }, TestContext.Current.CancellationToken);
}
