using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Regression cover for the ffmpeg deadlock: a redirected child whose pipes
///     are never read blocks on a full (~4 KB) buffer while the parent blocks in
///     WaitForExit. On the save path that wedged the save worker permanently.
/// </summary>
public sealed class MediaProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dotcharter_mediaproc_" + Guid.NewGuid().ToString("N"));

    public MediaProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string WriteScript(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Run_ChildFloodingBothPipes_CompletesInsteadOfDeadlocking()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        // Far more than the ~4 KB pipe buffer on BOTH streams: undrained, this
        // never returns.
        var script = WriteScript("flood.cmd",
            """
            @echo off
            for /L %%i in (1,1,600) do echo AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            for /L %%i in (1,1,600) do echo BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB 1>&2
            exit /b 0
            """);

        // A DEADLOCK DETECTOR, not a budget. The claim is "an undrained pipe
        // blocks FOREVER", so the only falsifying observation is that the call
        // returns with both pipes complete — no threshold between "a while" and
        // "infinity" means anything. It used to pass 30 s here AND assert
        // `elapsed < 30 s`, i.e. the wall-clock assertion the root AGENTS.md
        // forbids in the default gate: on a box running eight agents, starting
        // cmd.exe and moving 1,200 lines through two pipes overran it, and the
        // gate reported the deadlock regression that had not happened.
        var result = MediaProcess.Run(script, [], TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, $"exit={result.ExitCode}");
        Assert.Equal(0, result.ExitCode);
        // Both pipes drained WHOLE — a stronger claim than "contains BBBB", and
        // the one that fails if either drain is truncated or dropped.
        Assert.Equal(600, CountLines(result.StandardError, "BBBB"));
        Assert.Equal(600, CountLines(result.StandardOutput, "AAAA"));
    }

    private static int CountLines(string text, string needle) =>
        text.Split('\n').Count(l => l.Contains(needle, StringComparison.Ordinal));

    [Fact]
    public void Run_ChildThatNeverExits_IsKilledAndReportsNoExitCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        var script = WriteScript("hang.cmd",
            """
            @echo off
            ping -n 60 127.0.0.1 >nul
            exit /b 0
            """);

        var result = MediaProcess.Run(script, [], TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // A null exit code IS "the timeout fired and the tree was killed" — the
        // child pings for 60 s, so it could not have exited on its own. The
        // `elapsed < 20 s` that stood here added nothing to that and was a
        // wall-clock assertion in the default gate.
        Assert.Null(result.ExitCode);
        Assert.False(result.Succeeded);
    }

    /// <summary>
    ///     A cancelled Run kills the tree and returns promptly, instead of holding
    ///     the caller for the rest of the wall-clock timeout.
    ///     <para>This is what made the Cancel button on the status pill real during
    ///     the ONNX backend's transcode pre-pass: <c>TranscodeTimeout</c> is FIVE
    ///     MINUTES, and Run had no token at all, so a wedged ffmpeg left the user
    ///     pressing a dead affordance for up to that long.</para>
    ///     <para>No wall-clock assertion (root AGENTS.md). The falsifying evidence
    ///     is a MARKER the child writes only AFTER its sleep: if the tree was really
    ///     killed the marker can never appear, and if the kill were dropped the call
    ///     would simply wait the child out and find it there. That distinguishes
    ///     "cancelled" from "eventually finished" without any threshold.</para>
    /// </summary>
    [Fact]
    public void Run_CancelledMidFlight_KillsTheTreeAndThrows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        var marker = Path.Combine(_dir, "survived.txt");
        var script = WriteScript("cancelme.cmd",
            $"""
            @echo off
            ping -n 60 127.0.0.1 >nul
            echo survived > "{marker}"
            exit /b 0
            """);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaProcess.Run(script, [], TimeSpan.FromMinutes(5), cts.Token));
        Assert.False(File.Exists(marker),
            "the child outlived its cancellation — the process tree was not killed.");
    }

    /// <summary>An already-cancelled token never starts the child at all.</summary>
    [Fact]
    public void Run_WithAnAlreadyCancelledToken_StartsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        var marker = Path.Combine(_dir, "it-ran.txt");
        var script = WriteScript("marker.cmd",
            $"""
            @echo off
            echo ran > "{marker}"
            exit /b 0
            """);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaProcess.Run(script, [], TimeSpan.FromMinutes(5), cts.Token));
        Assert.False(File.Exists(marker));
    }

    /// <summary>The default (no token) behaviour is unchanged — every other caller
    /// in the app passes nothing.</summary>
    [Fact]
    public void Run_WithNoToken_StillCompletesNormally()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        var script = WriteScript("ok.cmd",
            """
            @echo off
            echo hello
            exit /b 0
            """);

        // The MISSING token is the subject of this case, so the xunit analyzer's
        // "always pass TestContext.Current.CancellationToken" rule is suppressed
        // here on purpose: passing one would test the other overload.
#pragma warning disable xUnit1051
        var result = MediaProcess.Run(script, [], TimeSpan.FromSeconds(60));
#pragma warning restore xUnit1051

        Assert.True(result.Succeeded);
        Assert.Equal("hello", result.StandardOutput.Trim());
    }

    [Fact]
    public void Run_NonZeroExit_IsReportedWithStderr()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a .cmd script.");

        var script = WriteScript("fail.cmd",
            """
            @echo off
            echo boom 1>&2
            exit /b 3
            """);

        var result = MediaProcess.Run(script, [], TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.StandardError);
    }
}
