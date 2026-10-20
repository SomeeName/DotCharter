using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Editing.Linting;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="AudioLintViewModel" /> against a chart switch.
///     <para>
///         An audio-lint run decodes the whole song and runs a CQT over it —
///         tens of seconds on a real track, on a background thread — and the
///         charter is free to open another arrangement, or another project,
///         while it works. The findings are TIMESTAMPS into the song that was
///         analysed, so once the chart underneath them changes they name
///         positions in a song that is no longer loaded, and
///         <c>JumpTo</c> seeks the NEW song to the OLD one's times.
///     </para>
///     <para>
///         The view model had no notion of either event: nothing reset it on a
///         switch (its own doc says "findings persist until the next run"), and
///         a run that finished after a switch wrote its results into a panel now
///         describing a different chart. The service takes a
///         <see cref="CancellationToken" /> and was handed none, so the decode
///         also ran on to completion for a chart nobody was looking at.
///     </para>
/// </summary>
public sealed class AudioLintStaleResultTests
{
    /// <summary>A lint service the test finishes by hand, so the switch lands
    /// strictly INSIDE the run.</summary>
    private sealed class GatedLintService : IAudioChartLintService
    {
        public readonly TaskCompletionSource<AudioChartLintOutcome> Gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Arrangement? Asked;
        public CancellationToken Token;

        public Task<AudioChartLintOutcome> LintAsync(Arrangement arrangement, IList<BeatMarker> beats,
            string? audioPath, CancellationToken cancellationToken = default)
        {
            Asked = arrangement;
            Token = cancellationToken;
            return Gate.Task;
        }
    }

    private static AudioChartLintOutcome OneFinding() => new(
        Ran: true, Error: null, SoundsChecked: 1, SoundsVerified: 0,
        Findings: [new ChartAudioFinding(1234.0, 60, 0.9f, 59, 0.8f)]);

    private static (AudioLintViewModel vm, GatedLintService svc, Arrangement[] current) Rig()
    {
        var svc = new GatedLintService();
        var current = new[] { new Arrangement { Name = "Lead" } };
        var vm = new AudioLintViewModel(
            () => current[0],
            () => [new() { Time = 0, Bpm = 120 }],
            () => "song.ogg",
            _ => { },
            svc,
            FakeLocalization.English());
        return (vm, svc, current);
    }

    /// <summary>The ordinary path, so the guards below cannot pass by refusing
    /// every result.</summary>
    [Fact]
    public async Task ARunThatFinishesOnTheSameChartIsShown()
    {
        var (vm, svc, _) = Rig();

        var run = vm.RunCommand.ExecuteAsync(null);
        svc.Gate.SetResult(OneFinding());
        await run;

        Assert.Single(vm.AudioFindings);
        Assert.True(vm.HasResult);
        Assert.False(vm.IsRunning);
    }

    /// <summary>
    ///     The arrangement changed under the run: the result belongs to a chart
    ///     the panel is no longer describing, so it is discarded rather than
    ///     listed against the new one.
    /// </summary>
    [Fact]
    public async Task ARunThatFinishesAfterAChartSwitchIsDiscarded()
    {
        var (vm, svc, current) = Rig();

        var run = vm.RunCommand.ExecuteAsync(null);
        current[0] = new Arrangement { Name = "Rhythm" }; // the charter switches arrangement
        svc.Gate.SetResult(OneFinding());
        await run;

        Assert.Empty(vm.AudioFindings);
        Assert.False(vm.HasResult);
        Assert.False(vm.IsRunning);
    }

    /// <summary>
    ///     <c>Reset</c> is what the inspector calls on a project or arrangement
    ///     change. It forgets the last run's findings AND its summary line — a
    ///     stale "4 of 200 sounds verified" is as wrong as the rows under it.
    /// </summary>
    [Fact]
    public async Task ResetForgetsTheLastRunsFindingsAndSummary()
    {
        var (vm, svc, _) = Rig();

        var run = vm.RunCommand.ExecuteAsync(null);
        svc.Gate.SetResult(OneFinding());
        await run;
        Assert.Single(vm.AudioFindings);

        vm.Reset();

        Assert.Empty(vm.AudioFindings);
        Assert.False(vm.HasResult);
        Assert.Equal("", vm.SummaryLine);
    }

    /// <summary>
    ///     A run in flight is CANCELLED by the reset, not merely ignored — the
    ///     work is a whole-song decode plus a CQT matrix, and the service has
    ///     taken a token for it all along.
    /// </summary>
    [Fact]
    public async Task ResetCancelsTheRunInFlight()
    {
        var (vm, svc, _) = Rig();

        var run = vm.RunCommand.ExecuteAsync(null);
        Assert.True(svc.Token.CanBeCanceled, "the service is handed a real token");
        Assert.False(svc.Token.IsCancellationRequested);

        vm.Reset();

        Assert.True(svc.Token.IsCancellationRequested);

        svc.Gate.SetCanceled(svc.Token);
        await run;

        Assert.Empty(vm.AudioFindings);
        Assert.False(vm.IsRunning);
    }

    /// <summary>
    ///     The view model cannot see a project switch by itself — the inspector
    ///     owns the ProjectState subscription — so the WIRING is what makes the
    ///     reset real. Linted rather than left to a heavyweight
    ///     <c>InspectorViewModel</c> construction, in the house style of the
    ///     reachability guards: comments stripped first, because a
    ///     <c>see cref</c> is exactly as inert as a dead call.
    /// </summary>
    [Fact]
    public void TheInspectorResetsTheAudioLintOnAProjectOrArrangementSwitch()
    {
        var source = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "InspectorViewModel.cs"))));

        Assert.Contains("AudioLint.Reset()", source, StringComparison.Ordinal);

        // …and inside the ProjectState handler, next to the export-findings
        // reset that already had this exact contract.
        var handler = source[source.IndexOf("private void OnProjectStateChanged", StringComparison.Ordinal)..];
        var end = handler.IndexOf("private void ScheduleIssueRefresh", StringComparison.Ordinal);
        Assert.True(end > 0, "OnProjectStateChanged is still followed by ScheduleIssueRefresh");
        handler = handler[..end];

        Assert.Contains("AudioLint.Reset()", handler, StringComparison.Ordinal);
        Assert.Contains(nameof(ProjectState.ActiveArrangement), handler, StringComparison.Ordinal);
    }
}
