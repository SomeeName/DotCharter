using System.Diagnostics;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Read-only publication point for Vulkan validation-layer output when the
///     app runs with <c>DOTCHARTER_VK_VALIDATION=1</c> (see
///     <see cref="VulkanContext" />). Same static-channel pattern as
///     <c>RenderDiagnostics</c>: the messenger callback writes, diagnostics
///     UI / logs read a snapshot. Inactive (all zeros) in normal runs.
/// </summary>
public static class VulkanValidationDiagnostics
{
    /// <summary>True when the validation layer was requested AND found.</summary>
    public static bool Active { get; private set; }

    public static int ErrorCount => _errorCount;
    public static int WarningCount => _warningCount;

    /// <summary>The most recent validation ERROR message (never warnings —
    /// the first thing anyone wants to see is the worst thing that happened).</summary>
    public static string? LastError { get; private set; }

    private static int _errorCount;
    private static int _warningCount;

    internal static void Reset(bool active)
    {
        Active = active;
        _errorCount = 0;
        _warningCount = 0;
        LastError = null;
    }

    internal static void Record(bool isError, string message)
    {
        if (isError)
        {
            Interlocked.Increment(ref _errorCount);
            LastError = message;
            Debug.WriteLine($"[VULKAN VALIDATION ERROR] {message}");
            Console.Error.WriteLine($"[VULKAN VALIDATION ERROR] {message}");
        }
        else
        {
            Interlocked.Increment(ref _warningCount);
            Debug.WriteLine($"[VULKAN VALIDATION WARN] {message}");
        }
    }
}
