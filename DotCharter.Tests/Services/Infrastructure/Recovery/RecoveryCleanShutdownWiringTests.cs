using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Infrastructure.Recovery;

/// <summary>
///     A clean close must leave no crash evidence — otherwise the next launch
///     offers to "recover" work the user deliberately closed, on every launch,
///     forever. That is exactly what shipped: <c>NotifyCleanShutdown</c> existed
///     but its ONLY caller was <c>ProjectSessionService.Dispose</c>, and the
///     shutdown path is <c>App.TerminateNow</c>, which <c>TerminateProcess</c>-es
///     the process so no managed teardown ever runs. The comment above that call
///     even said so ("while the shutdown path hard-terminates, this may not run")
///     and the consequence was filed as erring on the side of caution.
///     <para>
///         It bites hardest on a PSARC load, which has no project path: the
///         snapshot lands under the "untitled" key, and
///         <c>RecoverySnapshotStore.IsRecoverable</c> returns <c>true</c>
///         unconditionally for untitled snapshots — there is no "did anything
///         change" gate to save it.
///     </para>
///     <para>
///         Both halves are structural: proving them at runtime would need a
///         booted app and a real process kill. So they are source lints, in the
///         same shape as <c>ProjectReplaceGuardWiringTests</c>'s.
///     </para>
/// </summary>
public sealed class RecoveryCleanShutdownWiringTests
{
    /// <summary>
    ///     <c>TerminateNow</c> is the one shutdown path, so it is the last place
    ///     a clean close can retire its snapshot. It must ask for the recovery
    ///     service the same way it already asks for the usage tracker — and for
    ///     the identical stated reason (Dispose never runs).
    /// </summary>
    [Fact]
    public void TerminateNow_RetiresTheCrashSnapshot()
    {
        // App is one class over several partial files; the exit path lives in
        // the App.Shutdown.cs slice.
        var body = MethodBody(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.Shutdown.cs")), "TerminateNow");

        Assert.Contains("NotifyCleanShutdown", body);

        // It has to happen BEFORE the process is killed, or it never runs at all.
        var clear = body.IndexOf("NotifyCleanShutdown", StringComparison.Ordinal);
        var kill = body.IndexOf("TerminateProcess", StringComparison.Ordinal);
        Assert.True(kill > 0, "TerminateNow no longer calls TerminateProcess — re-check this guard.");
        Assert.True(clear < kill,
            "The snapshot must be retired before TerminateProcess; after it, the call is unreachable.");
    }

    /// <summary>
    ///     A session that edited nothing writes no snapshot of its own, so
    ///     <c>_lastSnapshotOriginalPath</c> is null — and an EARLIER session's
    ///     untitled snapshot is still on disk, unconditionally recoverable. So
    ///     the clean-shutdown path must also retire the key for whatever is open
    ///     NOW, not only what this session wrote. Guarding the whole method on
    ///     <c>_lastSnapshotOriginalPath == null</c> is precisely the bug.
    /// </summary>
    [Fact]
    public void NotifyCleanShutdown_AlsoRetiresTheOpenProjectsKey()
    {
        var body = MethodBody(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Infrastructure", "Recovery", "CrashRecoveryService.cs"),
            "NotifyCleanShutdown");

        Assert.Contains("CurrentProjectPath", body);
        Assert.DoesNotContain("if (_lastSnapshotOriginalPath == null) return;", body);
    }

    /// <summary>
    ///     Reads one method's source body by brace matching from its signature.
    ///     Comments are stripped so a doc comment naming the call cannot satisfy
    ///     a lint that means to find the CALL.
    /// </summary>
    private static string MethodBody(string filePath, string methodName)
    {
        var source = UiSourceTree.ReadAllText(filePath);
        source = Regex.Replace(source, @"//.*?$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);

        var signature = new Regex($@"\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{");
        var match = signature.Match(source);
        Assert.True(match.Success, $"{methodName} not found in {Path.GetFileName(filePath)}.");

        var start = match.Index + match.Length - 1;
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"Unbalanced braces reading {methodName}.");
        return "";
    }
}
