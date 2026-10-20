namespace DotCharter.Tests.Rendering.Headless;

/// <summary>Which device the headless GPU tests render on, if any.</summary>
public enum GpuTestLane
{
    /// <summary><c>DOTCHARTER_GPU_TESTS</c> unset (the default gate): the tests skip.</summary>
    Off,

    /// <summary><c>DOTCHARTER_GPU_TESTS=1</c>: the machine's own Vulkan driver.</summary>
    Hardware,

    /// <summary><c>DOTCHARTER_GPU_TESTS=sw</c>: Mesa lavapipe, the CPU rasterizer, and nothing else.</summary>
    Software
}

/// <summary>The resolved lane plus, on the software lane, the ICD manifest the loader is pointed at.</summary>
public sealed record GpuTestLaneConfig(GpuTestLane Lane, string? IcdManifest);

/// <summary>
///     The software-rasterizer lane of the headless golden frames (#315).
///     <c>DOTCHARTER_GPU_TESTS=sw</c> runs every <c>Rendering/Headless</c> test
///     on Mesa lavapipe instead of the GPU, so they run on any box and in a
///     manual CI run. <c>tools/ci/Get-SoftwareVulkan.ps1</c> downloads the
///     pinned, hash-checked build to <see cref="DefaultIcdManifest" />;
///     <c>DOTCHARTER_SWVK_ICD</c> overrides the manifest path.
///     <para>
///         <see cref="Apply" /> writes the loader variables into THIS PROCESS's
///         environment only — never the user's or the machine's, never the
///         registry. <c>VK_DRIVER_FILES</c> (and the pre-1.3.207 spelling
///         <c>VK_ICD_FILENAMES</c>) REPLACES the loader's driver list, so the
///         software device is the only physical device the headless device
///         pick (first device with a graphics queue) can see; implicit layers
///         (overlays, capture hooks) are disabled so a frame does not depend on
///         what else is installed on the box.
///     </para>
/// </summary>
public static class SoftwareVulkanLane
{
    /// <summary>Must equal <c>-Version</c>'s default in <c>tools/ci/Get-SoftwareVulkan.ps1</c> (linted).</summary>
    public const string PinnedVersion = "lavapipe-x64-26.2.4-20261006";

    public const string ManifestFileName = "lvp_icd.x86_64.json";

    private static readonly Lock ApplyLock = new();
    private static string? _applied;

    /// <summary>Where the script unpacks by default: <c>%LOCALAPPDATA%\DotCharter\swvk\&lt;version&gt;</c>.</summary>
    public static string DefaultIcdManifest(string localAppData) =>
        Path.Combine(localAppData, "DotCharter", "swvk", PinnedVersion, ManifestFileName);

    /// <summary>Pure: the lane an environment asks for. Anything but <c>1</c> or <c>sw</c> is off.</summary>
    public static GpuTestLaneConfig Resolve(string? gpuTests, string? icdOverride, string localAppData) =>
        gpuTests switch
        {
            "1" => new GpuTestLaneConfig(GpuTestLane.Hardware, null),
            "sw" => new GpuTestLaneConfig(GpuTestLane.Software,
                string.IsNullOrWhiteSpace(icdOverride) ? DefaultIcdManifest(localAppData) : icdOverride),
            _ => new GpuTestLaneConfig(GpuTestLane.Off, null)
        };

    /// <summary>Pure: the process variables the software lane sets. Empty for every other lane.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LoaderVariables(GpuTestLaneConfig config) =>
        config is { Lane: GpuTestLane.Software, IcdManifest: { } icd }
            ?
            [
                new("VK_DRIVER_FILES", icd),
                new("VK_ICD_FILENAMES", icd),
                new("VK_LOADER_LAYERS_DISABLE", "~implicit~")
            ]
            : [];

    /// <summary>This process's lane.</summary>
    public static GpuTestLaneConfig Current =>
        Resolve(Environment.GetEnvironmentVariable("DOTCHARTER_GPU_TESTS"),
            Environment.GetEnvironmentVariable("DOTCHARTER_SWVK_ICD"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    ///     Points this process's Vulkan loader at the software ICD, once, before
    ///     any test creates an instance (the loader reads the variables at
    ///     <c>vkCreateInstance</c>). A missing manifest FAILS rather than skips:
    ///     the lane was asked for by name, and a skip would read as green.
    /// </summary>
    public static void Apply(GpuTestLaneConfig config)
    {
        if (config.Lane != GpuTestLane.Software) return;

        lock (ApplyLock)
        {
            if (_applied == config.IcdManifest) return;
            if (!File.Exists(config.IcdManifest))
            {
                throw new InvalidOperationException(
                    $"DOTCHARTER_GPU_TESTS=sw but the software Vulkan ICD manifest is missing: {config.IcdManifest}. " +
                    "Run `pwsh -NoProfile -File tools/ci/Get-SoftwareVulkan.ps1` (or set DOTCHARTER_SWVK_ICD).");
            }

            foreach (var (name, value) in LoaderVariables(config))
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            _applied = config.IcdManifest;
        }
    }
}
