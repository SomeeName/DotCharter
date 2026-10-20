using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/setup/Bootstrap.ps1</c> is the first thing a new contributor runs
///     (<c>docs/dev/GETTING-STARTED.md</c>). It must exist, parse, stay READ-ONLY and keep
///     the ABI comparison that makes it worth running.
/// </summary>
public sealed class BootstrapScriptLintTests
{
    private static string ScriptPath() => Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "Bootstrap.ps1");

    /// <summary>Code only: whole-line comments and <c>&lt;# #&gt;</c> blocks dropped.</summary>
    private static string CodeOnly()
    {
        var text = Regex.Replace(File.ReadAllText(ScriptPath()), "<#.*?#>", "", RegexOptions.Singleline);
        return string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    [Fact]
    public void The_script_exists_and_the_getting_started_guide_names_it()
    {
        Assert.True(File.Exists(ScriptPath()), "tools/setup/Bootstrap.ps1 is missing.");
        var guide = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "docs", "dev", "GETTING-STARTED.md"));
        Assert.Contains("tools/setup/Bootstrap.ps1", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_parses_without_errors()
    {
        var pwsh = FindPwsh();
        Assert.SkipUnless(pwsh is not null, "pwsh is not on PATH; cannot parse the script.");

        var command = "$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile("
                      + "$env:BOOTSTRAP_SCRIPT,[ref]$t,[ref]$e);if($e.Count){$e|ForEach-Object{$_.Message};exit 1}";
        var info = new ProcessStartInfo(pwsh!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        info.Environment["BOOTSTRAP_SCRIPT"] = ScriptPath();

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The PowerShell parser did not finish.");
        Assert.True(process.ExitCode == 0, "Bootstrap.ps1 has parse errors:\n" + output);
    }

    [Fact]
    public void The_script_never_installs_or_changes_anything()
    {
        // Lines that only PRINT advice (Write-Host / Write-Check) may name the commands the user runs.
        var code = string.Join('\n', CodeOnly().Split('\n')
            .Where(l => !l.Contains("Write-Host", StringComparison.Ordinal) && !l.Contains("Write-Check", StringComparison.Ordinal)));
        string[] forbidden =
        [
            @"\bInstall-\w+", @"\bwinget\b", @"\bchoco\b", @"\bSet-\w+", @"\bNew-Item\b", @"\bRemove-\w+",
            @"\bOut-File\b", @"\bAdd-Content\b", @"\bSet-Content\b", @"\bStart-Process\b", @"\bInvoke-WebRequest\b",
            @"\bInvoke-RestMethod\b", @"\bgit\s+(clone|fetch|pull|checkout|submodule|reset|clean|add|commit)\b",
            @"\bdotnet\s+(build|restore|publish|workload|tool)\b", @"\bcmake\b.*--build"
        ];
        var hits = forbidden.Where(p => Regex.IsMatch(code, p, RegexOptions.IgnoreCase)).ToList();
        Assert.True(hits.Count == 0, "Bootstrap.ps1 must stay read-only; it matches: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_script_compares_the_dll_with_the_header_abi_version()
    {
        var code = CodeOnly();
        Assert.Contains("DCA_ABI_VERSION", code, StringComparison.Ordinal);
        Assert.Contains("dca_abi_version", code, StringComparison.Ordinal);
        Assert.Contains("juce_sharp.dll", code, StringComparison.Ordinal);
    }

    private static string? FindPwsh()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : ["pwsh"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
