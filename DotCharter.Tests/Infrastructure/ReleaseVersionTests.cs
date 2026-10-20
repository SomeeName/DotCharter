using DotCharter.Tests.Services.Update;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Infrastructure;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void Release_fixture_readme_and_checksums_retain_prerelease()
    {
        using var scratch = new TempDirectory();
        var script = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1").Replace("'", "''");
        var output = HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $script = '{{script}}'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-SplitInstallLayout'}, $true)
            Invoke-Expression $function.Extent.Text
            $Version = '1.0.0-beta.1'
            $ReleaseIdentity = Get-ReleaseIdentity $Version
            $root = '{{scratch.Path.Replace("'", "''")}}'
            $app = Join-Path $root 'app'
            New-Item -ItemType Directory -Path $app | Out-Null
            foreach ($name in 'DotCharter.runtimeconfig.json', 'DotCharter.deps.json', 'hostfxr.dll', 'coreclr.dll') {
                Set-Content (Join-Path $app $name) 'fixture'
            }
            New-SplitInstallLayout $root $app
            $readme = Get-Content (Join-Path $root 'README.txt') -Raw
            if (-not $readme.Contains('DotCharter 1.0.0-beta.1') -or
                -not $readme.Contains('https://github.com/SomeeName/DotCharter/tree/v1.0.0-beta.1')) { throw 'README lost release identity' }
            $asset = Join-Path $root $ReleaseIdentity.ZipName
            Set-Content $asset 'fixture'
            $assets = @($asset)
            $OutputDir = $root
            $text = Get-Content $script -Raw
            $start = $text.IndexOf('$checksumPath = Join-Path')
            $end = $text.IndexOf('$assets = @($assets) + $checksumPath', $start)
            Invoke-Expression $text.Substring($start, $end - $start)
            Get-Content $checksumPath
            """, ["tools", "release", "ReleaseVersion.psm1"], "pwsh.exe");
        var checksums = ReleaseChecksums.Parse(output);
        Assert.Single(checksums);
        Assert.Contains("DotCharter-v1.0.0-beta.1-win-x64.zip", checksums.Keys);
    }

    [Fact]
    public void Release_asset_identity_keeps_the_prerelease()
    {
        using var scratch = new TempDirectory();
        var output = HeadlessShardsModule.RunModule(scratch.Path,
            "Assert-ReleaseVersionSelfTest; (Get-ReleaseIdentity '1.0.0-beta.1').ZipName",
            "tools", "release", "ReleaseVersion.psm1");
        Assert.Equal("DotCharter-v1.0.0-beta.1-win-x64.zip", output);
    }

    [Fact]
    public void Release_script_uses_the_identity_and_publishes_betas_as_latest_normal_releases()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        Assert.Contains("$ReleaseIdentity = Get-ReleaseIdentity $Version", script, StringComparison.Ordinal);
        Assert.Contains("$ReleaseIdentity.ZipName", script, StringComparison.Ordinal);
        Assert.Contains("$ReleaseIdentity.RootName", script, StringComparison.Ordinal);
        Assert.Contains("$($ReleaseIdentity.SourceUrl)", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseVersionSelfTest", script, StringComparison.Ordinal);
        var command = script[script.LastIndexOf("gh release create v$Version -R", StringComparison.Ordinal)..];
        Assert.Contains("--latest", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", command, StringComparison.Ordinal);
        Assert.Contains("\"-p:Version=$Version\"", script, StringComparison.Ordinal);
    }
}
