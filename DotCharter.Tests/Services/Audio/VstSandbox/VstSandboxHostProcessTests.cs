using System.Diagnostics;
using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// Process-level integration tests: spawn the real <c>juce_vst_host.exe</c>,
/// drive the control pipe and the shared-memory audio bridge end-to-end
/// (no audio device, no plugin binaries required). Skipped when the native
/// host executable is not deployed (e.g. non-Windows CI).
/// </summary>
[SupportedOSPlatform("windows")]
public class VstSandboxHostProcessTests
{
    private static string RequireExe()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "VST sandbox is Windows-only.");
        var exe = VstSandboxHost.FindExecutable();
        Assert.SkipWhen(exe == null, "juce_vst_host.exe not deployed next to the test binaries.");
        return exe!;
    }

    [Fact]
    public async Task HandshakePingAndGracefulQuit()
    {
        RequireExe();
        using var host = new VstSandboxHost();
        var exitedRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Exited += requested => exitedRequested.TrySetResult(requested);

        await host.StartAsync(48000, 256);
        Assert.True(host.IsRunning);
        Assert.NotNull(host.ChildProcessId is int ? host.ChildProcessId : null);

        var pong = await host.SendAsync(new VstSandboxRequest { Op = "ping" }, TimeSpan.FromSeconds(10));
        Assert.True(pong.Ok);

        await host.StopAsync();
        var requested = await exitedRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(requested, "graceful quit must be reported as a requested exit");
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task BogusPluginLoadFailsWithoutKillingTheChild()
    {
        RequireExe();
        using var host = new VstSandboxHost();
        await host.StartAsync(48000, 256);

        var response = await host.SendAsync(new VstSandboxRequest
        {
            Op = "load",
            Slot = Guid.NewGuid().ToString(),
            Path = @"C:\definitely\not\a\plugin.vst3",
        }, TimeSpan.FromSeconds(30));

        Assert.False(response.Ok);
        Assert.False(string.IsNullOrEmpty(response.Error));
        Assert.True(host.IsRunning, "a failed load must not kill the sandbox");

        // Still responsive afterwards.
        var pong = await host.SendAsync(new VstSandboxRequest { Op = "ping" }, TimeSpan.FromSeconds(10));
        Assert.True(pong.Ok);

        await host.StopAsync();
    }

    [Fact]
    public async Task SharedMemoryBlockRoundTripsWithEmptyChain()
    {
        RequireExe();
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "windows only");
        using var host = new VstSandboxHost();
        using var shm = new VstSandboxSharedMemory(
            host.ShmName, host.RequestEventName, host.ResponseEventName, sampleRate: 48000);

        await host.StartAsync(48000, 256);

        const int frames = 256;
        var left = new float[frames];
        var right = new float[frames];
        for (var i = 0; i < frames; i++) { left[i] = i * 0.001f; right[i] = -i * 0.001f; }
        var leftCopy = (float[])left.Clone();
        var rightCopy = (float[])right.Clone();

        // The child's audio worker answers every sequence bump. With an
        // empty chain it reports chainActive=false (parent keeps its dry
        // buffer — passthrough semantics).
        var responded = false;
        for (var attempt = 0; attempt < 50 && !responded; attempt++)
            responded = shm.ProcessBlock(left, right, frames, null, 0, out _, out _, timeoutMs: 200);
        Assert.True(responded, "child never answered a shared-memory block request");

        var ok = shm.ProcessBlock(left, right, frames, null, 0,
                                  out var chainActive, out var instrumentActive, timeoutMs: 500);
        Assert.True(ok);
        Assert.False(chainActive);
        Assert.False(instrumentActive);
        Assert.Equal(leftCopy, left);
        Assert.Equal(rightCopy, right);

        // Heartbeat must advance while the worker is alive.
        var hb1 = shm.Heartbeat;
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.NotEqual(hb1, shm.Heartbeat);

        await host.StopAsync();
    }

    [Fact]
    public async Task HardKillIsReportedAsCrashAndBlocksTimeOutDry()
    {
        RequireExe();
        using var host = new VstSandboxHost();
        using var shm = new VstSandboxSharedMemory(
            host.ShmName, host.RequestEventName, host.ResponseEventName, sampleRate: 48000);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Exited += requested => exited.TrySetResult(requested);

        await host.StartAsync(48000, 256);
        Assert.True(host.ChildProcessId.HasValue);

        // Simulate an external plugin crash: hard-kill the child directly
        // (equivalent to `taskkill /f /pid <child>`), NOT via host.Kill()
        // which would mark the exit as requested.
        using (var child = Process.GetProcessById(host.ChildProcessId!.Value))
        {
            child.Kill();
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var requested = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(requested, "an external kill must surface as a crash (unrequested exit)");

        // The audio side must degrade to dry passthrough: the block request
        // times out but never throws or blocks indefinitely.
        var left = new float[128];
        var right = new float[128];
        var sw = Stopwatch.StartNew();
        var ok = shm.ProcessBlock(left, right, 128, null, 0, out _, out _, timeoutMs: 100);
        sw.Stop();
        Assert.False(ok, "a dead child cannot answer block requests");
        Assert.True(sw.ElapsedMilliseconds < 2000, "timeout must be bounded");
    }
}
