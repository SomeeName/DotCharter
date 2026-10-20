using DotCharter.Tests.Services.Update;

namespace DotCharter.Tests.Infrastructure;

public sealed class ReleaseProjectClosureTests
{
    [Theory]
    [InlineData(false, false, "Core|UI")]
    [InlineData(true, false, "Core|UI")]
    [InlineData(true, true, "Core|Optional|UI")]
    [InlineData(false, true, "Core|UI")]
    public void Public_builds_ignore_plugin_projects_while_plugin_builds_include_what_is_there(
        bool optionalExists, bool includePlugins, string expected) =>
        Assert.Equal(expected, RunClosure(optionalExists, includePlugins, missingRequired: false));

    [Fact]
    public void Missing_required_projects_are_never_ignored() =>
        Assert.Equal("unresolved", RunClosure(optionalExists: false, includePlugins: false, missingRequired: true));

    [Fact]
    public void Cleanup_passes_the_release_build_mode_to_the_project_walker()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        Assert.Contains("Get-ProjectClosure $RootProjects -IncludePlugins:$WithPlugins", script, StringComparison.Ordinal);
    }

    private static string RunClosure(bool optionalExists, bool includePlugins, bool missingRequired)
    {
        using var scratch = new TempDirectory();
        var ui = Path.Combine(scratch.Path, "UI", "UI.csproj");
        var core = Path.Combine(scratch.Path, "Core", "Core.csproj");
        var optional = Path.Combine(scratch.Path, "plugins", "Optional", "Optional.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(ui)!);
        File.WriteAllText(ui, """
            <Project><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup>
            <ItemGroup Condition="'$(DotCharterPlugins)' != 'false'">
            <ProjectReference Include="../plugins/*/*.csproj" /></ItemGroup></Project>
            """);
        if (!missingRequired)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(core)!);
            File.WriteAllText(core, "<Project />");
        }
        if (optionalExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(optional)!);
            File.WriteAllText(optional, "<Project />");
        }
        var builder = Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1").Replace("'", "''");
        return HeadlessShardsModule.RunModuleWithHost(scratch.Path, $$"""
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{builder}}', [ref]$null, [ref]$null)
            $function = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-ProjectClosure'}, $true)
            Invoke-Expression $function.Extent.Text
            $RepoRoot = '{{scratch.Path.Replace("'", "''")}}'
            try {
                $projects = @(Get-ProjectClosure @('{{ui.Replace("'", "''")}}') -IncludePlugins:${{includePlugins.ToString().ToLowerInvariant()}})
                ($projects | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } | Sort-Object) -join '|'
            } catch {
                if ($_.Exception.Message -notlike '*does not resolve*') { throw }
                'unresolved'
            }
            """, ["tools", "release", "FfmpegInventory.psm1"], "pwsh.exe");
    }
}
