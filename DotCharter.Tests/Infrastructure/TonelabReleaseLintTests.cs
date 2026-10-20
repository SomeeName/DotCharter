using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     tonelab ships prebuilt and pinned: its source stays in its own repository, and
///     <c>tools/release/tonelab/tonelab.json</c> names the commit and each file's SHA-256.
///     <c>tools/release/New-Release.ps1</c> checks the files from <c>-TonelabDir</c> against that pin
///     in preflight and again before it copies exactly the files <see cref="TonelabInstall" /> looks
///     for into the install's <c>tools/tonelab/</c>, before the payload is hashed, with a printed
///     <c>-SkipTonelab</c> escape.
/// </summary>
public sealed partial class TonelabReleaseLintTests
{
    private static string Root => TestPaths.RepoRoot();

    private static string PinDir => Path.Combine(Root, "tools", "release", "tonelab");

    private static string Script() => File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1"));

    [Fact]
    public void TheReleaseShipsExactlyWhatTheLauncherLooksFor_IntoToolsTonelab()
    {
        var script = Script();
        var files = Regex.Match(script, @"\$TonelabFiles\s*=\s*@\(([^)]*)\)");
        Assert.True(files.Success, "New-Release.ps1 no longer lists $TonelabFiles.");
        Assert.Equal([TonelabInstall.DllName, TonelabInstall.HelperName, "tonelab.ini"],
            Regex.Matches(files.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value));
        Assert.Contains($"Join-Path $PublishDir 'tools/{TonelabInstall.FolderName}'", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $TonelabPinDir 'LICENSE.txt'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePin_HashesEveryShippedFile_AndNamesItsCommit()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(PinDir, "tonelab.json")));
        Assert.Matches("^[0-9a-f]{7,40}$", pin.RootElement.GetProperty("commit").GetString());
        var hashes = pin.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal([TonelabInstall.DllName, TonelabInstall.HelperName, "tonelab.ini"], hashes.Keys);
        Assert.All(hashes.Values, h => Assert.Matches("^[0-9a-f]{64}$", h));
        Assert.StartsWith("MIT License", File.ReadAllText(Path.Combine(PinDir, "LICENSE.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCopy_IsCheckedAgainstThePin_BeforeThePayloadIsHashed()
    {
        var script = Script();
        Assert.Contains("Get-FileHash -LiteralPath $path -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("if ($hash -ne $pin.files.$f) { throw", script, StringComparison.Ordinal);

        var addTonelab = Regex.Match(script, @"function Add-Tonelab\([^)]*\) \{\s*Test-TonelabPin");
        Assert.True(addTonelab.Success, "Add-Tonelab must check the pin before it copies anything.");

        var add = script.IndexOf("Add-Tonelab -PublishDir $publishDir", StringComparison.Ordinal);
        Assert.True(add > 0, "the release never calls Add-Tonelab on the payload.");
        Assert.True(add < script.IndexOf("Test-PayloadBinaries -PayloadDir $publishDir", StringComparison.Ordinal), "tonelab must be in the payload before its binaries are checked");
        Assert.True(add < script.IndexOf("Join-Path $appDir 'payload-manifest.json'", StringComparison.Ordinal), "tonelab must be in the payload before the manifest is hashed");
    }

    [Fact]
    public void AMissingOrUnpinnedBuild_FailsInPreflight_UnlessSkipTonelabSaysSo()
    {
        var script = Script();
        Assert.Matches(@"\[switch\]\$SkipTonelab", script);
        Assert.Contains(".PARAMETER SkipTonelab", script, StringComparison.Ordinal);
        Assert.Contains("[string]$TonelabDir = $env:DOTCHARTER_TONELAB_DIR", script, StringComparison.Ordinal);
        Assert.Matches(@"if \(-not \$SkipUi -and -not \$SkipTonelab\) \{\s*Write-Step 'tonelab[^']*'\s*\$tonelabCommit = Test-TonelabPin", script);
        Assert.Contains("Write-Warn2 'SKIPPING tonelab (-SkipTonelab)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TonelabSource_IsNotInThisRepository()
    {
        Assert.False(Directory.Exists(Path.Combine(Root, "native", "tonelab")), "tonelab's source belongs in its own repository; ship the pinned build.");
        var tracked = Directory.EnumerateFiles(Root, "*.vcxproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(".claude", "worktrees"), StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains("JuceSharp", StringComparison.Ordinal) &&
                        TonelabProject().IsMatch(Path.GetFileName(f)))
            .ToList();
        Assert.True(tracked.Count == 0, $"tonelab projects in the repository: {string.Join(", ", tracked)}");
    }

    [Fact]
    public void AddTonelab_RunUnderPwsh_CopiesThePinnedFiles_AndRefusesAChangedOne()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-tonelab-release-").FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(scratch, "build")).FullName;
            var pinDir = Directory.CreateDirectory(Path.Combine(scratch, "pin")).FullName;
            var files = new[] { TonelabInstall.DllName, TonelabInstall.HelperName, "tonelab.ini" };
            var hashes = new Dictionary<string, string>();
            foreach (var name in files)
            {
                File.WriteAllText(Path.Combine(source, name), "fake " + name);
                hashes[name] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(source, name))));
            }

            File.WriteAllText(Path.Combine(pinDir, "tonelab.json"), JsonSerializer.Serialize(new { commit = "abc1234", files = hashes }));
            File.WriteAllText(Path.Combine(pinDir, "LICENSE.txt"), "MIT License");

            var shipped = RunAddTonelab(scratch, source, pinDir, Path.Combine(scratch, "publish"));
            Assert.True(shipped.ExitCode == 0, shipped.Output);
            foreach (var name in files.Append("LICENSE.txt"))
                Assert.True(File.Exists(Path.Combine(scratch, "publish", "tools", TonelabInstall.FolderName, name)), $"{name} was not shipped.{Environment.NewLine}{shipped.Output}");

            File.AppendAllText(Path.Combine(source, "tonelab.ini"), "changed");
            var refused = RunAddTonelab(scratch, source, pinDir, Path.Combine(scratch, "publish2"));
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Contains("is not the pinned build abc1234", refused.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    // Runs New-Release.ps1's own Test-TonelabPin and Add-Tonelab, lifted out by the PowerShell parser.
    private static (int ExitCode, string Output) RunAddTonelab(string scratch, string source, string pinDir, string publish)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var releaseScript = Path.Combine(Root, "tools", "release", "New-Release.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile({{Quote(releaseScript)}}, [ref]$null, [ref]$null)
            foreach ($name in 'Test-TonelabPin', 'Add-Tonelab') {
                $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
                . ([scriptblock]::Create($fn.Extent.Text))
            }
            $TonelabFiles = @('{{TonelabInstall.DllName}}', '{{TonelabInstall.HelperName}}', 'tonelab.ini')
            $TonelabPinDir = {{Quote(pinDir)}}
            $TonelabDir = {{Quote(source)}}
            Add-Tonelab -PublishDir {{Quote(publish)}} | Out-Null
            """;
        var file = Path.Combine(scratch, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var info = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", file })
            info.ArgumentList.Add(argument);

        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("pwsh (PowerShell 7+) is not on PATH, so New-Release.ps1's tonelab step cannot be executed here.");
            return default;
        }

        Assert.NotNull(process);
        using (process)
        {
            var stdout = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEnd();
            // A deadlock detector for a hung child, never a budget.
            Assert.True(process.WaitForExit(120_000), "pwsh did not finish within 120 s.");
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr);
        }
    }

    [GeneratedRegex("^tonelab", RegexOptions.IgnoreCase)]
    private static partial Regex TonelabProject();
}
