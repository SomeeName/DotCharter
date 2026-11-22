using System.Text.RegularExpressions;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

public sealed class UpdaterScriptBuilderTests
{
    [Fact]
    public void Build_ConfinesMetacharactersInPathsToQuotedArguments()
    {
        const string folder = @"C:\X&calc&rem X^(!name!)100%PATH%";
        const string escaped = @"C:\X&calc&rem X^(!name!)100%%PATH%%";
        var script = UpdaterScriptBuilder.Build(4242, folder + "\\payload", folder + "\\staging",
            folder, "Dot&Charter!.exe", folder + "\\update.zip");

        Assert.Contains("setlocal EnableExtensions DisableDelayedExpansion\r\n", script);
        Assert.Contains($"echo   payload  \"{escaped}\\payload\"\r\n", script);
        Assert.Contains($"echo   install  \"{escaped}\"\r\n", script);
        Assert.Contains("restarting \"Dot&Charter!.exe\".", script);
        Assert.Contains("kept in \"%DCBACKUP%\";", script);
        Assert.Contains($"robocopy \"{escaped}\\payload\" \"{escaped}\"", script);
        Assert.Contains($"start \"\" \"{escaped}\\Dot&Charter!.exe\"", script);
        Assert.Contains($"del /F /Q \"{escaped}\\update.zip\"", script);
        Assert.DoesNotContain("call set", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad\"&calc&rem ")]
    [InlineData("bad\r\ncalc")]
    [InlineData("bad\0path")]
    public void Build_RefusesPathsThatCanBreakOutOfAQuotedArgument(string unsafePath)
    {
        Assert.Throws<ArgumentException>(() => UpdaterScriptBuilder.Build(4242,
            unsafePath, @"C:\staging", Install, "DotCharter.exe", Zip));
        Assert.Throws<ArgumentException>(() => UpdaterScriptBuilder.Build(4242,
            @"C:\payload", @"C:\staging", Install, unsafePath, Zip));
        Assert.Throws<ArgumentException>(() => UpdaterScriptBuilder.Build(4242,
            @"C:\payload", @"C:\staging", Install, "DotCharter.exe", unsafePath));
    }

    private const string Install = @"C:\Games\DotCharter";
    private const string Zip = @"C:\Users\Test\AppData\Roaming\DotCharter\updates\DotCharter-v0.13.0-win-x64.zip";
    private static readonly TimeSpan ScriptDeadlockCap = TimeSpan.FromMinutes(10);

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

    private static void AssertScriptExit(int expected, int actual, Func<string> readLog)
    {
        if (actual == expected) return;
        Assert.True(actual == expected, $"expected exit {expected}, actual exit {actual}\n{readLog()}");
    }

    private static string ReadScriptLog(string install)
    {
        try
        {
            using var stream = new FileStream(UpdaterScriptBuilder.LogPathFor(install), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"updater log unavailable: {ex.Message}";
        }
    }

    [Fact]
    public void Diagnostics_MatchingExit_DoesNotReadLog()
        => AssertScriptExit(1, 1, () => throw new InvalidOperationException("a passing assertion must not read the log"));

    [Fact]
    public void Diagnostics_WrongExit_IncludesLogWhileWriterHoldsIt()
    {
        var install = Path.Combine(Path.GetTempPath(), "dc-updater-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        try
        {
            var logPath = UpdaterScriptBuilder.LogPathFor(install);
            File.WriteAllText(logPath, "copy failed: diagnostic detail");
            using var writer = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read);

            var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertScriptExit(1, 3, () => ReadScriptLog(install)));

            Assert.Contains("expected exit 1, actual exit 3", failure.Message);
            Assert.Contains("copy failed: diagnostic detail", failure.Message);
        }
        finally
        {
            Directory.Delete(install, true);
        }
    }

    [Fact]
    public void Diagnostics_WrongExit_WithExclusiveLog_PreservesAssertionFailure()
    {
        var install = Path.Combine(Path.GetTempPath(), "dc-updater-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        try
        {
            using var lockedLog = new FileStream(UpdaterScriptBuilder.LogPathFor(install), FileMode.Create, FileAccess.Write, FileShare.None);

            var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertScriptExit(1, 3, () => ReadScriptLog(install)));

            Assert.Contains("expected exit 1, actual exit 3", failure.Message);
            Assert.Contains("updater log unavailable", failure.Message);
        }
        finally
        {
            Directory.Delete(install, true);
        }
    }

    [Fact]
    public void Diagnostics_WrongExit_WithMissingLog_PreservesAssertionFailure()
    {
        var install = Path.Combine(Path.GetTempPath(), "dc-updater-log-" + Guid.NewGuid().ToString("N"));

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertScriptExit(1, 3, () => ReadScriptLog(install)));

        Assert.Contains("expected exit 1, actual exit 3", failure.Message);
        Assert.Contains("updater log unavailable", failure.Message);
    }

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

    [Fact]
    public void Build_PayloadCopy_IncludesSameTweakedAndModifiedFiles()
    {
        var copy = Between(BuildDefault(), "app_exited", "copy_failed")
            .Split("\r\n").Single(line => line.StartsWith("robocopy ", StringComparison.Ordinal));

        Assert.Contains(" /IS ", copy);
        Assert.Contains(" /IT ", copy);
        Assert.Contains(" /IM ", copy);
    }

    [Fact]
    public void Build_RestoreCopy_IncludesSameTweakedAndModifiedFiles()
    {
        var restore = Between(BuildDefault(), "copy_failed", "restore_failed");
        var copy = restore.Split("\r\n").Single(line => line.StartsWith("robocopy ", StringComparison.Ordinal));

        Assert.Contains(" /IS ", copy);
        Assert.Contains(" /IT ", copy);
        Assert.Contains(" /IM ", copy);
        Assert.Contains("if %DCRRC% GEQ 8 goto restore_failed", restore);
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
        Assert.Contains(">>\"%DCLOG%\" echo [%TIME%] update applied; restarting \"DotCharter.exe\".", script);
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
                "%DCLIST%",
                "%DCNEW%",
                Zip,
                @"C:\Games\DotCharter\dotcharter-update-pending.json",
                "%~f0",
                // The failure path: the files the update ADDED (listed by the backup
                // pass) are removed again, then the scratch lists.
                @"C:\Games\DotCharter\%%~N",
                "%DCNEW%",
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

    // ---- #381: non-ASCII and percent signs in paths --------------------------

    /// <summary>
    ///     The script used to be written with <c>Encoding.ASCII</c>, which turns every
    ///     non-ASCII character of a path into '?'. It is now UTF-8 behind a
    ///     <c>chcp 65001</c> line, and a literal percent sign is doubled so cmd does
    ///     not expand it.
    /// </summary>
    [Fact]
    public void CreateScriptFile_RoundTripsANonAsciiPathAndEscapesPercent()
    {
        const string folder = "C:\\Users\\M\u00fcller \u65e5\u672c\\100%\\DotCharter";
        var script = UpdaterScriptBuilder.Build(
            4242, folder + "\\update-staging\\p", folder + "\\update-staging", folder, "DotCharter.exe", Zip);
        var dir = Path.Combine(Path.GetTempPath(), "dc-updater-enc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = UpdaterScriptBuilder.CreateScriptFile(dir, script);
            var bytes = File.ReadAllBytes(path);

            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM before @echo off");
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            const string escaped = "C:\\Users\\M\u00fcller \u65e5\u672c\\100%%\\DotCharter";
            Assert.Contains($"set \"DCLOG={escaped}\\dotcharter-update.log\"", text);
            Assert.Contains($"\"{escaped}\\DotCharter.exe\"", text);

            // The code page switch precedes every non-ASCII byte.
            var lines = text.Split("\r\n");
            Assert.Equal("@echo off", lines[0]);
            Assert.Equal("chcp 65001 >nul", lines[1]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---- #382: back up before the copy, restore on failure -------------------

    [Fact]
    public void Build_BacksUpWhatThePayloadReplaces_BeforeTheCopy()
    {
        var script = BuildDefault();

        var backup = script.IndexOf("call :backup_one", StringComparison.Ordinal);
        var copy = script.IndexOf("robocopy \"C:\\Games\\DotCharter\\update-staging", StringComparison.Ordinal);
        var check = script.IndexOf("if %DCBKFAIL% EQU 1 goto backup_failed", StringComparison.Ordinal);
        Assert.True(backup > 0 && check > backup && copy > check, "the backup loop and its check must run before the payload copy");
        Assert.Contains("set \"DCBACKUP=C:\\Games\\DotCharter\\update-backup\"", script);
    }

    [Fact]
    public void Build_OnCopyFailure_RestoresTheBackupAndTellsTheTruth()
    {
        var script = BuildDefault();
        var branch = script[script.IndexOf("\r\n:copy_failed\r\n", StringComparison.Ordinal)..];
        var failed = Between(script, "copy_failed", "restore_failed");

        Assert.Contains("robocopy \"%DCBACKUP%\" \"C:\\Games\\DotCharter\" /E", failed);
        Assert.Contains("RESTORED", failed);
        Assert.Contains("%%~N", failed); // files the update added are removed again
        Assert.DoesNotContain("left on the old version", branch);
        Assert.Contains("RESTORE FAILED - the install is a MIX", branch);
        Assert.Contains($"exit /B {UpdaterScriptBuilder.ExitRestoreFailed}", branch);
        // The pending state is only ever deleted on the success path.
        Assert.DoesNotContain("dotcharter-update-pending.json", branch);
        // A restore that failed keeps the backup.
        Assert.DoesNotContain("rd /S /Q", Between(script, "restore_failed", "backup_failed"));
        // A successful run cleans the backup up.
        var success = script[..script.IndexOf("\r\n:copy_failed\r\n", StringComparison.Ordinal)];
        Assert.Contains("rd /S /Q \"%DCBACKUP%\"", success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedScript_WithSameSizeAndTimestamp_ReplacesTheInstalledContent(bool modifiedChangeTime)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "dc-updater-run-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(install, "update-staging");
        var payload = Path.Combine(staging, "p");
        try
        {
            Directory.CreateDirectory(payload);
            await WriteSameMetadataPairAsync(install, payload, "a.txt", "old-a", "new-a", modifiedChangeTime);
            File.Copy(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), Path.Combine(install, "DotCharter.exe"));
            var script = UpdaterScriptBuilder.Build(
                999999, payload, staging, install, "DotCharter.exe", Path.Combine(root, "u.zip"));
            var scriptPath = UpdaterScriptBuilder.CreateScriptFile(Path.Combine(root, "scripts"), script);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
            };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(scriptPath);
            using var process = System.Diagnostics.Process.Start(psi)!;
            try
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(ScriptDeadlockCap, TestContext.Current.CancellationToken);
                AssertScriptExit(0, process.ExitCode, () => ReadScriptLog(install));
                Assert.Equal("new-a", File.ReadAllText(Path.Combine(install, "a.txt")));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            await CleanScriptInstallAsync(root, install);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileFixture_EqualSizeAndWriteTime_HasTheRequestedChangeTimeClass(bool modifiedChangeTime)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        Directory.CreateDirectory(payload);

        await WriteSameMetadataPairAsync(temp.Path, payload, "a.txt", "old-a", "new-a", modifiedChangeTime);

        Assert.Equal("old-a", File.ReadAllText(Path.Combine(temp.Path, "a.txt")));
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(payload, "a.txt")));
    }

    private static async Task WriteSameMetadataPairAsync(
        string install, string payload, string name, string oldContent, string newContent, bool modifiedChangeTime)
    {
        var installedFile = Path.Combine(install, name);
        var payloadFile = Path.Combine(payload, name);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(installedFile, oldContent);
        File.WriteAllText(payloadFile, newContent);
        File.SetLastWriteTimeUtc(installedFile, timestamp);
        File.SetLastWriteTimeUtc(payloadFile, timestamp);
        await SetCopyClassAsync(installedFile, payloadFile, modifiedChangeTime);
        Assert.Equal(new FileInfo(installedFile).Length, new FileInfo(payloadFile).Length);
        Assert.Equal(File.GetLastWriteTimeUtc(installedFile), File.GetLastWriteTimeUtc(payloadFile));
        Assert.NotEqual(oldContent, newContent);
    }

    private static async Task SetCopyClassAsync(string installedFile, string sourceFile, bool modifiedChangeTime)
    {
        var changeTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        UpdaterFileMetadata.SetChangeTime(installedFile, changeTime);
        UpdaterFileMetadata.SetChangeTime(sourceFile, changeTime);
        Assert.Equal(changeTime, UpdaterFileMetadata.GetChangeTime(sourceFile));
        Assert.Equal(changeTime, UpdaterFileMetadata.GetChangeTime(installedFile));
        if (modifiedChangeTime)
        {
            // This real gap prepares distinct NTFS change times in the fixture; it
            // never synchronizes the product. Last-write time stays equal afterwards.
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var lastWriteTime = File.GetLastWriteTimeUtc(installedFile);
            File.WriteAllText(installedFile, File.ReadAllText(installedFile));
            File.SetLastWriteTimeUtc(installedFile, lastWriteTime);
            Assert.NotEqual(UpdaterFileMetadata.GetChangeTime(sourceFile), UpdaterFileMetadata.GetChangeTime(installedFile));
        }

        Assert.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(installedFile));
    }

    /// <summary>
    ///     Runs the generated script against a temp install in which one payload target
    ///     is locked: robocopy replaces the first file, fails on the locked one, and the
    ///     script must put every install file back to its old bytes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedScript_WithALockedFile_RestoresTheOldInstall(bool modifiedChangeTime)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "dc-updater-run-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(install, "update-staging");
        var payload = Path.Combine(staging, "p");
        try
        {
            Directory.CreateDirectory(payload);
            await WriteSameMetadataPairAsync(install, payload, "a.txt", "old-a", "new-a", modifiedChangeTime);
            await WriteSameMetadataPairAsync(install, payload, "b.txt", "old-b", "new-b", modifiedChangeTime);
            File.WriteAllText(Path.Combine(payload, "c.txt"), "new-c");
            File.Copy(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), Path.Combine(install, "DotCharter.exe"));
            var script = UpdaterScriptBuilder.Build(
                999999, payload, staging, install, "DotCharter.exe", Path.Combine(root, "u.zip"));
            // /IS /IT /IM also restore the unchanged locked target. Pause at the failure label so
            // the test can release that lock before restore, without a race or a sleep.
            script = script.Replace("\r\n:copy_failed\r\n",
                "\r\n:copy_failed\r\necho restore-ready\r\nset /P DCRESTORE=\r\n", StringComparison.Ordinal);
            var scriptPath = UpdaterScriptBuilder.CreateScriptFile(Path.Combine(root, "scripts"), script);

            int exit;
            using (var lockedFile = new FileStream(Path.Combine(install, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = root,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(scriptPath);
                using var process = System.Diagnostics.Process.Start(psi)!;
                try
                {
                    var ready = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                        .AsTask().WaitAsync(ScriptDeadlockCap, TestContext.Current.CancellationToken);
                    Assert.Equal("restore-ready", ready);
                    Assert.Equal("new-a", File.ReadAllText(Path.Combine(install, "a.txt")));
                    Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(install, "update-backup", "a.txt")),
                        File.GetLastWriteTimeUtc(Path.Combine(install, "a.txt")));
                    // Pin the restore pair independently: robocopy changed a.txt's NTFS
                    // change time, and the plain-copy backup has its own change time too.
                    await SetCopyClassAsync(Path.Combine(install, "a.txt"),
                        Path.Combine(install, "update-backup", "a.txt"), modifiedChangeTime);
                    lockedFile.Dispose();
                    await process.StandardInput.WriteLineAsync("restore");
                    // Process exit is the rendezvous; the cap only detects a deadlock. It sits well above
                    // the script's own bounded waits (process wait, robocopy retries, restore), which
                    // took more than two minutes on a loaded machine.
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(ScriptDeadlockCap, TestContext.Current.CancellationToken);
                    exit = process.ExitCode;
                }
                catch (TimeoutException ex)
                {
                    var transcript = ReadScriptLog(install);
                    throw new TimeoutException($"the generated script did not finish:\n{transcript}", ex);
                }
                finally
                {
                    // Stop the script before releasing the lock or deleting its install, even on failure.
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                AssertScriptExit(UpdaterScriptBuilder.ExitCopyFailed, exit, () => ReadScriptLog(install));
            }

            Assert.Equal(UpdaterScriptBuilder.ExitCopyFailed, exit);
            Assert.Equal("old-a", File.ReadAllText(Path.Combine(install, "a.txt")));
            Assert.Equal("old-b", File.ReadAllText(Path.Combine(install, "b.txt")));
            Assert.False(File.Exists(Path.Combine(install, "c.txt")));
            var log = ReadScriptLog(install);
            Assert.True(log.Contains("RESTORED", StringComparison.Ordinal), log);
        }
        finally
        {
            await CleanScriptInstallAsync(root, install);
        }
    }

    private static async Task CleanScriptInstallAsync(string root, string install)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("DotCharter"))
        {
            try
            {
                if (!string.Equals(p.MainModule?.FileName, Path.Combine(install, "DotCharter.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                await p.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The restart may already be gone; an inaccessible process from another install is left alone.
            }
            finally { p.Dispose(); }
        }

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
