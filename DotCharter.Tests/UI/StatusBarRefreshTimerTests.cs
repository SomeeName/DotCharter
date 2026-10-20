using System.Text.RegularExpressions;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>StatusBarViewModel</c>'s 1 Hz refresh used to be a
///     <c>System.Timers.Timer</c> whose Elapsed handler <c>Post</c>ed from a
///     thread-pool thread. Two things were wrong with that shape:
///     <list type="bullet">
///         <item>In the app it is a pool hop per second for a UI-only refresh.</item>
///         <item>Under <c>Avalonia.Headless</c> every booted app is torn down
///             WITHOUT its container being disposed, so every test that built the
///             shell leaked one such timer for the rest of the run — hundreds of
///             pool threads posting into whichever dispatcher exists at that
///             instant, including the gap between one test's
///             <c>Dispatcher.ResetForUnitTests()</c> and the next test's platform
///             init. That is the shape of the once-per-run
///             <c>ServerCompositor..ctor ← DefaultRenderLoop.Add ← VerifyAccess</c>
///             setup failure (perf-harnesses.md).</item>
///     </list>
///     The refresh goes through <see cref="IUiDispatcher.SchedulePeriodic" /> now
///     — a <c>DispatcherTimer</c> in the app, owned by the dispatcher and gone
///     with it; the interface's default is the old pool shape so the test fakes
///     need no change.
/// </summary>
public sealed class StatusBarRefreshTimerTests
{
    private sealed class RecordingUi : IUiDispatcher
    {
        public TimeSpan? Period;
        public Action? Tick;
        public bool Stopped;

        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
        {
            Period = period;
            Tick = tick;
            return new Stop(this);
        }

        private sealed class Stop(RecordingUi owner) : IDisposable
        {
            public void Dispose() => owner.Stopped = true;
        }
    }

    [Fact]
    public void TheRefresh_IsScheduledThroughTheDispatcher_AndStoppedOnDispose()
    {
        var ui = new RecordingUi();
        var state = new DotCharter.Services.State.ProjectState();
        var profiles = new DotCharter.Services.Gameplay.Profiles.PlayerProfileService(FakeLocalization.English(),
            filePath: Path.Combine(Path.GetTempPath(), $"dotcharter-statusbar-{Guid.NewGuid():N}.json"));
        var vm = new DotCharter.UI.ViewModels.Application.StatusBarViewModel(
            new StatusBarService(ui), state, new FakeAudioPlaybackService(), ui, profiles,
            new FakeFileStorageService(), FakeLocalization.English());

        Assert.Equal(TimeSpan.FromSeconds(1), ui.Period);
        Assert.NotNull(ui.Tick);
        ui.Tick!(); // the tick is the refresh itself, on the UI thread — it must not throw

        vm.Dispose();
        Assert.True(ui.Stopped);
    }

    [Fact]
    public void TheDefaultSchedule_TicksThroughPost()
    {
        var ui = new PostCountingUi();
        using var timer = ((IUiDispatcher)ui).SchedulePeriodic(TimeSpan.FromMilliseconds(15), () => ui.Ticks++);
        SpinWait.SpinUntil(() => ui.Ticks >= 2, TimeSpan.FromSeconds(10));
        Assert.True(ui.Ticks >= 2, "the default periodic schedule never ticked");
        Assert.True(ui.Posts >= 2, "the default schedule must reach the UI thread through Post");
    }

    private sealed class PostCountingUi : IUiDispatcher
    {
        public int Ticks;
        public int Posts;
        public bool CheckAccess() => true;
        public void Post(Action action) { Interlocked.Increment(ref Posts); action(); }
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    [Fact]
    public void NoPoolTimerIsLeftInTheStatusBar_AndTheAppUsesADispatcherTimer()
    {
        var vm = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Application", "StatusBarViewModel.cs")));
        vm = Regex.Replace(vm, @"^\s*//.*$", "", RegexOptions.Multiline);
        Assert.DoesNotContain("System.Timers", vm);
        Assert.Contains("_ui.SchedulePeriodic(TimeSpan.FromSeconds(1)", vm);

        var app = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Platform", "AvaloniaUiDispatcher.cs")));
        Assert.Contains("new DispatcherTimer(DispatcherPriority.Background)", app);
    }
}
