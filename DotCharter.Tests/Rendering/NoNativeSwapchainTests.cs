using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DotCharter.Rendering.Engines;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>The 3D renderer has no native swapchain, and this keeps it that way.</b>
///     <para>
///         The app's only on-screen path is the Avalonia-composed one (exported
///         D3D11 keyed-mutex images); tests render headless. The native
///         child-HWND path — a Win32 surface, a <c>VkSwapchainKHR</c>,
///         acquire/present, per-image present semaphores, the pop-out surface
///         re-target — was unreachable from the app after the UI host was
///         removed and was then deleted from <c>DotCharter.Rendering</c>.
///         Bringing any piece of it back silently re-opens a second, untested
///         presentation path whose sync rules (Suboptimal-acquire semaphore
///         leaks, per-image present semaphores, destroy-swapchain-before-surface)
///         nobody exercises any more.
///     </para>
///     <para>
///         Two angles: a SOURCE lint over every <c>.cs</c> in the project (code
///         only — comments are stripped, because the guides and remarks still
///         explain what was removed) and a REFLECTION check that no type in the
///         assembly exposes a window-handle or surface/swapchain shape.
///     </para>
/// </summary>
public sealed class NoNativeSwapchainTests
{
    private static readonly Assembly Rendering = typeof(Fretboard3DEngine).Assembly;

    /// <summary>Identifiers and extension names that only a surface/swapchain
    /// path uses. Matched as whole words in comment-free source.</summary>
    public static readonly TheoryData<string> ForbiddenTokens =
    [
        "KhrSwapchain",
        "KhrSurface",
        "KhrWin32Surface",
        "Win32SurfaceCreateInfoKHR",
        "CreateWin32Surface",
        "SwapchainKHR",
        "SwapchainCreateInfoKHR",
        "SurfaceKHR",
        "PresentInfoKHR",
        "AcquireNextImage",
        "QueuePresent",
        "PresentSrcKhr",
        "GetPhysicalDeviceSurfaceSupport",
        "GetPhysicalDeviceSurfaceCapabilities",
        "IPlatformHandle",
        "VK_KHR_swapchain",
        "VK_KHR_surface",
        "VK_KHR_win32_surface"
    ];

    [Theory]
    [MemberData(nameof(ForbiddenTokens))]
    public void NoRenderingSourceUsesASurfaceOrSwapchain(string token)
    {
        var files = RenderingSourceFiles();
        // A rename of the project folder must not make this pass vacuously.
        Assert.True(files.Count > 50,
            $"Only {files.Count} .cs files found under DotCharter.Rendering — repoint this lint.");
        Assert.Contains(files, f => f.EndsWith("VulkanRenderer.cs", StringComparison.Ordinal));

        var pattern = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])");
        var offenders = files
            .Where(f => pattern.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(TestPaths.RepoRoot(), f))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{token}' is back in DotCharter.Rendering ({string.Join(", ", offenders)}). The native " +
            "child-HWND swapchain path was removed on purpose: the 3D preview is composed-only " +
            "(docs/agents/rendering/vulkan-device-lifetime.md). Do not reintroduce a surface/swapchain " +
            "path without the owner asking for it.");
    }

    /// <summary>No member anywhere in the assembly takes or exposes a window
    /// handle, a surface or a swapchain — the shape an HWND init would need.</summary>
    [Fact]
    public void NoRenderingTypeTakesAWindowHandleOrExposesASwapchain()
    {
        var forbiddenTypeNames = new[]
        {
            "Avalonia.Platform.IPlatformHandle",
            "Silk.NET.Vulkan.SurfaceKHR",
            "Silk.NET.Vulkan.SwapchainKHR",
            "Silk.NET.Vulkan.Extensions.KHR.KhrSurface",
            "Silk.NET.Vulkan.Extensions.KHR.KhrSwapchain",
            "Silk.NET.Vulkan.Extensions.KHR.KhrWin32Surface"
        };

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        var types = Rendering.GetTypes();
        Assert.Contains(types, t => t.Name == "VulkanRenderer");

        foreach (var type in types)
        {
            foreach (var field in type.GetFields(all))
                if (IsForbidden(field.FieldType)) offenders.Add($"{type.FullName}.{field.Name}");
            foreach (var property in type.GetProperties(all))
                if (IsForbidden(property.PropertyType)) offenders.Add($"{type.FullName}.{property.Name}");
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method is MethodInfo mi && IsForbidden(mi.ReturnType))
                    offenders.Add($"{type.FullName}.{method.Name} (return)");
                foreach (var parameter in method.GetParameters())
                {
                    if (IsForbidden(parameter.ParameterType))
                        offenders.Add($"{type.FullName}.{method.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DotCharter.Rendering has a window-handle / surface / swapchain shape again: " +
            string.Join(", ", offenders) + ". The 3D preview is composed-only.");

        bool IsForbidden(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            return forbiddenTypeNames.Contains(t.FullName);
        }
    }

    private static List<string> RenderingSourceFiles()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) &&
                        !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments; string and char
    /// literals are copied through untouched (an extension NAME in a string is
    /// exactly what this lint must see).</summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                sb.Append(c);
                i++;
                while (i < source.Length && source[i] != c)
                {
                    if (source[i] == '\\' && !verbatim && i + 1 < source.Length)
                    {
                        sb.Append(source[i]);
                        i++;
                    }

                    sb.Append(source[i]);
                    i++;
                }

                if (i < source.Length) sb.Append(source[i]);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
