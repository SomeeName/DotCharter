using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The prebuilt native runtime (#832): <c>tools/setup/NativeRuntime.psm1</c> run as
///     itself under <c>pwsh</c> on FIXTURE zips with a fake downloader and a fake ABI
///     reader — no network, no real DLL. A pair is installed only when every file
///     matches its SHA-256 pin and the DLL reports the header's ABI; any refusal leaves
///     the runtimes folder untouched. Plus the pins and the MSBuild wiring that runs it.
///     A missing <c>pwsh</c> is a skip, never a failure.
/// </summary>
public sealed partial class NativeRuntimeFetchTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Asset = "juce-sharp-native-win-x64-0123456789ab.zip";
    private static readonly byte[] DllBytes = Encoding.ASCII.GetBytes("MZ fixture dll");
    private static readonly byte[] ExeBytes = Encoding.ASCII.GetBytes("MZ fixture exe");

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-native-runtime-" + Guid.NewGuid().ToString("N"));

    public NativeRuntimeFetchTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Fact]
    public void The_pinned_pair_is_installed_from_the_asset_named_by_the_gitlink()
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes)));

        Assert.True(result.GetProperty("error").ValueKind == JsonValueKind.Null, result.GetProperty("error").ToString());
        Assert.Equal(["SomeeName/JuceSharp|native-prebuilt|" + Asset], Strings(result, "calls"));
        Assert.Equal(["juce_sharp.dll", "juce_vst_host.exe"], Strings(result, "installed"));
        Assert.Equal(DllBytes, File.ReadAllBytes(Path.Combine(_scratch, "dest", "juce_sharp.dll")));
        Assert.Equal(ExeBytes, File.ReadAllBytes(Path.Combine(_scratch, "dest", "juce_vst_host.exe")));
    }

    [Fact]
    public void A_download_with_the_wrong_hash_fails_loudly_and_installs_nothing()
    {
        var tampered = Encoding.ASCII.GetBytes("MZ fixture dll, one byte off");
        var result = Install(Zip(tampered, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes)));

        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("SHA-256 mismatch for juce_sharp.dll", error, StringComparison.Ordinal);
        Assert.Contains(Sha(tampered), error, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "installed"));
    }

    [Fact]
    public void A_dll_reporting_another_abi_fails_and_installs_nothing()
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes)), dllAbi: 45);

        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("reports dca_abi_version() 45, but the header says DCA_ABI_VERSION 46", error, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "installed"));
    }

    [Fact]
    public void A_commit_without_a_pin_downloads_nothing()
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes), commit: new string('f', 40)));

        Assert.Contains("No prebuilt native runtime is pinned for JuceSharp " + Commit,
            result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "calls"));
        Assert.Empty(Strings(result, "installed"));
    }

    [Fact]
    public void A_pin_for_another_abi_than_the_header_downloads_nothing()
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes), abi: 45));

        Assert.Contains("reports ABI 45, but the header says DCA_ABI_VERSION 46",
            result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "calls"));
    }

    [Fact]
    public void A_zip_missing_half_of_the_pair_installs_nothing()
    {
        var result = Install(Zip(DllBytes, exe: null), Manifest(Sha(DllBytes), Sha(ExeBytes)));

        Assert.Contains("has no juce_vst_host.exe at its root", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "installed"));
    }

    [Theory]
    [InlineData("SomeeName/JuceSharp-Evil")]
    [InlineData("Attacker/JuceSharp")]
    [InlineData("SomeeName/JuceSharp/../Other")]
    public void A_manifest_naming_another_repository_downloads_nothing(string repository)
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes), repository: repository));

        Assert.Contains("is not one of the allowed repositories", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "calls"));
        Assert.Empty(Strings(result, "probes"));
        Assert.Empty(Strings(result, "installed"));
    }

    [Fact]
    public void A_release_name_that_is_not_a_plain_tag_downloads_nothing()
    {
        var result = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes), release: "../../evil"));

        Assert.Contains("is not a plain release tag", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(result, "calls"));
    }

    [Fact]
    public void The_dll_is_probed_only_after_every_hash_matched()
    {
        var good = Install(Zip(DllBytes, ExeBytes), Manifest(Sha(DllBytes), Sha(ExeBytes)));
        Assert.Single(Strings(good, "probes"));

        var tamperedExe = Install(Zip(DllBytes, Encoding.ASCII.GetBytes("MZ other exe")), Manifest(Sha(DllBytes), Sha(ExeBytes)));
        Assert.Contains("SHA-256 mismatch for juce_vst_host.exe", tamperedExe.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(Strings(tamperedExe, "probes"));
    }

    /// <summary>
    ///     The real ABI reader loads the DLL in a CHILD PowerShell, never in the
    ///     process that runs the build (or holds a CI token), and hands that child
    ///     no GitHub token.
    /// </summary>
    [Fact]
    public void The_abi_probe_loads_the_dll_in_a_child_process_that_gets_no_token()
    {
        var notADll = Path.Combine(_scratch, "not-a-dll.dll");
        File.WriteAllBytes(notADll, DllBytes);
        var result = RunScript($$"""
            $env:GH_TOKEN = 'gh-secret'; $env:GITHUB_TOKEN = 'run-secret'; $env:SUBMODULES_TOKEN = 'pat-secret'
            $result = [ordered]@{ error = $null }
            try { [void](Read-DllAbiVersion -DllPath '{{Escape(notADll)}}') } catch { $result.error = $_.Exception.Message }
            $result.loadedHere = [bool]('DotCharterNativeRuntime.Kernel32' -as [type])
            $info = New-DllAbiProbeStartInfo -DllPath '{{Escape(notADll)}}'
            $result.childTokens = @(foreach ($name in 'GH_TOKEN', 'GITHUB_TOKEN', 'SUBMODULES_TOKEN') { if ($info.EnvironmentVariables.ContainsKey($name)) { $name } })
            $result.parentToken = $env:GH_TOKEN
            """);

        Assert.Contains("Could not load", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.False(result.GetProperty("loadedHere").GetBoolean(), "the DLL was loaded into the calling PowerShell");
        Assert.Empty(Strings(result, "childTokens"));
        Assert.Equal("gh-secret", result.GetProperty("parentToken").GetString());
    }

    [Fact]
    public void The_asset_name_is_keyed_on_the_full_gitlink_and_refuses_anything_else()
    {
        var result = RunScript("""
            $result = [ordered]@{
                name = Get-NativeRuntimeAssetName '35781c1bc458c66876680d75aa8eb53c879524c9'
                shortRefused = $false
                upperRefused = $false
            }
            try { [void](Get-NativeRuntimeAssetName '35781c1') } catch { $result.shortRefused = $true }
            try { [void](Get-NativeRuntimeAssetName '35781C1BC458C66876680D75AA8EB53C879524C9') } catch { $result.upperRefused = $true }
            """);

        Assert.Equal("juce-sharp-native-win-x64-35781c1bc458.zip", result.GetProperty("name").GetString());
        Assert.True(result.GetProperty("shortRefused").GetBoolean());
        Assert.True(result.GetProperty("upperRefused").GetBoolean());
    }

    /// <summary>
    ///     A gitlink bump without a pin leaves every clean clone without an engine:
    ///     the checkout's own gitlink must be pinned, for the header's ABI.
    /// </summary>
    [Fact]
    public void The_manifest_pins_the_checked_in_gitlink_for_the_header_abi()
    {
        var root = TestPaths.RepoRoot();
        var gitlink = GitlinkOrNull(root);
        Assert.SkipUnless(gitlink is not null, "not a git checkout with a JuceSharp gitlink.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "setup", "native-runtime.json")));
        var entry = manifest.RootElement.GetProperty("runtimes").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("commit").GetString() == gitlink);
        Assert.True(entry.ValueKind == JsonValueKind.Object,
            $"tools/setup/native-runtime.json pins no runtime for the JuceSharp gitlink {gitlink}. "
            + "Publish the pair with tools/release/Publish-NativeRuntime.ps1 and commit the pin with the bump.");

        var header = File.ReadAllText(Path.Combine(root, "JuceSharp", "src", "JuceSharp.Native", "include", "dotcharter_audio.h"));
        var abi = AbiDefine().Match(header);
        Assert.True(abi.Success, "dotcharter_audio.h has no DCA_ABI_VERSION define.");
        Assert.Equal(int.Parse(abi.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), entry.GetProperty("abi").GetInt32());
        foreach (var file in new[] { "juce_sharp.dll", "juce_vst_host.exe" })
            Assert.Matches("^[0-9a-f]{64}$", entry.GetProperty("files").GetProperty(file).GetString()!);
    }

    /// <summary>
    ///     The fetch runs only when the DLL is MISSING, is skippable, stays off in CI
    ///     unless asked, and is hooked for a solution build AND a single-project build.
    /// </summary>
    [Fact]
    public void The_build_fetches_only_a_missing_dll_and_honours_the_switches()
    {
        var root = TestPaths.RepoRoot();
        var targets = File.ReadAllText(Path.Combine(root, "tools", "setup", "NativeRuntime.targets"));
        Assert.Contains("Condition=\"!Exists('$(DotCharterNativeRuntimeDir)juce_sharp.dll')\"", targets, StringComparison.Ordinal);
        Assert.Contains("'$(DotCharterSkipNativeFetch)' != 'true'", targets, StringComparison.Ordinal);
        Assert.Contains("('$(CI)' != 'true' And '$(GITHUB_ACTIONS)' != 'true') Or '$(DotCharterFetchNative)' == 'true'", targets, StringComparison.Ordinal);
        Assert.Contains("'$(DesignTimeBuild)' != 'true'", targets, StringComparison.Ordinal);
        Assert.Contains("Get-NativeRuntime.ps1", targets, StringComparison.Ordinal);

        var solution = File.ReadAllText(Path.Combine(root, "Directory.Solution.targets"));
        Assert.Contains("<Import Project=\"tools/setup/NativeRuntime.targets\"", solution, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"Build;Rebuild\" DependsOnTargets=\"DotCharterEnsureNativeRuntime\"", solution, StringComparison.Ordinal);

        var project = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        Assert.Contains("<Import Project=\"tools/setup/NativeRuntime.targets\"", project, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"ResolveProjectReferences\"", project, StringComparison.Ordinal);
        Assert.Contains("DependsOnTargets=\"DotCharterEnsureNativeRuntime\"", project, StringComparison.Ordinal);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Zip(byte[] dll, byte[]? exe)
    {
        var dir = Path.Combine(_scratch, "assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Asset);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "juce_sharp.dll", dll);
        if (exe is not null) Add(archive, "juce_vst_host.exe", exe);
        return dir;

        static void Add(ZipArchive archive, string name, byte[] bytes)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    private string Manifest(string dllSha, string exeSha, string commit = Commit, int abi = 46,
        string repository = "SomeeName/JuceSharp", string release = "native-prebuilt")
    {
        var path = Path.Combine(_scratch, "native-runtime.json");
        File.WriteAllText(path, $$"""
            { "repository": "{{repository}}", "release": "{{release}}",
              "runtimes": [ { "commit": "{{commit}}", "abi": {{abi}},
                "files": { "juce_sharp.dll": "{{dllSha}}", "juce_vst_host.exe": "{{exeSha}}" } } ] }
            """);
        return path;
    }

    /// <summary>Install-NativeRuntime against a fake downloader serving <paramref name="assetDir" />.</summary>
    private JsonElement Install(string assetDir, string manifest, int dllAbi = 46)
    {
        var dest = Path.Combine(_scratch, "dest");
        return RunScript($$"""
            $calls = [System.Collections.Generic.List[string]]::new()
            $probes = [System.Collections.Generic.List[string]]::new()
            $fake = {
                param($Repository, $Release, $Asset, $OutDir)
                $calls.Add("$Repository|$Release|$Asset")
                Copy-Item -LiteralPath (Join-Path '{{Escape(assetDir)}}' $Asset) -Destination $OutDir
            }
            $result = [ordered]@{ error = $null }
            try {
                [void](Install-NativeRuntime -Commit '{{Commit}}' -ManifestPath '{{Escape(manifest)}}' -HeaderAbi 46 `
                    -Destination '{{Escape(dest)}}' -Download $fake -ReadAbi { param($p) $probes.Add($p); {{dllAbi}} })
            }
            catch { $result.error = $_.Exception.Message }
            $result.calls = @($calls)
            $result.probes = @($probes)
            $result.installed = @(if (Test-Path -LiteralPath '{{Escape(dest)}}') { Get-ChildItem -LiteralPath '{{Escape(dest)}}' -Force | Sort-Object Name | ForEach-Object Name })
            """);
    }

    /// <summary>Runs <paramref name="body" /> with the module imported; it fills <c>$result</c>.</summary>
    private JsonElement RunScript(string body)
    {
        var module = Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "NativeRuntime.psm1");
        var resultFile = Path.Combine(_scratch, "result-" + Guid.NewGuid().ToString("N") + ".json");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module '{{Escape(module)}}' -Force
            {{body}}
            $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath '{{Escape(resultFile)}}' -Encoding utf8
            """;
        var file = Path.Combine(_scratch, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", file })
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("pwsh (PowerShell 7+) is not on PATH, so NativeRuntime.psm1 cannot be executed here.");
            return default;
        }

        Assert.NotNull(process);
        using (process)
        {
            var stdoutTask = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEnd();
            // A deadlock detector for a hung child, never a budget.
            Assert.True(process.WaitForExit(120_000), "pwsh did not finish within 120 s.");
            var stdout = stdoutTask.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
        return document.RootElement.Clone();
    }

    private static string? GitlinkOrNull(string root)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-C", root, "ls-tree", "HEAD", "JuceSharp" })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            var match = Gitlink().Match(output);
            return process.ExitCode == 0 && match.Success ? match.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> Strings(JsonElement result, string name) =>
        result.GetProperty(name).ValueKind == JsonValueKind.Null
            ? []
            : result.GetProperty(name).EnumerateArray().Select(e => e.GetString()!);

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex(@"^160000 commit ([0-9a-f]{40})\s")]
    private static partial Regex Gitlink();

    [GeneratedRegex(@"^\s*#define\s+DCA_ABI_VERSION\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex AbiDefine();
}
