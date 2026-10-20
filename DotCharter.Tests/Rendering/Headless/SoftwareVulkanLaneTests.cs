using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The software-rasterizer lane (#315). The pure half runs in the default
///     gate: which variables each lane sets (the GPU lane and the off lane set
///     none, so the default gate's Vulkan is untouched), the pin shared with
///     <c>tools/ci/Get-SoftwareVulkan.ps1</c>, and the script's hash check run for
///     real on a synthetic archive (no network). The device half runs only on
///     the lane itself.
/// </summary>
public sealed class SoftwareVulkanLaneTests : IDisposable
{
    private const string LocalAppData = @"E:\Local";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "dotcharter-swvk-test-" + Guid.NewGuid().ToString("N"));

    public SoftwareVulkanLaneTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("SW")]
    public void Anything_but_1_or_sw_is_off_and_sets_no_loader_variable(string? value)
    {
        var config = SoftwareVulkanLane.Resolve(value, @"D:\icd.json", LocalAppData);

        Assert.Equal(GpuTestLane.Off, config.Lane);
        Assert.Empty(SoftwareVulkanLane.LoaderVariables(config));
    }

    [Fact]
    public void The_gpu_lane_leaves_the_loader_alone()
    {
        var config = SoftwareVulkanLane.Resolve("1", @"D:\icd.json", LocalAppData);

        Assert.Equal(GpuTestLane.Hardware, config.Lane);
        Assert.Null(config.IcdManifest);
        Assert.Empty(SoftwareVulkanLane.LoaderVariables(config));
    }

    [Fact]
    public void The_software_lane_replaces_the_driver_list_with_the_pinned_icd()
    {
        var config = SoftwareVulkanLane.Resolve("sw", null, LocalAppData);
        var expected = Path.Combine(LocalAppData, "DotCharter", "swvk", SoftwareVulkanLane.PinnedVersion, "lvp_icd.x86_64.json");

        Assert.Equal(GpuTestLane.Software, config.Lane);
        Assert.Equal(expected, config.IcdManifest);
        var vars = SoftwareVulkanLane.LoaderVariables(config).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(expected, vars["VK_DRIVER_FILES"]);
        Assert.Equal(expected, vars["VK_ICD_FILENAMES"]);
        Assert.Equal("~implicit~", vars["VK_LOADER_LAYERS_DISABLE"]);
    }

    [Fact]
    public void DOTCHARTER_SWVK_ICD_overrides_the_manifest_path()
    {
        var config = SoftwareVulkanLane.Resolve("sw", @"D:\swvk\my_icd.json", LocalAppData);

        Assert.Equal(@"D:\swvk\my_icd.json", config.IcdManifest);
    }

    [Fact]
    public void A_missing_manifest_fails_the_lane_instead_of_skipping_it()
    {
        var config = new GpuTestLaneConfig(GpuTestLane.Software, Path.Combine(_scratch, "absent.json"));

        var error = Assert.Throws<InvalidOperationException>(() => SoftwareVulkanLane.Apply(config));
        Assert.Contains("Get-SoftwareVulkan.ps1", error.Message);
    }

    [Fact]
    public void The_pinned_version_is_the_scripts_default()
    {
        var script = File.ReadAllText(ScriptPath());
        var match = Regex.Match(script, @"\[string\]\$Version = '([^']+)'");

        Assert.True(match.Success, "Get-SoftwareVulkan.ps1 no longer declares a default -Version.");
        Assert.Equal(SoftwareVulkanLane.PinnedVersion, match.Groups[1].Value);
    }

    [Fact]
    public void The_script_refuses_an_archive_whose_hash_is_not_the_pinned_one()
    {
        var archive = FakeArchive();
        var destination = Path.Combine(_scratch, "dest");
        var wrong = Convert.ToHexStringLower(SHA256.HashData("not the archive"u8.ToArray()));

        var (exit, output) = RunScript(archive, destination, wrong);

        Assert.Equal(1, exit);
        // Windows PowerShell wraps an error record at a fixed width, so a deep checkout's script path
        // can push a line break into the message.
        Assert.Contains("SHA-256 mismatch", Regex.Replace(output, @"\s+", " "));
        Assert.False(Directory.Exists(destination), "a refused archive must leave nothing unpacked");
    }

    [Fact]
    public void The_script_unpacks_a_matching_archive_and_writes_the_manifest_next_to_the_driver()
    {
        var archive = FakeArchive();
        var destination = Path.Combine(_scratch, "dest");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));

        var (exit, output) = RunScript(archive, destination, hash);

        Assert.True(exit == 0, output);
        var manifest = Path.Combine(destination, SoftwareVulkanLane.ManifestFileName);
        Assert.Equal(manifest, output.Trim().Split('\n')[^1].Trim());
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal(Path.Combine(destination, "bin", "vulkan_lvp.dll"),
            json.RootElement.GetProperty("ICD").GetProperty("library_path").GetString());

        // A second run finds the marker and does not fetch anything (no -ArchivePath, an unreachable -Url).
        var (again, againOutput) = RunScript(null, destination, hash, "https://invalid.invalid/none.zip");
        Assert.True(again == 0, againOutput);
        Assert.Equal(manifest, againOutput.Trim().Split('\n')[^1].Trim());
    }

    /// <summary>
    ///     On the lane itself: the renderer really is on the CPU rasterizer. If
    ///     the loader variables were set too late (after an instance), or the
    ///     ICD failed to load and the loader fell back, the device would be the
    ///     GPU and every other headless test would still pass — this is the one
    ///     that would not.
    /// </summary>
    [Collection("HeadlessGpu")]
    public sealed class OnTheLane
    {
        [Fact]
        public void The_headless_device_is_lavapipe()
        {
            Assert.SkipWhen(SoftwareVulkanLane.Current.Lane != GpuTestLane.Software,
                "Software-lane check — set DOTCHARTER_GPU_TESTS=sw to run.");
            Assert.True(GoldenFrame.GpuTestsEnabled);

            using var renderer = new HeadlessHighwayRenderer();

            Assert.Contains("llvmpipe", renderer.DeviceName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ScriptPath() => Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "Get-SoftwareVulkan.ps1");

    private string FakeArchive()
    {
        var source = Path.Combine(_scratch, "src");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        Directory.CreateDirectory(Path.Combine(source, "share", "vulkan", "icd.d"));
        File.WriteAllBytes(Path.Combine(source, "bin", "vulkan_lvp.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        var archive = Path.Combine(_scratch, "fake-lavapipe.zip");
        ZipFile.CreateFromDirectory(source, archive);
        return archive;
    }

    private (int Exit, string Output) RunScript(string? archive, string destination, string sha256, string? url = null)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath() })
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("-Destination");
        info.ArgumentList.Add(destination);
        info.ArgumentList.Add("-ExpectedSha256");
        info.ArgumentList.Add(sha256);
        if (archive is not null)
        {
            info.ArgumentList.Add("-ArchivePath");
            info.ArgumentList.Add(archive);
        }

        if (url is not null)
        {
            info.ArgumentList.Add("-Url");
            info.ArgumentList.Add(url);
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Windows PowerShell is not available, so Get-SoftwareVulkan.ps1 cannot be executed here.");
            return (-1, "");
        }

        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEnd();
        // A deadlock detector for a hung child, never a budget.
        Assert.True(process.WaitForExit(120_000), "powershell did not finish within 120 s.");
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult() + stderr);
    }
}
