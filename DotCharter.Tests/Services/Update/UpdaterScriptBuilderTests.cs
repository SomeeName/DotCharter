using System.Text.RegularExpressions;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

public sealed class UpdaterScriptBuilderTests
{
    private const string Install = @"C:\Games\DotCharter";
    private const string Zip = @"C:\Users\Test\AppData\Roaming\DotCharter\updates\DotCharter-v0.13.0-win-x64.zip";

    private static string BuildDefault(
        IReadOnlyList<string>? deletions = null,
        int waitTimeoutSeconds = UpdaterScriptBuilder.DefaultWaitTimeoutSeconds)
    {
        return UpdaterScriptBuilder.Build(
            processId: 4242,
            payloadDir: @"C:\Games\DotCharter\update-staging\DotCharter-v0.13.0-win-x64",
            stagingRoot: @"C:\Games\DotCharter\update-staging",
            installDir: @"C:\Games\DotCharter\",
            exeName: "DotCharter.exe",
            zipPath: Zip,
            deletions: deletions,
            waitTimeoutSeconds: waitTimeoutSeconds);
    }

    /// <summary>The text between two labels, so a claim about ONE branch cannot be
    /// satisfied by a line in another.</summary>
    private static string Between(string script, string fromLabel, string toLabel)
    {
        var from = script.IndexOf("\r\n:" + fromLabel + "\r\n", StringComparison.Ordinal);
        var to = script.IndexOf("\r\n:" + toLabel + "\r\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $":{fromLabel} is missing from the script");
        Assert.True(to > from, $":{toLabel} must follow :{fromLabel}");
        return script[from..to];
    }

    private static string[] DelTargets(string script)
        => Regex.Matches(script, "del(?: /F /Q)? \"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    // ---- the wait ------------------------------------------------------------

    [Fact]
    public void Build_WaitsForTheAppPidToExit()
    {
        var script = BuildDefault();

        Assert.Contains("tasklist /FI \"PID eq 4242\" /NH", script);
        Assert.Contains("findstr /C:\" 4242 \"", script);
        Assert.Contains("goto wait_for_exit", script);
        Assert.Contains("timeout /T 1 /NOBREAK", script);
    }

    /// <summary>
    ///     <b>The bug this whole file exists around.</b> The wait loop used to be
    ///     <c>tasklist … | findstr …</c>. A cmd.exe with no console — which is what
    ///     <c>DETACHED_PROCESS</c> gives it — DEADLOCKS on its first pipeline: 0 %
    ///     CPU, no error, no exit, forever. Every self-update therefore staged its
    ///     payload, closed the app and then copied nothing and relaunched nothing.
    ///     <para>
    ///         The launch flags are fixed too (<c>UpdaterBreakawayGuardTests</c>),
    ///         but a script that only works under one particular set of creation
    ///         flags is a trap for whoever changes them next. <c>tasklist</c> writes
    ///         a probe file and <c>findstr</c> reads it, so there is nothing to
    ///         deadlock on.
    ///     </para>
    /// </summary>
    [Fact]
    public void Build_UsesNoPipeline()
    {
        var script = BuildDefault(["a/b.dll"]);

        Assert.DoesNotContain("|", script);
        Assert.Contains("/NH >\"%DCPROBE%\"", script);
        Assert.Contains("findstr /C:\" 4242 \" \"%DCPROBE%\"", script);
    }

    /// <summary>
    ///     An unbounded spin on a PID that may never die is indefensible however
    ///     the process got there: it produces the reported symptom exactly
    ///     (staging populated, nothing copied, no relaunch, no error).
    /// </summary>
    [Fact]
    public void Build_BoundsTheWait()
    {
        var script = BuildDefault(waitTimeoutSeconds: 42);

        Assert.Contains("set DCWAIT=0", script);
        Assert.Contains("set /A DCWAIT+=1", script);
        Assert.Contains("if %DCWAIT% GEQ 42 goto wait_timeout", script);
        Assert.Contains(":wait_timeout", script);
    }

    [Fact]
    public void Build_RefusesANonPositiveWaitTimeout() => Assert.Throws<ArgumentOutOfRangeException>(() => BuildDefault(waitTimeoutSeconds: 0));

    /// <summary>
    ///     cmd expands <c>%VAR%</c> when it PARSES a parenthesised block, so a wait
    ///     loop written as <c>if … ( set /A n+=1 &amp; if %n% GEQ N … )</c> compares
    ///     the value the counter had on entry — zero — on every iteration, forever.
    ///     The loop is therefore built from <c>goto</c>s and must contain no
    ///     parentheses at all.
    /// </summary>
    [Fact]
    public void Build_TheWaitLoopHasNoParenthesisedBlock()
    {
        var loop = Between(BuildDefault(), "wait_for_exit", "wait_timeout");

        Assert.DoesNotContain("(", loop);
        Assert.DoesNotContain(")", loop);
    }

    /// <summary>
    ///     On timeout the app is still holding its own DLLs, so a copy would half
    ///     succeed and leave the unbootable host/runtime mix <c>AppPayload</c>
    ///     exists to prevent. The honest move is to touch nothing, say so in the
    ///     log and let the next start offer to finish or discard.
    /// </summary>
    [Fact]
    public void Build_OnWaitTimeout_CopiesNothingAndDeletesNothing()
    {
        var branch = Between(BuildDefault(["old.dll"]), "wait_timeout", "app_exited");

        Assert.DoesNotContain("robocopy", branch);
        Assert.DoesNotContain("rd /S /Q", branch);
        Assert.DoesNotContain("start \"\"", branch);
        Assert.DoesNotContain(@"C:\Games\DotCharter\update-staging", branch);
        Assert.DoesNotContain(@"C:\Games\DotCharter\old.dll", branch);
        Assert.Contains($"exit /B {UpdaterScriptBuilder.ExitWaitTimedOut}", branch);
    }

    // ---- the copy ------------------------------------------------------------

    [Fact]
    public void Build_CopiesStagingOverInstallDirWithRobocopy()
    {
        var script = BuildDefault();

        Assert.Contains(
            "robocopy \"C:\\Games\\DotCharter\\update-staging\\DotCharter-v0.13.0-win-x64\" \"C:\\Games\\DotCharter\" /E",
            script);
        // robocopy: 0..7 are success codes, 8+ are failures.
        Assert.Contains("if %DCRC% GEQ 8 goto copy_failed", script);
    }

    /// <summary>
    ///     A failure that leaves nothing behind is undiagnosable, and that is
    ///     exactly how the field report arrived: staging populated, nothing copied,
    ///     no log, no marker, nothing to send. Every phase writes one line.
    /// </summary>
    [Fact]
    public void Build_LogsEveryPhaseToAFileBesideTheInstall()
    {
        var script = BuildDefault();

        Assert.Contains(@"set ""DCLOG=C:\Games\DotCharter\dotcharter-update.log""", script);
        // Truncating `>` for the first line, appending `>>` after: one file per
        // attempt, not an ever-growing one.
        Assert.Contains(">\"%DCLOG%\" echo [%DATE% %TIME%] DotCharter updater started.", script);
        Assert.Contains(">>\"%DCLOG%\" echo   waiting for pid 4242, giving up after 300 seconds", script);
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] app exited after %DCWAIT% seconds", script);
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] robocopy exit code %DCRC%", script);
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] update applied; restarting DotCharter.exe.", script);
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] COPY FAILED", script);
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] TIMEOUT", script);

        // robocopy's own summary lands in the same file — the failure detail is
        // worthless if only our one-liner survives.
        Assert.Contains("/NP >>\"%DCLOG%\" 2>&1", script);
    }

    /// <summary>
    ///     Redirection is written BEFORE the command so a message ending in a digit
    ///     cannot be parsed as a handle redirect (<c>echo done 1>>file</c> writes
    ///     "done" to handle 1). Every log line in the script must use that form.
    /// </summary>
    [Fact]
    public void Build_PutsRedirectionBeforeEveryEcho()
    {
        var script = BuildDefault();

        foreach (var line in script.Split("\r\n"))
        {
            if (!line.Contains("echo ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("@echo off", StringComparison.Ordinal)) continue;
            Assert.True(line.StartsWith('>'),
                $"log line must lead with its redirection: '{line}'");
        }
    }

    [Fact]
    public void Build_RestartsTheAppAndCleansUp()
    {
        var script = BuildDefault();

        Assert.Contains("start \"\" \"C:\\Games\\DotCharter\\DotCharter.exe\"", script);
        Assert.Contains("rd /S /Q \"C:\\Games\\DotCharter\\update-staging\"", script);
        Assert.Contains($"del /F /Q \"{Zip}\"", script);
        // Batch self-delete idiom — the script must remove itself last.
        Assert.Contains("(goto) 2>nul & del \"%~f0\"", script);
    }

    /// <summary>
    ///     The pending-apply record is what lets the next start offer to finish an
    ///     interrupted update. Removing it on any path other than a SUCCESSFUL copy
    ///     would throw away the recovery route the whole change exists for.
    /// </summary>
    [Fact]
    public void Build_ClearsThePendingRecordOnlyAfterASuccessfulCopy()
    {
        var script = BuildDefault();
        var state = @"C:\Games\DotCharter\dotcharter-update-pending.json";

        Assert.Contains($"del /F /Q \"{state}\"", script);

        var clear = script.IndexOf($"del /F /Q \"{state}\"", StringComparison.Ordinal);
        var failureBranch = script.IndexOf("if %DCRC% GEQ 8 goto copy_failed", StringComparison.Ordinal);
        Assert.True(clear > failureBranch, "the record may only be cleared past the copy-failure check");

        Assert.DoesNotContain(state, Between(script, "wait_timeout", "app_exited"));
        Assert.DoesNotContain(state, script[script.IndexOf("\r\n:copy_failed\r\n", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Build_IsPlainAsciiForCmdCompatibility()
    {
        var script = BuildDefault();

        Assert.All(script, c => Assert.True(c <= 127, $"non-ASCII char '{c}' in updater script"));
        Assert.StartsWith("@echo off", script);
    }

    [Fact]
    public void Build_TrimsTrailingBackslashesInsideQuotedPaths()
    {
        var script = BuildDefault();

        // A trailing backslash before a closing quote would escape it and
        // corrupt the whole robocopy command line — the generated script must
        // never contain the sequence backslash-quote.
        Assert.DoesNotContain("\\\"", script);
        Assert.Contains("\"C:\\Games\\DotCharter\"", script);
    }

    // ---- deletions (delta patches) -----------------------------------------

    /// <summary>
    ///     Without a deletions list nothing the app INSTALLED may be deleted. The
    ///     only <c>del</c> targets left are the updater's own scratch file, the
    ///     downloaded zip, the pending record and the script itself.
    /// </summary>
    [Fact]
    public void Build_WithoutDeletions_DeletesNothingItDidNotWriteItself()
    {
        Assert.Equal(
            new[]
            {
                "%DCPROBE%",
                "%DCPROBE%",
                Zip,
                @"C:\Games\DotCharter\dotcharter-update-pending.json",
                "%~f0",
            },
            DelTargets(BuildDefault()));
    }

    [Fact]
    public void Build_DeletesADroppedFileFromTheInstallDir()
    {
        var script = BuildDefault(["runtimes/win-x64/native/old.dll"]);

        Assert.Contains(
            "del /F /Q \"C:\\Games\\DotCharter\\runtimes\\win-x64\\native\\old.dll\"", script);
    }

    /// <summary>
    ///     robocopy 8+ means the copy failed and the OLD version is still
    ///     installed — deleting its files then would break a working install.
    /// </summary>
    [Fact]
    public void Build_OnlyDeletesAfterTheCopySucceeded()
    {
        var script = BuildDefault(["old.dll"]);

        var failureBranch = script.IndexOf("if %DCRC% GEQ 8 goto copy_failed", StringComparison.Ordinal);
        var deletion = script.IndexOf("del /F /Q \"C:\\Games\\DotCharter\\old.dll\"", StringComparison.Ordinal);

        Assert.True(failureBranch >= 0 && deletion > failureBranch,
            "deletions must be emitted after the robocopy failure check");
    }

    /// <summary>
    ///     The builder is the last place before these become shell arguments, so
    ///     it re-validates rather than trusting its caller.
    /// </summary>
    [Fact]
    public void Build_RefusesUnsafeDeletionsEvenWhenHandedThemDirectly()
    {
        var script = BuildDefault([
            "../../Windows/System32/kernel32.dll",
            "DotCharter.exe",
            "*.dll",
            "x\" & format C: & rem ",
        ]);

        Assert.DoesNotContain("kernel32", script);
        Assert.DoesNotContain("format C:", script);
        Assert.DoesNotContain("*.dll", script);
        // The exe is restarted, never deleted.
        Assert.DoesNotContain("del /F /Q \"C:\\Games\\DotCharter\\DotCharter.exe\"", script);
    }

    /// <summary>
    ///     <c>del /F /Q &lt;dir&gt;</c> deletes every FILE INSIDE that directory without
    ///     prompting — verified against real cmd.exe. On a portable install
    ///     ConfigRoot IS the install dir, so an entry naming <c>phrases</c>,
    ///     <c>profiles</c> or <c>checkpoints</c> would destroy the charter's riff
    ///     library, player profiles or snapshots in one line. Every deletion is
    ///     therefore guarded by a directory test.
    /// </summary>
    [Fact]
    public void Build_RefusesToDeleteADirectory()
    {
        var script = BuildDefault(["phrases", "runtimes/win-x64/old.dll"]);

        // "x\*" exists only for a directory with contents.
        Assert.Contains("if not exist \"C:\\Games\\DotCharter\\phrases\\*\" del /F /Q", script);
        Assert.Contains("if not exist \"C:\\Games\\DotCharter\\runtimes\\win-x64\\old.dll\\*\" del /F /Q", script);

        // Never an unguarded del of a payload path.
        Assert.DoesNotContain("\r\ndel /F /Q \"C:\\Games\\DotCharter\\phrases\"", script);
    }

    [Fact]
    public void Build_StaysAsciiAndQuoteSafeWithDeletions()
    {
        var script = BuildDefault(["a/b.dll", "c.dll"]);

        Assert.All(script, c => Assert.True(c <= 127, $"non-ASCII char '{c}' in updater script"));
        Assert.DoesNotContain("\\\"", script);
    }

    // ---- derived paths -------------------------------------------------------

    [Fact]
    public void ThePathHelpers_AgreeWithTheScriptTheyAreEmbeddedIn()
    {
        Assert.Equal(@"C:\Games\DotCharter\dotcharter-update.cmd", UpdaterScriptBuilder.LegacyScriptPathFor(Install + "\\"));
        Assert.Equal(@"C:\Games\DotCharter\dotcharter-update.log", UpdaterScriptBuilder.LogPathFor(Install + "\\"));
        Assert.Equal(@"C:\Games\DotCharter\update-staging", UpdaterScriptBuilder.StagingRootFor(Install));
        Assert.Equal(@"C:\Games\DotCharter\dotcharter-update-pending.json",
            UpdaterScriptBuilder.PendingStatePathFor(Install));
    }
}
