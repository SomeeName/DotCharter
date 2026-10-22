using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The app-local Visual C++ runtime the release ships beside <c>onnxruntime.dll</c>
///     (<c>tools/release/VcRedist.psm1</c>, pinned by <c>tools/release/vcredist.json</c>). ONNX Runtime
///     is a NuGet binary on the dynamic C++ runtime, so without these DLLs it does not load on a
///     Windows that lacks the system-wide redistributable. The module runs under pwsh on fixture
///     folders, so no Visual Studio is needed; the last cases keep <c>New-Release.ps1</c> calling it
///     before the payload's binary check and keep the pin honest against a real install when one exists.
/// </summary>
public sealed class VcRedistTests
{
    private static readonly string[] ExpectedFiles = ["msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    private static string ReleaseDir => Path.Combine(TestPaths.RepoRoot(), "tools", "release");

    private static string Module => Path.Combine(ReleaseDir, "VcRedist.psm1");

    private static (string Version, string Folder, Dictionary<string, string> Files) RepoPin()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReleaseDir, "vcredist.json")));
        var root = json.RootElement;
        var files = root.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        return (root.GetProperty("version").GetString()!, root.GetProperty("folder").GetString()!, files);
    }

    [Fact]
    public void The_pin_names_exactly_the_four_DLLs_ONNX_Runtime_imports_with_a_hash_each()
    {
        var pin = RepoPin();

        Assert.Equal(ExpectedFiles, pin.Files.Keys.Order(StringComparer.Ordinal));
        Assert.All(pin.Files.Values, hash => Assert.Matches("^[0-9a-f]{64}$", hash));
        Assert.Matches(@"^\d+\.\d+\.\d+$", pin.Version);
        Assert.Matches(@"^Microsoft\.VC\d+\.CRT$", pin.Folder);
    }

    /// <summary>The pin is only worth something if it matches Microsoft's files: checked wherever a Visual Studio carries that version.</summary>
    [Fact]
    public void The_pin_matches_the_installed_redistributable_when_one_carries_that_version()
    {
        var pin = RepoPin();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio");
        var dir = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(install => Path.Combine(install, "VC", "Redist", "MSVC", pin.Version, "x64", pin.Folder))
                .FirstOrDefault(Directory.Exists)
            : null;
        if (dir is null) Assert.Skip($"No Visual Studio here carries VC\\Redist\\MSVC\\{pin.Version}.");

        foreach (var (name, hash) in pin.Files)
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))));
    }

    [Fact]
    public void Install_copies_verified_files_from_the_installation_and_names_a_changed_one()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-vcredist-").FullName;
        try
        {
            var pinPath = WritePin(scratch, "install-a", out var folder);
            var run = RunPwsh(scratch, $$"""
                $env:DOTCHARTER_RELEASE_CACHE = {{Quote(Path.Combine(scratch, "cache"))}}
                Import-Module {{Quote(Module)}} -Force
                $pin = Read-VcRedistPin -Path {{Quote(pinPath)}}
                $from = Install-VcRedist -Pin $pin -Destination {{Quote(Path.Combine(scratch, "out1"))}} -InstallationPaths @({{Quote(Path.Combine(scratch, "install-a"))}})
                "from=$from"
                Add-Content -LiteralPath {{Quote(Path.Combine(folder, "vcruntime140.dll"))}} -Value 'tampered'
                Remove-Item {{Quote(Path.Combine(scratch, "cache"))}} -Recurse -Force
                try { Install-VcRedist -Pin $pin -Destination {{Quote(Path.Combine(scratch, "out2"))}} -InstallationPaths @({{Quote(Path.Combine(scratch, "install-a"))}}) | Out-Null; 'accepted-tampered' }
                catch { "refused=$($_.Exception.Message)" }
                """);
            Assert.True(run.ExitCode == 0, run.Output);

            Assert.Contains($"from={folder}", run.Output, StringComparison.Ordinal);
            foreach (var name in ExpectedFiles)
                Assert.True(File.Exists(Path.Combine(scratch, "out1", name)), name + " was not copied.");

            Assert.DoesNotContain("accepted-tampered", run.Output, StringComparison.Ordinal);
            Assert.Contains("vcruntime140.dll has SHA-256", run.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(scratch, "out2")) && Directory.EnumerateFiles(Path.Combine(scratch, "out2")).Any(),
                "a refused install left files in the payload.");
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void Install_falls_back_to_the_release_cache_and_refuses_when_nothing_holds_the_pinned_bytes()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-vcredist-cache-").FullName;
        try
        {
            var pinPath = WritePin(scratch, "install-a", out _);
            var run = RunPwsh(scratch, $$"""
                $env:DOTCHARTER_RELEASE_CACHE = {{Quote(Path.Combine(scratch, "cache"))}}
                Import-Module {{Quote(Path.Combine(ReleaseDir, "ReleaseCache.psm1"))}} -Force
                Import-Module {{Quote(Module)}} -Force
                $pin = Read-VcRedistPin -Path {{Quote(pinPath)}}
                Install-VcRedist -Pin $pin -Destination {{Quote(Path.Combine(scratch, "out1"))}} -InstallationPaths @({{Quote(Path.Combine(scratch, "install-a"))}}) | Out-Null
                # A Visual Studio update removed the pinned version: the cached copy serves the release.
                $cached = Install-VcRedist -Pin $pin -Destination {{Quote(Path.Combine(scratch, "out2"))}} -InstallationPaths @({{Quote(Path.Combine(scratch, "gone"))}})
                "cached=$cached"
                $entry = Get-RedistCacheDir -Tool 'vcredist' -Key (Get-VcRedistKey -Pin $pin)
                Add-Content -LiteralPath (Join-Path $entry 'msvcp140.dll') -Value 'bitrot'
                try { Install-VcRedist -Pin $pin -Destination {{Quote(Path.Combine(scratch, "out3"))}} -InstallationPaths @({{Quote(Path.Combine(scratch, "gone"))}}) | Out-Null; 'accepted-bad-cache' }
                catch { "refused=$($_.Exception.Message)" }
                "entry-kept=$(Test-Path $entry)"
                """);
            Assert.True(run.ExitCode == 0, run.Output);

            Assert.Contains("cached=", run.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(scratch, "out2", "msvcp140_1.dll")));
            Assert.DoesNotContain("accepted-bad-cache", run.Output, StringComparison.Ordinal);
            Assert.Contains("msvcp140.dll has SHA-256", run.Output, StringComparison.Ordinal);
            Assert.Contains("entry-kept=False", run.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(scratch, "out3")) && Directory.EnumerateFiles(Path.Combine(scratch, "out3")).Any());
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void New_Release_installs_the_pinned_runtime_before_the_payload_binary_check_and_beside_the_app()
    {
        var script = File.ReadAllText(Path.Combine(ReleaseDir, "New-Release.ps1"));

        Assert.Contains("Import-Module (Join-Path $PSScriptRoot 'VcRedist.psm1')", script, StringComparison.Ordinal);
        var install = script.IndexOf("Install-VcRedist -Pin $VcRedistPin", StringComparison.Ordinal);
        var copy = script.IndexOf("Join-Path $appDir $name", StringComparison.Ordinal);
        var check = script.IndexOf("Test-PayloadBinaries -PayloadDir $publishDir -Label 'UI'", StringComparison.Ordinal);
        Assert.True(install >= 0 && copy > install && check > copy,
            "New-Release.ps1 must verify the runtime (Install-VcRedist), copy it into app\\, then run the binary check.");
    }

    /// <summary>A fake Visual Studio install holding the four DLLs, and a pin file with their real hashes.</summary>
    private static string WritePin(string scratch, string installName, out string folder)
    {
        const string version = "14.0.1";
        const string crt = "Microsoft.VC999.CRT";
        folder = Path.Combine(scratch, installName, "VC", "Redist", "MSVC", version, "x64", crt);
        Directory.CreateDirectory(folder);
        var files = new Dictionary<string, string>();
        foreach (var name in ExpectedFiles)
        {
            var bytes = Encoding.UTF8.GetBytes("fixture " + name);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            files[name] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        var path = Path.Combine(scratch, "vcredist.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version, folder = crt, files }));
        return path;
    }
}
