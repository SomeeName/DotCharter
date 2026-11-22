using System.ComponentModel;
using Avalonia.Threading;
using DotCharter.UI;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class ArrangementSwitchOrderUiPerfSmokeTests
{
    [Fact]
    public Task TheTimingSinkRunsAfterTheSubscribersQueuedRefresh() => HeadlessSessionFixture.Dispatch(() =>
    {
        var services = App.Services ?? throw new InvalidOperationException("headless services are unavailable");
        var state = services.GetRequiredService<ProjectState>();
        var previousProject = state.ActiveProject;
        var previous = state.ActiveArrangement;
        // Only a switch inside the open project is timed (#856).
        var project = new ChartProject
        {
            Arrangements = { new Arrangement { Levels = { new ChartLevel() } }, new Arrangement { Levels = { new ChartLevel() } } }
        };
        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
        Dispatcher.UIThread.RunJobs(DispatcherPriority.ApplicationIdle);
        var sink = SurfaceShowTiming.Sink;
        var order = new List<string>();
        PropertyChangedEventHandler subscriber = (_, e) =>
        {
            if (e.PropertyName != nameof(ProjectState.ActiveArrangement)) return;
            Dispatcher.UIThread.Post(() =>
            {
                order.Add("refresh");
                Dispatcher.UIThread.Post(() => order.Add("trailing work"), DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        };
        state.PropertyChanged += subscriber;
        try
        {
            SurfaceShowTiming.Sink = (name, _) =>
            {
                if (name.StartsWith("Arrangement switch #", StringComparison.Ordinal)) order.Add("timing");
            };
            state.ActiveArrangement = project.Arrangements[1];
            Dispatcher.UIThread.RunJobs(DispatcherPriority.ApplicationIdle);

            Assert.Equal(["refresh", "trailing work", "timing"], order);
        }
        finally
        {
            state.PropertyChanged -= subscriber;
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previous;
            SurfaceShowTiming.Sink = sink;
        }
    }, TestContext.Current.CancellationToken);
}
