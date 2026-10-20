using DotCharter.Services.Infrastructure.Process;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The app runs inside a kill-on-close job object so nothing it spawns can
///     outlive it. The updater is the ONE child that must — it waits for our PID
///     to disappear and only then copies the payload over the install dir.
///     <para>
///         That makes these two facts a matched pair, and breaking either one
///         produces a SILENT failure: reverting the updater to
///         <c>Process.Start</c> puts it in the job, so exiting to apply the update
///         kills the updater with us and the update simply never happens — no
///         error, no log line, the app just relaunches on the old version. And a
///         job without <c>BreakawayOk</c> makes the breakaway call fail for the
///         same net effect.
///     </para>
///     <para>
///         Source lints, because the real behaviour needs a process that actually
///         dies. What is being protected is a structural coupling between two
///         files, which is exactly what a lint can hold.
///     </para>
/// </summary>
public sealed class UpdaterBreakawayGuardTests
{

    private static string Source(string relative)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), relative);
        Assert.True(File.Exists(path), $"Expected source file not found: {relative}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheUpdaterIsLaunchedOutsideTheJobObject()
    {
        // The launch moved out of GitHubUpdateService into UpdateInstaller (#652).
        var source = Source(Path.Combine("DotCharter.Services", "Update", "UpdateInstaller.cs"));

        Assert.Contains("ChildProcessJob.StartDetached", source);

        // A plain Process.Start would be adopted into the job and die with the app —
        // anywhere in the updater, not only in the file that launches it today.
        var updateFolder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Update");
        var files = Directory.GetFiles(updateFolder, "*.cs", SearchOption.AllDirectories);
        Assert.Contains(files, f => Path.GetFileName(f) == "UpdateInstaller.cs");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Process.Start(psi)", text);
            Assert.DoesNotContain("System.Diagnostics.Process.Start", text);
        }
    }

    [Fact]
    public void ContainmentIsOnlyEverEnabledTogetherWithBreakaway()
    {
        var source = Source(Path.Combine(
            "DotCharter.Services", "Infrastructure", "Process", "ChildProcessJob.cs"));

        // The two flags are set in one expression precisely so they cannot drift.
        Assert.Contains("JobLimit.KillOnJobClose | JobLimit.BreakawayOk", source);
        Assert.Contains("CreateBreakawayFromJob", source);
    }

    /// <summary>
    ///     <b>DETACHED_PROCESS is what actually broke self-update in the field.</b>
    ///     It gives the child NO console, and a console-less <c>cmd.exe</c> running
    ///     a batch file deadlocks on the first pipeline — measured 3/3 on Windows
    ///     11 26200 against the exact command line this service uses, and 0/3 with
    ///     the flag removed. The updater script's first statement is a
    ///     <c>tasklist … | findstr …</c> wait loop, so every update staged its
    ///     payload, exited the app, and then hung at 0 % CPU before copying a byte:
    ///     no error, no relaunch, nothing in any log.
    ///     <para>
    ///         <c>CREATE_NO_WINDOW</c> alone already runs a console app with no
    ///         visible window, which is all "hidden" ever meant here. This asserts
    ///         on the constants rather than the source text so it cannot be
    ///         satisfied by a comment.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheUpdaterIsNeverLaunchedWithoutAConsole()
    {
        Assert.Equal(0u, ChildProcessJob.UpdaterCreationFlags & ChildProcessJob.DetachedProcess);

        // …and it still breaks out of the job, which is the other half of the pair.
        Assert.NotEqual(0u, ChildProcessJob.UpdaterCreationFlags & 0x0100_0000u);
    }

    /// <summary>
    ///     The job handle must NOT be closed on the success path: the job — and
    ///     every member — dies when the last handle closes, so closing it there
    ///     would disband the job immediately and terminate the app itself.
    /// </summary>
    [Fact]
    public void TheJobHandleIsHeldForTheProcessLifetime()
    {
        var source = Source(Path.Combine(
            "DotCharter.Services", "Infrastructure", "Process", "ChildProcessJob.cs"));

        var assign = source.IndexOf("AssignProcessToJobObject(job, GetCurrentProcess())", StringComparison.Ordinal);
        Assert.True(assign > 0, "the assign call moved — re-check that the handle is still held");

        // The failure branch closes it legitimately (no job, nothing to keep), so
        // the success path is everything AFTER that branch's own return.
        var tail = source[assign..];
        var lastFailure = tail.LastIndexOf("return false;", StringComparison.Ordinal);
        var successReturn = tail.IndexOf("return true;", StringComparison.Ordinal);
        Assert.True(lastFailure > 0 && successReturn > lastFailure,
            "expected the assign failure branch to return before the success path");

        Assert.DoesNotContain("CloseHandle(job)", tail[lastFailure..successReturn]);
    }
}
