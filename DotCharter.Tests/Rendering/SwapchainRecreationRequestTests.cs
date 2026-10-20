using System.Text.RegularExpressions;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>A deferred swapchain-recreation request must be CONSUMED before the
///     work, and only ever SET afterwards.</b>
///     <para>
///         <c>SetPresentMode</c>, <c>SetMsaaSamples</c>,
///         <c>SetRenderScalePercent</c> and <c>SetPostEffects</c> all run on the
///         UI thread, take <c>_renderLock</c> and set
///         <c>SwapchainNeedsRecreation = true</c>. <c>VulkanRenderer.Resize</c>
///         runs on the render thread and used to END with
///         <c>SwapchainNeedsRecreation = &lt;degenerate?&gt;</c> — an
///         unconditional write of <c>false</c> on the success path.
///     </para>
///     <para>
///         Between those two points <c>Resize</c> idles the device and rebuilds
///         every image resource, which is milliseconds wide. A request arriving in
///         that window was overwritten and LOST — and because none of those four
///         settings changes the surface SIZE, the engine's consume point
///         (<c>Fretboard3DEngine.RenderFrame</c>, which re-enters <c>Resize</c>
///         only on a size change or the flag) never came back for it. The setting
///         was silently never applied, with no log line. Most visible as a
///         render-scale slider whose final tick lands inside the last rebuild.
///     </para>
///     <para>
///         There is no way to unit-test the interleave without a device, so this
///         lints the SHAPE that makes it impossible: exactly one clear, at the
///         top, and every other write in the method sets <c>true</c>.
///     </para>
/// </summary>
public sealed class SwapchainRecreationRequestTests
{
    [Fact]
    public void ResizeClearsTheRequestOnceAndOnlyEverSetsItAfterwards()
    {
        var body = ResizeBody();

        var assignments = Regex.Matches(body, @"SwapchainNeedsRecreation\s*=\s*([^;]+);")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        // Guard against the lint measuring nothing if the method is renamed or
        // the flag moves: Resize must still touch it.
        Assert.NotEmpty(assignments);

        var clears = assignments.Where(v => v == "false").ToList();
        var sets = assignments.Where(v => v == "true").ToList();

        Assert.True(clears.Count == 1,
            $"Resize must consume the recreation request EXACTLY once. Found {clears.Count} clears " +
            $"among: {string.Join(" | ", assignments)}");

        Assert.True(clears.Count + sets.Count == assignments.Count,
            "Every SwapchainNeedsRecreation write in Resize must be a literal `true` or the single " +
            "leading `false`. A computed assignment (e.g. `= _composedPool.Count == 0`) clears the flag " +
            "on its success path and swallows any request the UI thread made during the rebuild. " +
            "Write `if (<degenerate>) SwapchainNeedsRecreation = true;` instead. Found: " +
            string.Join(" | ", assignments));
    }

    /// <summary>
    ///     …and the single clear must come FIRST. Consuming the request after the
    ///     rebuild is the original bug however it is spelled.
    /// </summary>
    [Fact]
    public void TheClearComesBeforeAnySet()
    {
        var body = ResizeBody();

        var clearAt = body.IndexOf("SwapchainNeedsRecreation = false;", StringComparison.Ordinal);
        var firstSetAt = body.IndexOf("SwapchainNeedsRecreation = true;", StringComparison.Ordinal);

        Assert.True(clearAt >= 0, "Resize no longer consumes the recreation request at all.");
        Assert.True(firstSetAt >= 0, "Resize no longer re-arms the flag for a degenerate result.");
        Assert.True(clearAt < firstSetAt,
            "The request must be consumed at the TOP of Resize, before the rebuild — clearing it " +
            "afterwards overwrites anything the UI thread requested during the rebuild.");
    }

    /// <summary>
    ///     The four request setters must keep taking the render lock: the clear
    ///     above takes it too, and that pairing is what makes the consume atomic
    ///     against a concurrent request.
    /// </summary>
    [Fact]
    public void EveryRequestSetterTakesTheRenderLock()
    {
        var source = RendererSource();

        foreach (var setter in new[]
                     { "SetPresentMode", "SetMsaaSamples", "SetRenderScalePercent", "SetPostEffects" })
        {
            var body = MethodBody(source, setter);
            Assert.True(body.Contains("lock (_renderLock)", StringComparison.Ordinal),
                $"{setter} sets SwapchainNeedsRecreation from the UI thread without _renderLock.");
        }
    }

    /// <summary>
    ///     <b><c>Resize</c> is the heaviest frame op there is, and it must run
    ///     ENTIRELY under <c>_renderLock</c>.</b>
    ///     <para>
    ///         Only the flag clear used to be locked. <c>DeviceWaitIdle</c>,
    ///         <c>RebuildFor</c>, <c>Recreate</c>/<c>RecreateComposed</c>/
    ///         <c>RecreateHeadless</c>, <c>RecreateRenderFinishedSemaphores</c>
    ///         and the <c>CurrentWidth</c>/<c>CurrentHeight</c> writes all ran
    ///         unlocked — while <c>Dispose()</c> takes that same lock and
    ///         destroys the swapchain manager, the pipelines and the context. A
    ///         <c>Dispose</c> from the UI thread landing inside a render-thread
    ///         <c>Resize</c> is a use-after-free.
    ///     </para>
    ///     <para>
    ///         It also falsified a written safety claim:
    ///         <c>docs/agents/rendering/vulkan-device-lifetime.md</c> says
    ///         "every <c>VulkanRenderer</c> frame op runs under
    ///         <c>lock (_renderLock)</c>".
    ///     </para>
    /// </summary>
    [Fact]
    public void TheWholeOfResizeRunsUnderTheRenderLock()
    {
        var body = ResizeBody();

        var locks = Regex.Matches(body, @"lock\s*\(\s*_renderLock\s*\)").ToList();
        Assert.True(locks.Count == 1,
            $"Resize must take _renderLock exactly ONCE, around its whole body. Found {locks.Count}.");

        // …and that one lock must be the FIRST statement, so nothing before it
        // touches the device. Everything ahead of it is guards on plain fields.
        var head = body[..locks[0].Index];
        Assert.DoesNotContain("_context.", head, StringComparison.Ordinal);
        Assert.DoesNotContain("_swapchainManager", head, StringComparison.Ordinal);
        Assert.DoesNotContain("_pipelineManager", head, StringComparison.Ordinal);

        // Nothing after the lock's closing brace either — i.e. the lock's block
        // reaches the end of the method.
        var afterLock = body[locks[0].Index..];
        var open = afterLock.IndexOf('{');
        var depth = 0;
        var close = -1;
        for (var i = open; i < afterLock.Length; i++)
        {
            if (afterLock[i] == '{')
            {
                depth++;
            }
            else if (afterLock[i] == '}' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        Assert.True(close > 0, "Unbalanced braces in Resize's lock block.");
        var tail = afterLock[(close + 1)..].Trim().TrimEnd('}').Trim();
        Assert.True(tail.Length == 0,
            $"Resize does work AFTER its render lock is released: {tail}");
    }

    /// <summary>
    ///     …and the disposal guard must come before anything that dereferences
    ///     a destroyed manager. <c>Dispose</c> nulls nothing — it destroys the
    ///     Vulkan objects and leaves the fields pointing at them — so
    ///     <c>_isDisposed</c> is the only thing standing between a late
    ///     <c>Resize</c> and freed memory.
    /// </summary>
    [Fact]
    public void ResizeRefusesOnceDisposed()
    {
        var body = ResizeBody();

        var guard = body.IndexOf("_isDisposed", StringComparison.Ordinal);
        Assert.True(guard >= 0,
            "Resize does not check _isDisposed. Dispose() destroys the swapchain manager, the " +
            "pipelines and the context under the same lock; a Resize that ran afterwards would " +
            "use them.");

        var firstApi = body.IndexOf("_context.Api", StringComparison.Ordinal);
        if (firstApi >= 0)
        {
            Assert.True(guard < firstApi,
                "Resize touches _context.Api before its _isDisposed guard.");
        }
    }

    private static string ResizeBody() => MethodBody(RendererSource(), "Resize");

    /// <summary>
    ///     The renderer's source with comments and string literals removed. The
    ///     comments in <c>Resize</c> QUOTE the old broken assignment to explain
    ///     the rule — a lint that reads prose fires on its own documentation.
    /// </summary>
    private static string RendererSource() => StripCommentsAndStrings(File.ReadAllText(RendererPath()));

    private static string StripCommentsAndStrings(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }

            if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }

                continue;
            }

            sb.Append(source[i]);
        }

        return sb.ToString();
    }

    /// <summary>Brace-matched body of the named method — crude, but these methods
    /// have no nested local functions and string literals with braces.</summary>
    private static string MethodBody(string source, string methodName)
    {
        var signature = Regex.Match(source, $@"public\s+(?:unsafe\s+)?\w[\w<>,\[\]\s]*\s{methodName}\s*\(");
        Assert.True(signature.Success, $"{methodName} not found in VulkanRenderer.cs — repoint this lint.");

        var open = source.IndexOf('{', signature.Index + signature.Length);
        Assert.True(open > 0);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"Unbalanced braces scanning {methodName}.");
        return string.Empty;
    }

    private static string RendererPath() => Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan", "VulkanRenderer.cs");
}
