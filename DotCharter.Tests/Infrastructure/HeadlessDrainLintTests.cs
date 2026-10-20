using System.Text.RegularExpressions;
using DotCharter.Tests.Perf;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Guards the two halves of the headless suite's drain — the fix for what
///     this repo's guides called "the residual <c>EnsureIsolatedApplication</c>
///     race" for a fortnight.
///     <para>
///         <b>It was never a race.</b> The session runs at
///         <c>AvaloniaTestIsolationLevel.PerTest</c>, so
///         <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c> disposes the
///         whole application after EVERY dispatch, and that teardown calls
///         <c>Dispatcher.ResetForUnitTests()</c>, which EXECUTES every job still
///         queued and throws <c>InvalidProgramException: You've caused
///         dispatcher loop</c> if the queue is not quiet within a hard-coded
///         <b>5 seconds</b> (Avalonia 12.1.1 <c>Dispatcher.Queue.cs</c>). The app
///         posts a self-refilling chain of
///         <see cref="Avalonia.Threading.DispatcherPriority.Background" /> jobs
///         after first paint (<c>HiddenSubtreePrewarmer.ScheduleDrain</c>, both
///         <c>MenuFlyoutPrewarmer</c> chains), so any case that shows a window
///         was racing a five-second budget nobody had declared — which is why
///         the victims moved between runs and why CPU contention made it worse.
///     </para>
///     <para>
///         <see cref="HeadlessSessionFixture.Dispatch(Action,CancellationToken)" />
///         drains that queue inside the test's own time. These two facts keep it
///         from silently coming undone: a call site that goes around the wrapper
///         reintroduces the flake for itself, and a lost reflection binding
///         downgrades the bounded drain without a word.
///     </para>
/// </summary>
public sealed class HeadlessDrainLintTests
{
    /// <summary>
    ///     The bounded drain uses Avalonia's INTERNAL
    ///     <c>Dispatcher.RunJobs(DispatcherPriority?, CancellationToken)</c>,
    ///     because the public overload cannot be bounded and a hang is a worse
    ///     failure than the flake it replaces. A package bump that renames or
    ///     drops that overload leaves the binding null, and the fallback is the
    ///     unbounded public one — which works, but silently loses the deadlock
    ///     detector. This is the only thing that would say so.
    /// </summary>
    [Fact]
    public void TheBoundedDrain_IsStillBoundToAvaloniasCancellableRunJobs()
    {
        Assert.True(HeadlessSessionFixture.RunJobsWithToken != null,
            "Dispatcher.RunJobs(DispatcherPriority?, CancellationToken) no longer resolves by reflection — " +
            "Avalonia's internal API changed. HeadlessSessionFixture.DrainAppBackgroundWork has fallen back " +
            "to the UNBOUNDED public overload, so a self-posting job now hangs the headless suite instead of " +
            "failing it. Re-point the binding.");

        Assert.Equal(typeof(void), HeadlessSessionFixture.RunJobsWithToken!.ReturnType);
    }

    /// <summary>
    ///     The orphaned-<c>MediaContext</c> guard reads Avalonia's INTERNAL
    ///     <c>Avalonia.Media.MediaContext</c> and its <c>_dispatcher</c> field.
    ///     If a package bump renames either, the guard switches itself off and
    ///     a session whose render loop an earlier case killed goes back to
    ///     failing one unrelated assertion at a time (the 2026-09-19 headless
    ///     order failures). This is the only thing that would say so.
    /// </summary>
    [Fact]
    public void TheOrphanedMediaContextGuard_IsStillBoundToAvaloniasInternals()
    {
        Assert.True(HeadlessSessionFixture.MediaContextType != null,
            "Avalonia.Media.MediaContext no longer resolves by reflection — HeadlessSessionFixture's " +
            "orphaned-MediaContext guard is OFF. Re-point the binding.");
        Assert.True(HeadlessSessionFixture.MediaContextDispatcherField != null,
            "MediaContext._dispatcher no longer resolves by reflection — HeadlessSessionFixture's " +
            "orphaned-MediaContext guard is OFF. Re-point the binding.");
        Assert.Equal(typeof(Avalonia.Threading.Dispatcher), HeadlessSessionFixture.MediaContextDispatcherField!.FieldType);
        Assert.True(HeadlessSessionFixture.CurrentLocatorProperty != null,
            "AvaloniaLocator.Current no longer resolves by reflection — HeadlessSessionFixture's " +
            "orphaned-MediaContext guard is OFF. Re-point the binding.");
    }

    /// <summary>
    ///     The setup-race fix (#192) reads three of Avalonia's INTERNAL
    ///     dispatcher statics. A package bump that renames one leaves
    ///     <see cref="HeadlessUiThreadClaim.Claim" /> doing nothing, and the
    ///     headless suite goes back to losing a case or three per run to
    ///     <c>The calling thread cannot access this object</c>. That it is still
    ///     INSTALLED is the headless suite's job
    ///     (<c>Perf/HeadlessUiThreadClaimUiPerfSmokeTests</c>: its staged race
    ///     never runs without it).
    /// </summary>
    [Fact]
    public void TheUiThreadClaim_IsStillBoundToAvaloniasDispatcherStatics()
    {
        Assert.True(HeadlessUiThreadClaim.GlobalLockField?.FieldType == typeof(object),
            "Dispatcher.s_globalLock no longer resolves by reflection — HeadlessUiThreadClaim is OFF and the " +
            "headless setup race (#192) is back. Re-point the binding.");
        Assert.True(HeadlessUiThreadClaim.UiThreadField?.FieldType == typeof(Avalonia.Threading.Dispatcher),
            "Dispatcher.s_uiThread no longer resolves by reflection — HeadlessUiThreadClaim is OFF. Re-point the binding.");
        Assert.True(HeadlessUiThreadClaim.ResetBeforeUnitTestsMethod != null,
            "Dispatcher.ResetBeforeUnitTests() no longer resolves by reflection — HeadlessUiThreadClaim is OFF. " +
            "Re-point the binding.");
    }

    /// <summary>
    ///     Nothing outside the fixture may touch the raw session: a
    ///     <c>Get().Dispatch(…)</c> skips the drain and hands that one case back
    ///     to Avalonia's five-second budget.
    /// </summary>
    [Fact]
    public void NoTestDispatchesAroundTheDrainingWrapper()
    {
        var testRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        var offenders = new List<string>();
        var wrapperUsers = 0;
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            // The fixture DECLARES Get(); this lint QUOTES it in its own prose.
            if (name is "HeadlessAppFixture.cs" or "HeadlessDrainLintTests.cs"
                or "HeadlessCollectionLintTests.cs")
            {
                continue;
            }
            // LocalOnly_* probes are git-ignored owner-box scratch (see
            // DotCharter.Tests/AGENTS.md): they exist in no worktree and no CI,
            // so a red here would have no fix in the tree. Probes, not gate cases.
            if (name.StartsWith("LocalOnly_", StringComparison.Ordinal)) continue;

            scanned++;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (RawSessionUse.IsMatch(code)) offenders.Add(Path.GetRelativePath(testRoot, file));
            if (WrapperUse.IsMatch(code)) wrapperUsers++;
        }

        Assert.True(scanned > 500, $"only scanned {scanned} test files — the glob is broken");

        // Vacuity floor: if the wrapper stopped being the way tests dispatch,
        // "no offenders" would mean "nobody dispatches at all".
        Assert.True(wrapperUsers >= 30,
            $"only {wrapperUsers} files call HeadlessSessionFixture.Dispatch — the suite stopped using the " +
            "draining wrapper, so this lint is passing vacuously.");

        Assert.True(offenders.Count == 0,
            "these files reach the raw headless session instead of HeadlessSessionFixture.Dispatch, so the " +
            "app's own Background prewarm chains are left for Avalonia's 5 s ResetForUnitTests drain to run " +
            "— which is the 'dispatcher loop' failure that killed 3-6 cases a run:\n  " +
            string.Join("\n  ", offenders));
    }

    private static readonly Regex RawSessionUse = new(
        @"HeadlessSessionFixture\s*\.\s*Get\s*\(", RegexOptions.Compiled);

    private static readonly Regex WrapperUse = new(
        @"HeadlessSessionFixture\s*\.\s*Dispatch\s*\(", RegexOptions.Compiled);
}
