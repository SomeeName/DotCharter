using System.Diagnostics;
using System.Text.RegularExpressions;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The prewarm drains only step while the user is idle (0.49.4 — "the
///     app is laggy right after launch"). The rule is pure; the lints keep the
///     three drains on it, the window feeding it, and the headless fixture
///     switching it off (its bounded drain runs queued jobs, not timers).
/// </summary>
public sealed class PrewarmPacerTests
{
    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void RunsAtOnce_WhenTheGateIsOff_OrNothingWasEverTyped()
    {
        Assert.True(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(999), requestedTicks: Ms(1000), nowTicks: Ms(1000), quietWindowMs: 0, maxDeferralMs: 8000));
        Assert.True(PrewarmPacer.ShouldRunNow(lastInputTicks: 0, requestedTicks: Ms(1000), nowTicks: Ms(1000), quietWindowMs: 250, maxDeferralMs: 8000));
    }

    [Fact]
    public void Waits_WhileInputIsFresh_AndRuns_OnceItIsQuiet()
    {
        var request = Ms(1000);
        Assert.False(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(990), requestedTicks: request, nowTicks: Ms(1000), 250, 8000));
        Assert.False(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(990), requestedTicks: request, nowTicks: Ms(1239), 250, 8000));
        Assert.True(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(990), requestedTicks: request, nowTicks: Ms(1240), 250, 8000));
    }

    [Fact]
    public void AContinuousDrag_CannotStarveTheDrain()
    {
        var request = Ms(1000);
        // Input every few ms for eight seconds: still runs at the cap.
        Assert.False(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(8995), requestedTicks: request, nowTicks: Ms(8999), 250, 8000));
        Assert.True(PrewarmPacer.ShouldRunNow(lastInputTicks: Ms(8999), requestedTicks: request, nowTicks: Ms(9000), 250, 8000));
    }

    /// <summary>
    ///     A step posted while the input was quiet looks AGAIN when its dispatcher
    ///     turn comes (#234). A click that lands while the step waits in the queue
    ///     is handled first, and the queue is then idle until the render tick — so
    ///     without this second look the step ran between the click and the frame
    ///     that shows it, and a 3 ms switch drew 74-122 ms late.
    /// </summary>
    [Fact]
    public void APostedStep_LooksAgainWhenItsTurnComes_AndAClickSinceSendsItBack()
    {
        var ran = 0;
        var request = Ms(1000);

        Assert.False(PrewarmPacer.TryRun(() => ran++, lastInputTicks: Ms(1100), requestedTicks: request,
            nowTicks: Ms(1105), quietWindowMs: 250, maxDeferralMs: 8000));
        Assert.Equal(0, ran);

        Assert.True(PrewarmPacer.TryRun(() => ran++, lastInputTicks: Ms(1100), requestedTicks: request,
            nowTicks: Ms(1350), quietWindowMs: 250, maxDeferralMs: 8000));
        Assert.Equal(1, ran);
    }

    /// <summary>
    ///     …and the pacer really posts that second look, not the step: a post of
    ///     the bare step is the bug the fact above describes.
    /// </summary>
    [Fact]
    public void ThePacer_PostsTheSecondLook_NeverTheBareStep()
    {
        var src = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "PrewarmPacer.cs"))));
        var attempt = src[src.IndexOf("private static void Attempt(", StringComparison.Ordinal)..];

        Assert.DoesNotContain("Dispatcher.UIThread.Post(step", attempt);
        Assert.Matches(new Regex(@"Dispatcher\.UIThread\.Post\(\(\) =>\s*\{[^{}]*TryRun\(step"), attempt);
    }

    [Fact]
    public void NoteInput_StampsNow()
    {
        var before = Stopwatch.GetTimestamp();
        PrewarmPacer.NoteInput();
        Assert.InRange(PrewarmPacer.LastInputTicks, before, Stopwatch.GetTimestamp());
    }

    [Fact]
    public void EveryDrain_PostsThroughThePacer_AndNothingElsePostsAStepDirectly()
    {
        foreach (var file in new[] { "HiddenSubtreePrewarmer.cs", "MenuFlyoutPrewarmer.cs" })
        {
            var src = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", file))));
            Assert.Contains("PrewarmPacer.Post(Step)", src);
            Assert.DoesNotContain("Dispatcher.UIThread.Post(Step", src);
        }
    }

    [Fact]
    public void TheWindowFeedsIt_AndTheHeadlessFixtureTurnsItOff()
    {
        var window = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.axaml.cs"))));
        foreach (var ev in new[] { "PointerMovedEvent", "PointerPressedEvent", "PointerWheelChangedEvent", "KeyDownEvent" })
            Assert.Matches(new Regex($@"AddHandler\({ev},\s*\(_, _\) => PrewarmPacer\.NoteInput\(\)"), window);

        var fixture = SourceText.StripComments(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "HeadlessAppFixture.cs")));
        Assert.Contains("PrewarmPacer.QuietWindowMs = 0", fixture);
    }
}
