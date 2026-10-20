using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>No Vulkan creation or allocation result may be dropped on the
///     floor.</b>
///     <para>
///         Forty were. A failed <c>AllocateDescriptorSets</c> returns a NULL
///         set that <c>UpdateDescriptorSets</c> wrote into and a later frame
///         bound — a validation error and a probable fault, surfaced to the user
///         as nothing at all. A failed <c>MapMemory</c> leaves a null pointer
///         the next frame's <c>Buffer.MemoryCopy</c> writes through. A failed
///         <c>CreateFramebuffer</c> leaves a zero handle in the framebuffer
///         array that <c>BeginFrame</c> hands to <c>CmdBeginRenderPass</c>.
///     </para>
///     <para>
///         The 2D path already checked its own (<c>Timeline2DFrameResources</c>,
///         <c>Timeline2DComposedRenderer</c>); <c>Vulkan/VulkanCheck.Ok</c> is
///         that style factored out, and this lint is what stops the next call
///         site from being added unchecked. It fires on the SHAPE — the result
///         must be an argument to <c>VulkanCheck.Ok</c>, an <c>if</c> condition,
///         or assigned — because a GPU is needed to make any of these fail.
///     </para>
/// </summary>
public sealed class VulkanResultConsumptionTests
{
    /// <summary>Silk entry points on the Vulkan API object whose
    /// <c>Result</c> reports a failure the caller cannot survive.</summary>
    private static readonly Regex Call = new(
        @"(?<!\w)(?<recv>_context\.Api|_vk|(?<![\w.])Api)\.(?<name>Create\w+|Allocate\w+|MapMemory|BindBufferMemory|BindImageMemory)\(",
        RegexOptions.Compiled);

    /// <summary>Consumption shapes that are as good as the wrapper: an
    /// <c>if</c> condition, or an assignment to a local the caller then
    /// tests.</summary>
    private static readonly string[] AcceptedPrefixes = ["VulkanCheck.Ok(", "if (", "="];

    public static TheoryData<string> RendererSources()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan"), "*.cs",
                     SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(TestPaths.RepoRoot(), file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RendererSources))]
    public void EveryCreationResultIsConsumed(string relativePath)
    {
        var source = StripCommentsAndStrings(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath)));

        foreach (Match m in Call.Matches(source))
        {
            var prefix = source[..m.Index].TrimEnd();
            Assert.True(AcceptedPrefixes.Any(p => prefix.EndsWith(p, StringComparison.Ordinal)),
                $"{relativePath}: the result of {m.Groups["recv"].Value}.{m.Groups["name"].Value} " +
                $"is dropped (…{Tail(prefix)}). Wrap it in VulkanCheck.Ok(…, \"site\") or test it — " +
                "a failed create leaves a null handle that is bound one frame later, with no " +
                "diagnostic anywhere.");
        }
    }

    /// <summary>The lint must actually be LOOKING at something: a floor on how
    /// many of these calls it found across the folder, so a receiver rename
    /// cannot quietly turn the whole Theory vacuous.</summary>
    [Fact]
    public void TheLintSeesTheCallsItClaimsToCover()
    {
        var found = 0;
        var wrapped = 0;
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan"), "*.cs",
                     SearchOption.AllDirectories))
        {
            var source = StripCommentsAndStrings(File.ReadAllText(file));
            foreach (Match m in Call.Matches(source))
            {
                found++;
                if (source[..m.Index].TrimEnd().EndsWith("VulkanCheck.Ok(", StringComparison.Ordinal))
                    wrapped++;
            }
        }

        Assert.True(found >= 40,
            $"only {found} creation/allocation calls matched — the receiver pattern has drifted " +
            "and the Theory above is passing vacuously.");
        Assert.True(wrapped >= 35,
            $"only {wrapped} of {found} go through VulkanCheck.Ok; the rest would have to be " +
            "if-tested, which is unlikely to be true and is worth re-reading.");
    }

    private static string Tail(string s) => s.Length <= 70 ? s : s[^70..];

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
}
