using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>Every frame-loop Vulkan result must be CLASSIFIED, not compared
///     against one value.</b>
///     <para>
///         <c>grep -rn ErrorDeviceLost DotCharter.Rendering</c> used to return
///         zero hits. Each <c>WaitForFences</c> result was compared only against
///         <c>Result.Timeout</c>, so a <c>VK_ERROR_DEVICE_LOST</c> after a driver
///         reset (TDR) read as a SUCCESSFUL wait: the frame proceeded, the
///         acquire failed, <c>SwapchainNeedsRecreation</c> was set, and
///         <c>Resize</c> re-entered <c>DeviceWaitIdle</c> →
///         <c>CleanupImageResources</c> → <c>CreateSwapchain</c> on a dead device
///         forever, with no log line and no error surface — a permanently black
///         view.
///     </para>
///     <para>
///         There is no way to unit-test the device loss itself without a GPU
///         that can be reset, so this lints the SHAPE that makes the class of
///         bug impossible: every one of the frame-loop calls is an ARGUMENT
///         to <c>Check(...)</c>, which is the single place
///         <c>FrameResultPolicy.Classify</c> is consulted. (The decision table
///         itself is <c>FrameResultPolicyTests</c>.) Only <c>WaitForFences</c>
///         and <c>QueueSubmit</c> remain: the swapchain acquire/present went
///         with the native path, and <c>NoNativeSwapchainTests</c> keeps them
///         out.
///     </para>
/// </summary>
public sealed class DeviceLostHandlingTests
{
    [Theory]
    [InlineData("WaitForFences(")]
    [InlineData("QueueSubmit(")]
    public void EveryFrameLoopCallIsClassified(string call)
    {
        var source = RendererSource();

        var sites = Occurrences(source, call).ToList();
        Assert.True(sites.Count > 0,
            $"No {call} call found in VulkanRenderer.cs — this lint has gone vacuous; repoint it.");

        foreach (var at in sites)
        {
            var prefix = source[..at];
            // Walk back over the receiver expression (`_context.Api.`, …) and
            // any whitespace, then demand the wrapper.
            var i = prefix.Length - 1;
            while (i >= 0 && (char.IsLetterOrDigit(prefix[i]) || prefix[i] is '_' or '.')) i--;
            while (i >= 0 && char.IsWhiteSpace(prefix[i])) i--;

            var head = prefix[..(i + 1)];
            Assert.True(head.EndsWith("Check(", StringComparison.Ordinal),
                $"A {call} result in VulkanRenderer.cs is not routed through Check(...): " +
                $"…{Tail(head)}. Comparing a frame-loop result against a single value is how " +
                "VK_ERROR_DEVICE_LOST read as a successful wait and the renderer looped " +
                "recreating a swapchain on a dead device forever.");
        }
    }

    /// <summary>
    ///     <c>Check</c> must be the only classifier, and it must consult the
    ///     pure policy rather than growing its own comparisons.
    /// </summary>
    [Fact]
    public void CheckDelegatesToThePurePolicy()
    {
        var source = RendererSource();

        Assert.Contains("FrameResultPolicy.Classify(", source, StringComparison.Ordinal);
        Assert.Contains("FrameResultAction.DeviceLost", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     None of the four results may be compared against a bare
    ///     <c>Result.</c> value any more — that comparison IS the bug. (The
    ///     classification lives in <c>FrameResultPolicy</c>; the renderer only
    ///     ever switches on <c>FrameResultAction</c>.)
    /// </summary>
    [Fact]
    public void NoFrameLoopResultIsComparedAgainstASingleResultValue()
    {
        var body = RendererSource();

        // Any `xxxResult == Result.Yyy` / `!= Result.Yyy` in the frame loop.
        var offenders = Regex.Matches(body, @"\w*(?:[Ff]ence|[Rr]esult|[Aa]cquire|[Pp]resent|[Ss]ubmit)\w*\s*[!=]=\s*Result\.\w+")
            .Select(m => m.Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            "VulkanRenderer.cs still compares a frame-loop result against a single Result value: " +
            string.Join(" | ", offenders) +
            ". Route it through Check(...) / FrameResultPolicy instead.");
    }

    /// <summary>
    ///     …and the latch must actually STOP things. A lost device never heals:
    ///     <c>BeginFrame</c>, <c>EndFrame</c> and above all <c>Resize</c> must
    ///     refuse, or the deferred-recreation flag drives an infinite rebuild.
    /// </summary>
    [Theory]
    [InlineData("BeginFrameLocked", "private")]
    [InlineData("EndFrame", "public")]
    [InlineData("Resize", "public")]
    public void TheDeviceLostLatchStopsTheFrameLoop(string method, string accessibility)
    {
        var body = MethodBody(RendererSource(), method, accessibility);

        Assert.True(body.Contains("IsDeviceLost", StringComparison.Ordinal),
            $"{method} does not check IsDeviceLost — a dead device keeps being driven, and " +
            "Resize in particular loops recreating a swapchain on it forever.");
    }

    /// <summary>The host must be able to SEE it: a flag, a reason and a
    /// one-shot event, none of which existed.</summary>
    [Fact]
    public void TheDeviceLossIsSurfacedToTheHost()
    {
        var contract = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan", "IVulkanRenderService.cs"));

        Assert.Contains("bool IsDeviceLost", contract, StringComparison.Ordinal);
        Assert.Contains("string? DeviceLostReason", contract, StringComparison.Ordinal);
        Assert.Contains("event Action<string>? DeviceLost", contract, StringComparison.Ordinal);

        // …and it is LOGGED, not merely flagged. Debug.WriteLine is compiled
        // out of Release, which is why the original had no diagnostic at all.
        var source = RendererSource();
        var latch = MethodBody(source, "LatchDeviceLost", "private");
        Assert.Contains("Console.Error.WriteLine", latch, StringComparison.Ordinal);
    }

    private static string Tail(string s) => s.Length <= 80 ? s : s[^80..];

    private static IEnumerable<int> Occurrences(string haystack, string needle)
    {
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            yield return at;
            at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal);
        }
    }

    /// <summary>The renderer's source with comments and string literals removed
    /// — the comments QUOTE the old broken comparisons to explain the rule, and
    /// a lint that reads prose fires on its own documentation.</summary>
    private static string RendererSource() => StripCommentsAndStrings(File.ReadAllText(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan", "VulkanRenderer.cs")));

    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
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

    private static string MethodBody(string source, string methodName, string accessibility = "public")
    {
        var signature = Regex.Match(source, $@"{accessibility}\s+(?:unsafe\s+)?\w[\w<>,\[\]\?\s]*\s{methodName}\s*\(");
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
}
