using System.Diagnostics;
using DotCharter.Services.Parsing.Audio;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// The stem encoder's and the audio converter's child-process runners, driven
/// against REAL child processes — the defects they pin (a pipe-buffer deadlock and
/// an un-killed orphan) exist only at the OS boundary and cannot be reproduced with
/// a fake. Windows-only by construction (they use <c>cmd.exe</c>); skipped
/// elsewhere. (The Python demucs probe this class also covered was deleted with
/// its never-registered backend, #662.)
/// </summary>
public sealed class StemToolChildProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dc-childproc-" + Guid.NewGuid().ToString("N"));

    public StemToolChildProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    /// S1e. Cancelling an encode must KILL the child — and the whole tree, since
    /// the tool is normally launched behind a shell. <c>using var proc</c>
    /// disposes only the managed WRAPPER, so the real process kept running and
    /// kept writing into a destination the caller had already abandoned.
    /// <para>
    /// Observed on the grandchild (<c>ping</c>) rather than on a marker file: it
    /// makes the assertion immediate when the kill works and unambiguous when it
    /// does not, with no dependence on shell quoting. The ping is given ~119 s of
    /// life rather than ~14: BOTH windows below have to sit comfortably inside
    /// it, and on a saturated box a 14 s child leaves no room for the one that
    /// waits for the grandchild to appear.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancellingAnEncode_KillsTheChildProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var before = PingProcessIds();

        using var cts = new CancellationTokenSource();
        // Start() runs synchronously before the first await, so the child tree is
        // already alive by the time the task handle is back.
        var run = FfmpegStemEncoder.RunProcessAsync("cmd.exe", ["/c", "ping -n 120 127.0.0.1"], cts.Token);

        // POLL for the ping grandchild rather than sleeping a fixed 600 ms for
        // it. The cap is a deadlock detector — reaching it only means the shell
        // never got that far, which the early return below already tolerates —
        // but a fixed sleep on a saturated box takes that early return far more
        // often than it should, and every one of those is a case that asserted
        // nothing while reporting PASSED.
        var spawnDeadline = Stopwatch.StartNew();
        HashSet<int> spawned;
        do
        {
            spawned = PingProcessIds();
            spawned.ExceptWith(before);
            if (spawned.Count > 0) break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        } while (spawnDeadline.Elapsed < TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        if (spawned.Count == 0) return; // the shell never got that far; nothing to assert on

        // A killed tree is gone within a moment. An UNkilled ping runs ~119 s
        // more, so it is still alive well past this window and the test cannot
        // pass by the child simply timing out.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && AnyAlive(spawned))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(AnyAlive(spawned),
            "The cancelled encode left its child process tree running — only the managed wrapper was disposed.");
    }

    /// <summary>
    ///     The same defect, one folder over and never swept:
    ///     <c>Parsing/Audio/AudioFormatConverter.RunProcess</c> — the runner
    ///     behind the vgmstream and ffmpeg fallbacks for a <c>.wem</c> — redirected
    ///     stdout AND stderr and drained NEITHER, then waited two minutes. A child
    ///     that fills the ~4 KB anonymous pipe blocks forever writing into it while
    ///     this side blocks forever in <c>WaitForExit</c>: the user sees a
    ///     two-minute freeze and then "conversion failed" on a machine where the
    ///     tool works. It routes through <c>MediaProcess.Run</c> now.
    ///     <para>
    ///         A DEADLINE, not a budget, for the reason spelled out on
    ///         <see cref="TheChildOutputIsDrainedIndependentlyOfTheCallerToken" />:
    ///         the claim is "an undrained pipe blocks forever", so the falsifying
    ///         observation is that the call returns at all with the right answer.
    ///     </para>
    /// </summary>
    [Fact]
    public void AConversionWhoseChildFloodsItsPipes_IsDrainedAndReaped()
    {
        if (!OperatingSystem.IsWindows()) return;

        // ~120 KB down each pipe, one run per pipe: the converter redirects BOTH,
        // so either one left undrained wedges the whole call on its own. (One
        // child writing to both would need cmd's `&`, whose precedence inside a
        // `for` body silently makes it 9,000,000 lines.)
        var line = new string('z', 40);

        Assert.True(
            AudioFormatConverter.RunProcess("cmd.exe", ["/c", $"for /L %i in (1,1,3000) do @echo {line}"]),
            "a child flooding STDOUT must be drained and reaped, not left blocked on a full pipe " +
            "until the two-minute ceiling kills it.");

        Assert.True(
            AudioFormatConverter.RunProcess("cmd.exe", ["/c", $"for /L %i in (1,1,3000) do @echo {line} 1>&2"]),
            "a child flooding STDERR must be drained too — vgmstream is the chatty one there.");
    }

    /// <summary>And a failing tool must still report failure, so the caller can
    /// fall through to the next decoder.</summary>
    [Fact]
    public void AConversionThatFails_ReportsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(AudioFormatConverter.RunProcess("cmd.exe", ["/c", "exit 3"]));
        Assert.True(AudioFormatConverter.RunProcess("cmd.exe", ["/c", "exit 0"]));
        // A missing executable is a normal outcome, not an exception.
        Assert.False(AudioFormatConverter.RunProcess(
            Path.Combine(_dir, "no-such-tool.exe"), ["--version"]));
    }

    private static HashSet<int> PingProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("PING"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    private static bool AnyAlive(HashSet<int> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (!p.HasExited) return true;
            }
            catch (ArgumentException) { /* already gone */ }
        }
        return false;
    }

    /// <summary>A normal (uncancelled) run still reports the child's exit code.</summary>
    [Fact]
    public async Task ANormalRun_ReportsTheChildExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.True(await FfmpegStemEncoder.RunProcessAsync(
            "cmd.exe", ["/c", "exit 0"], TestContext.Current.CancellationToken));
        Assert.False(await FfmpegStemEncoder.RunProcessAsync(
            "cmd.exe", ["/c", "exit 3"], TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The drains must not be cancelled by the caller's token: a still-writing
    /// child with nobody reading its pipe blocks forever. Proven by running a
    /// flooding child to completion while the token is live, then cancelling.
    /// </summary>
    [Fact]
    public async Task TheChildOutputIsDrainedIndependentlyOfTheCallerToken()
    {
        if (!OperatingSystem.IsWindows()) return;

        // ~120 KB of stdout from a shell loop — no file paths, so nothing here
        // depends on how the argument quoting survives cmd.exe.
        // A DEADLINE, not a budget: the claim is "an undrained pipe blocks
        // FOREVER", so the falsifying observation is that the call returns at
        // all. Cancelling after a generous margin proves that without asserting
        // how fast the machine is.
        //
        // It used to assert `sw.Elapsed < 20 s`, which is the wall-clock pattern
        // the root AGENTS.md forbids in the default gate — "they flake the moment
        // suites run in parallel". It did exactly that: 41 s under load, failing
        // the gate while its real assertion (below) passed. The stall it guards
        // against is unbounded, so no threshold between 20 s and infinity means
        // anything; only "did it finish" does.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var sw = Stopwatch.StartNew();
        var ok = await FfmpegStemEncoder.RunProcessAsync(
            "cmd.exe", ["/c", "for /L %i in (1,1,3000) do @echo " + new string('z', 40)], cts.Token);
        sw.Stop();

        Assert.True(ok, "A flooding child must be drained and reaped, not left blocked on a full pipe.");
        Assert.False(cts.IsCancellationRequested,
            $"The run stalled on a full pipe — it did not finish before the 2-minute deadline " +
            $"(elapsed {sw.Elapsed.TotalSeconds:0}s).");
    }
}
