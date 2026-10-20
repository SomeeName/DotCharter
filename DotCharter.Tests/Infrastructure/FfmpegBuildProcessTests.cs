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
        Assert.Contains("if (Test-MsysToolchain (Join-Path $WorkRoot 'msys64'))", script, StringComparison.Ordinal);
        Assert.Contains("else {\n    Invoke-Low $bash @('-lc', \"pacman", script.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }
}
