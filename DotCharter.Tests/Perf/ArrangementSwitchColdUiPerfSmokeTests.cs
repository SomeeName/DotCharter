using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The #856 harness: opens the densest multi-arrangement corpus chart in the
///     booted headless app and switches arrangements, logging every switch from
///     the assignment to the drained <see cref="DispatcherPriority.ApplicationIdle" />
///     tick (what <c>Arrangement switch #N</c> measures in the app) plus the
///     cost of each synchronous <see cref="ProjectState" /> subscriber. It asserts
///     only that the switches happened; the timings go to
///     <c>perf-reports/arrangement-switch-cold.txt</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ArrangementSwitchColdUiPerfSmokeTests(ITestOutputHelper output)
{
    private static readonly FieldInfo HandlersField =
        typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ObservableObject.PropertyChanged has no backing field");

    [Fact]
    public Task ColdAndWarmSwitchesOnADenseCorpusChartAreLogged() => HeadlessSessionFixture.Dispatch(() =>
    {
        var chart = FindDensestMultiArrangementChart();
        if (chart is null) Assert.Skip("No multi-arrangement .rscp corpus under TestData/RsCharter.");

        var services = App.Services ?? throw new InvalidOperationException("headless services are unavailable");
        var shell = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var project = new RscpChartSerializer().Load(chart);
            shell.ChartEditor.ApplyProject(project, resetHistory: true);
            Drain(window);

            var report = new StringBuilder();
            report.AppendLine($"chart: {Path.GetFileName(Path.GetDirectoryName(chart))}, arrangements={project.Arrangements.Count}, "
                + $"notes={project.Arrangements.Sum(a => a.Levels.Sum(l => l.Notes.Count + l.Chords.Count))}");
            var order = new[] { 1, 0, 1, 2 % project.Arrangements.Count, 0, 1 };
            var switches = 0;
            foreach (var index in order)
            {
                var target = project.Arrangements[index];
                if (ReferenceEquals(target, state.ActiveArrangement)) continue;
                var subscribers = new List<(string Name, double Ms)>();
                var handlers = (PropertyChangedEventHandler?)HandlersField.GetValue(state);
                HandlersField.SetValue(state, Timed(handlers, subscribers));
                var clock = Stopwatch.StartNew();
                double setterMs;
                try
                {
                    state.ActiveArrangement = target;
                    setterMs = clock.Elapsed.TotalMilliseconds;
                }
                finally
                {
                    HandlersField.SetValue(state, handlers);
                }

                Drain(window);
                var idleMs = clock.Elapsed.TotalMilliseconds;
                switches++;
                report.AppendLine($"switch #{switches} -> {target.Name}: setter {setterMs:0.0} ms, to idle {idleMs:0.0} ms");
                foreach (var group in subscribers.GroupBy(s => s.Name).OrderByDescending(g => g.Sum(s => s.Ms)).Take(6))
                    report.AppendLine($"    {group.Sum(s => s.Ms),7:0.0} ms  {group.Key}");
            }

            var text = report.ToString();
            output.WriteLine(text);
            var dir = TestArtifactRun.Directory(Path.Combine(AppContext.BaseDirectory, "perf-reports"));
            File.WriteAllText(Path.Combine(dir, "arrangement-switch-cold.txt"), text);
            Assert.True(switches >= 3, text);
            Assert.True(window.Bounds.Width > 0);
        }
        finally
        {
            window.Close();
        }
    }, TestContext.Current.CancellationToken);

    private static void Drain(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static PropertyChangedEventHandler? Timed(PropertyChangedEventHandler? handlers, List<(string, double)> sink)
    {
        if (handlers is null) return null;
        var list = handlers.GetInvocationList().Cast<PropertyChangedEventHandler>().ToArray();
        return (sender, e) =>
        {
            foreach (var handler in list)
            {
                var start = Stopwatch.GetTimestamp();
                handler(sender, e);
                var target = handler.Target?.GetType();
                var owner = target?.DeclaringType ?? target;
                sink.Add(($"{owner?.Name}.{handler.Method.Name}", Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            }
        };
    }

    private static string? FindDensestMultiArrangementChart()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "TestData", "RsCharter"))) dir = dir.Parent;
        if (dir is null) return null;
        return Directory.EnumerateFiles(Path.Combine(dir.FullName, "TestData", "RsCharter"), "project.rscp", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => new FileInfo(p).Length)
            .Take(8)
            .FirstOrDefault(p => new RscpChartSerializer().Load(p).Arrangements.Count >= 3);
    }
}
