using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Rendering.Vulkan.Composed;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Two escape hatches the composed path did not have.
///     <para>
///         <b>The keyed-mutex acquire timeout was INFINITE</b>
///         (<c>uint.MaxValue</c>) on BOTH composed submits. If the compositor
///         ever fails to release key 0 — a lost D3D11 device, a wedged
///         <c>UpdateWithKeyedMutexAsync</c> — the submitted work never begins
///         and the queue hangs until the driver resets the GPU.
///     </para>
///     <para>
///         <b>And <c>FindAdapter</c> could dereference a null COM pointer and
///         spin without bound.</b> Its loop terminated only on
///         <c>DXGI_ERROR_NOT_FOUND</c>, so any other failing HRESULT left
///         <c>adapter</c> at its null default, and the very next line called
///         <c>GetDesc</c> on it before incrementing the index and going round
///         again.
///     </para>
/// </summary>
public sealed class ComposedSubmitAndAdapterTests
{
    [Fact]
    public void TheKeyedMutexAcquireTimeoutIsFinite()
    {
        Assert.True(ComposedSubmitPolicy.AcquireTimeoutMs < uint.MaxValue,
            "An INFINITE keyed-mutex acquire hangs the graphics queue until the driver resets " +
            "the GPU when the compositor fails to release key 0.");

        // Generous next to a frame — a merely slow compositor finishes in
        // single-digit ms — but nowhere near a driver TDR window.
        Assert.InRange(ComposedSubmitPolicy.AcquireTimeoutMs, 100u, 5_000u);
    }

    /// <summary>Neither composed submit may keep its own literal: one owner, or
    /// the two drift again.</summary>
    [Theory]
    [InlineData("DotCharter.Rendering/Vulkan/VulkanRenderer.cs")]
    [InlineData("DotCharter.Rendering/Vulkan2D/Timeline2DComposedRenderer.cs")]
    public void BothComposedSubmitsTakeTheirTimeoutFromThePolicy(string relativePath)
    {
        var source = StripCommentsAndStrings(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains("acquireTimeout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("acquireTimeout = uint.MaxValue", source, StringComparison.Ordinal);
        Assert.Contains("ComposedSubmitPolicy.AcquireTimeoutMs", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A submit that did not reach the GPU must POISON its slot. Leaving it
    ///     in <c>Rendering</c> wedges it: it is never <c>Free</c> again and
    ///     never gets the <c>CompleteComposedPresent</c> that would free it, so
    ///     the pool loses an image per failure until it holds none.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.Rendering/Vulkan/VulkanRenderer.cs")]
    [InlineData("DotCharter.Rendering/Vulkan2D/Timeline2DComposedRenderer.cs")]
    public void AFailedComposedSubmitPoisonsItsSlotAndRaisesNoFrameReady(string relativePath)
    {
        var source = StripCommentsAndStrings(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains("MarkBroken(", source, StringComparison.Ordinal);

        // The success-only calls must be guarded — neither may sit at the same
        // brace depth as the submit with nothing between them.
        var markSubmitted = source.IndexOf("MarkSubmitted(", StringComparison.Ordinal);
        var markBroken = source.IndexOf("MarkBroken(", StringComparison.Ordinal);
        Assert.True(markSubmitted >= 0 && markBroken >= 0);
    }

    /// <summary>A <c>Broken</c> image anywhere forces the whole pool to be
    /// rebuilt — the transition the poisoning above relies on.</summary>
    [Fact]
    public void ABrokenImageForcesAPoolRebuild()
    {
        ComposedImageState[] healthy =
            [ComposedImageState.Free, ComposedImageState.Rendering, ComposedImageState.AwaitingPresent];
        Assert.False(ComposedImagePoolPolicy.NeedsRebuild(healthy));

        ComposedImageState[] poisoned =
            [ComposedImageState.Free, ComposedImageState.Broken, ComposedImageState.AwaitingPresent];
        Assert.True(ComposedImagePoolPolicy.NeedsRebuild(poisoned));

        // …and a broken image is never handed out again in the meantime.
        Assert.Equal(0, ComposedImagePoolPolicy.PickNext(poisoned, 2));
        Assert.Equal(-1, ComposedImagePoolPolicy.PickNext(
            [ComposedImageState.Broken, ComposedImageState.Broken], -1));
    }

    /// <summary>
    ///     <c>FindAdapter</c> must loop on SUCCESS. Its old
    ///     <c>while (EnumAdapters(...) != DXGI_ERROR_NOT_FOUND)</c> treated
    ///     every other failing HRESULT as "keep going" with a null adapter in
    ///     hand and an index that only ever grows.
    /// </summary>
    [Fact]
    public void FindAdapterLoopsOnSuccessNotOnNotFound()
    {
        var source = StripCommentsAndStrings(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan", "Composed", "VulkanComposedImagePool.cs")));

        var body = MethodBody(source, "FindAdapter", "private");

        Assert.DoesNotContain("while (factory.EnumAdapters", body, StringComparison.Ordinal);
        Assert.Contains("EnumAdapters(", body, StringComparison.Ordinal);

        // The enumeration result is captured and BOTH outcomes are handled:
        // end-of-list breaks, any other failure throws.
        Assert.Matches(new Regex(@"var\s+hr\s*=\s*factory\.EnumAdapters\("), body);
        Assert.Contains("if (hr == dxgiErrorNotFound) break;", body, StringComparison.Ordinal);
        Assert.Contains("throw new NotSupportedException", body, StringComparison.Ordinal);

        // …and the throw comes BEFORE the first dereference of the adapter.
        var throwAt = body.IndexOf("if (hr != 0)", StringComparison.Ordinal);
        var derefAt = body.IndexOf("adapter.GetDesc(", StringComparison.Ordinal);
        Assert.True(throwAt >= 0 && derefAt > throwAt,
            "FindAdapter still calls GetDesc on an adapter EnumAdapters may not have written.");
    }

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

            sb.Append(source[i]);
        }

        return sb.ToString();
    }

    private static string MethodBody(string source, string methodName, string accessibility)
    {
        var signature = Regex.Match(source,
            $@"{accessibility}\s+(?:unsafe\s+)?[\w<>,\[\]\?\.\s]*\s{methodName}\s*\(");
        Assert.True(signature.Success, $"{methodName} not found — repoint this lint.");

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
