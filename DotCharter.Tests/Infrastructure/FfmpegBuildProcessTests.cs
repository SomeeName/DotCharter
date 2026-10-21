using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tests.Services.Update;

namespace DotCharter.Tests.Infrastructure;

public sealed class FfmpegBuildProcessTests
{
    [Fact]
    public void A_failed_tool_is_not_reported_as_a_success()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The build launcher uses Windows tools.");
        using var scratch = new TempDirectory();
        var builder = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1").Replace("'", "''");
        var result = HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{builder}}', [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-Low'}, $true)
            Invoke-Expression $function.Extent.Text
            $AffinityMask = $null
            try {
                Invoke-Low (Join-Path $env:SystemRoot 'System32/cmd.exe') @('/c', 'exit 17')
                'accepted'
            } catch { 'refused: ' + $_.Exception.Message }
            """, ["tools", "release", "FfmpegInventory.psm1"], "pwsh.exe");
        Assert.Contains("exited with 17", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_tool_keeps_its_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The build launcher uses Windows tools.");
        using var scratch = new TempDirectory();
        var builder = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1").Replace("'", "''");
        var result = HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{builder}}', [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-Low'}, $true)
            Invoke-Expression $function.Extent.Text
            $AffinityMask = $null
            Invoke-Low (Join-Path $env:SystemRoot 'System32/cmd.exe') @('/c', 'echo tool output')
            """, ["tools", "release", "FfmpegInventory.psm1"], "pwsh.exe");
        Assert.Equal("tool output", result);
    }

    [Fact]
    public void Only_a_complete_cached_toolchain_is_reused()
    {
        using var scratch = new TempDirectory();
        var builder = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1").Replace("'", "''");
        var result = HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{builder}}', [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-MsysToolchain'}, $true)
            Invoke-Expression $function.Extent.Text
            $root = '{{scratch.Path.Replace("'", "''")}}'
            foreach ($name in 'usr/bin/bash.exe', 'usr/bin/make.exe', 'usr/bin/diff.exe',
                              'mingw64/bin/gcc.exe', 'mingw64/bin/nasm.exe', 'mingw64/bin/pkg-config.exe') {
                $path = Join-Path $root $name
                New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
                Set-Content $path 'fixture'
            }
            $complete = Test-MsysToolchain $root
            Remove-Item (Join-Path $root 'mingw64/bin/gcc.exe')
            $missingCompiler = Test-MsysToolchain $root
            $absent = Test-MsysToolchain (Join-Path $root 'absent')
            "$complete|$missingCompiler|$absent"
            """, ["tools", "release", "FfmpegInventory.psm1"], "pwsh.exe");
        Assert.Equal("True|False|False", result);
        var script = File.ReadAllText(builder.Replace("''", "'"));
        Assert.Contains("if ((Test-MsysToolchain $msysRoot) -and (Get-MsysPinMismatches).Count -eq 0)", script, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The toolchain is pinned like the sources: every MSYS2 package is a file
    ///     named in <c>ffmpeg-min/msys2-packages.json</c> with its SHA-256, installed
    ///     with <c>pacman -U</c> from the verified file. No <c>pacman -S</c> (which
    ///     takes whatever the repository serves today) is left anywhere.
    /// </summary>
    [Fact]
    public void Every_msys2_package_is_a_pinned_file_and_pacman_never_syncs()
    {
        var root = TestPaths.RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "release", "Build-MinimalFfmpeg.ps1")).Replace("\r\n", "\n");
        Assert.DoesNotMatch(@"pacman\s+-S", script);
        Assert.Contains("pacman -U --noconfirm", script, StringComparison.Ordinal);
        Assert.Contains("'msys2-packages.json'", script, StringComparison.Ordinal);
        Assert.Matches(@"Save-PinnedFile -Urls [^\n]*-Sha256 \$package\.sha256", script);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "release", "ffmpeg-min", "msys2-packages.json")));
        foreach (var mirror in manifest.RootElement.GetProperty("mirrors").EnumerateArray())
            Assert.StartsWith("https://", mirror.GetString(), StringComparison.Ordinal);
        var names = new List<string>();
        foreach (var package in manifest.RootElement.GetProperty("packages").EnumerateArray())
        {
            var name = package.GetProperty("name").GetString()!;
            var version = package.GetProperty("version").GetString()!;
            names.Add(name);
            Assert.Matches("^[0-9a-f]{64}$", package.GetProperty("sha256").GetString()!);
            Assert.Matches("^" + Regex.Escape(name) + "-" + Regex.Escape(version) + @"-(x86_64|any)\.pkg\.tar\.zst$",
                package.GetProperty("file").GetString()!);
            Assert.Contains(package.GetProperty("repo").GetString(), new[] { "msys/x86_64", "mingw/mingw64" });
        }

        foreach (var tool in new[] { "make", "diffutils", "mingw-w64-x86_64-gcc", "mingw-w64-x86_64-nasm", "mingw-w64-x86_64-pkgconf" })
            Assert.Contains(tool, names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_installed_toolchain_that_is_not_exactly_the_pins_is_refused()
    {
        using var scratch = new TempDirectory();
        var builder = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1").Replace("'", "''");
        var result = HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{builder}}', [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Compare-MsysPinnedPackages'}, $true)
            Invoke-Expression $function.Extent.Text
            $pins = @(
                [pscustomobject]@{ name = 'make'; version = '4.4.1-3' }
                [pscustomobject]@{ name = 'mingw-w64-x86_64-gcc'; version = '16.2.0-4' })
            $exact = @(Compare-MsysPinnedPackages @('bash 5.2-1', 'make 4.4.1-3', 'mingw-w64-x86_64-gcc 16.2.0-4') $pins)
            $newer = @(Compare-MsysPinnedPackages @('make 4.4.1-3', 'mingw-w64-x86_64-gcc 16.3.0-1') $pins)
            $missing = @(Compare-MsysPinnedPackages @('make 4.4.1-3') $pins)
            $extra = @(Compare-MsysPinnedPackages @('make 4.4.1-3', 'mingw-w64-x86_64-gcc 16.2.0-4', 'mingw-w64-x86_64-evil 1-1') $pins)
            "$($exact.Count)|$($newer -join ';')|$($missing -join ';')|$($extra -join ';')"
            """, ["tools", "release", "FfmpegInventory.psm1"], "pwsh.exe");
        var parts = result.Split('|');
        Assert.Equal("0", parts[0]);
        Assert.Contains("mingw-w64-x86_64-gcc", parts[1], StringComparison.Ordinal);
        Assert.Contains("16.3.0-1", parts[1], StringComparison.Ordinal);
        Assert.Contains("mingw-w64-x86_64-gcc", parts[2], StringComparison.Ordinal);
        Assert.Contains("mingw-w64-x86_64-evil", parts[3], StringComparison.Ordinal);
    }
}
