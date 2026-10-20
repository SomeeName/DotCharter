using System.Text.RegularExpressions;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     Static perf audit 2026-08-29 P0 #5 (and P0 #6, below): the two
///     playhead-driven controls and the composed present pump.
///     <para>
///         <c>LivePlaySectionGraph</c> repainted its whole phrase + section
///         strip on every <c>CurrentTimeMs</c> publish — up to 60 a second —
///         while <c>MinimapOverviewControl</c> had carried a 33 ms gate since
///         0.16. Both go through <see cref="RepaintThrottle" /> now; the struct
///         is tested with an injected clock and the two controls are source-
///         linted to use it on their playhead path.
///     </para>
/// </summary>
public sealed class RepaintThrottleTests
{
    [Fact]
    public void TheFirstTickPasses_ThenOnePerInterval()
    {
        var gate = new RepaintThrottle(33);

        Assert.True(gate.TryPass(1_000));
        Assert.False(gate.TryPass(1_010));
        Assert.False(gate.TryPass(1_032));
        Assert.True(gate.TryPass(1_033));
        Assert.False(gate.TryPass(1_050));
        Assert.True(gate.TryPass(1_100));
    }

    [Fact]
    public void SixtyPublishesASecond_BecomeAboutThirtyRepaints()
    {
        var gate = new RepaintThrottle(RepaintThrottle.DefaultIntervalMs);
        var repaints = 0;
        for (var tick = 0; tick < 60; tick++)
        {
            if (gate.TryPass(10_000 + tick * 1000 / 60))
                repaints++;
        }

        Assert.InRange(repaints, 28, 31);
    }

    [Fact]
    public void TheGate_AllocatesNothing()
    {
        var gate = new RepaintThrottle(33);
        var now = 5_000L;
        DotCharter.Tests.Infrastructure.AllocationProbe.AssertAllocationFree("RepaintThrottle.TryPass", () =>
        {
            now += 40;
            gate.TryPass(now);
        });
    }

    [Theory]
    [InlineData("LivePlaySectionGraph.cs", "private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)", "_playheadRepaint.TryPass(Environment.TickCount64)")]
    [InlineData("MinimapOverviewControl.cs", "private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)", "_dynamicRepaint.TryPass(Environment.TickCount64)")]
    public void ThePlayheadPath_GoesThroughTheThrottle(string file, string handler, string gate)
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", file))));
        Assert.Contains("new(RepaintThrottle.DefaultIntervalMs)", src);
        var body = Body(src, handler);
        Assert.Contains(gate, body);
        // No ungated repaint left on the PLAYHEAD path: from the CurrentTimeMs
        // check on, every InvalidateVisual / RequestRepaint sits behind the gate.
        // (The minimap's static-layer branch above that check repaints at once
        // on purpose — a lefty flip is not a playhead tick.)
        var playheadPath = body[body.IndexOf("nameof(ProjectState.CurrentTimeMs)", StringComparison.Ordinal)..];
        var repaints = Regex.Matches(playheadPath, @"(InvalidateVisual|RequestRepaint)\(\)");
        Assert.NotEmpty(repaints);
        foreach (Match m in repaints)
        {
            var line = playheadPath[..m.Index].Split('\n')[^1];
            Assert.Contains("TryPass(", line);
        }
    }

    // ── P0 #6: the composed present pump ─────────────────────────────────────

    /// <summary>
    ///     <c>ComposedPresentPump.OnFrameReady</c> posted a closure per frame —
    ///     a display class capturing (index, generation, timestamp), an
    ///     <c>Action</c> and the async-void state-machine box, at up to 144 Hz on
    ///     two surfaces. It enqueues a struct and posts ONE cached delegate now;
    ///     the queue keeps every frame's own record so two posts can never
    ///     present the same frame after a pool rebuild.
    /// </summary>
    [Fact]
    public void ThePresentPump_PostsOneCachedDelegate_AndKeepsAStructPerFrame()
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ComposedPresentPump.cs"))));
        var onFrame = Body(src, "private void OnFrameReady(int index, long generation)");
        Assert.DoesNotContain("=>", onFrame);
        Assert.Contains("_pending.Enqueue(new PendingFrame(index, generation, Stopwatch.GetTimestamp()));", onFrame);
        Assert.Contains("Dispatcher.UIThread.Post(_presentNext, DispatcherPriority.Render);", onFrame);

        Assert.Contains("private readonly record struct PendingFrame(int Index, long Generation, long SubmitTimestamp);", src);
        Assert.Contains("private readonly ConcurrentQueue<PendingFrame> _pending = new();", src);
        Assert.Contains("_presentNext = PresentNext;", src);
        var next = Body(src, "private void PresentNext()");
        Assert.Contains("_pending.TryDequeue(out var frame)", next);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
