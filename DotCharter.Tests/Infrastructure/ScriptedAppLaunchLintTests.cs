using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every scripted launch of the app under <c>tools/</c> goes through
///     <c>tools/release/HiddenDesktop.psm1</c> (<c>Start-HiddenDesktopProcess</c>),
///     which starts it on a never-shown Win32 desktop (#419).
/// </summary>
/// <remarks>
///     A minimised or no-activate launch is not enough: DotCharter activates its
///     own window when it shows, and on 2026-10-07 9 of 42 such automated launches
///     still ended with the app in the foreground, possibly in front of a game. A
///     process on another desktop cannot own the input desktop's foreground.
///     The lint reads every script under <c>tools/</c>: a <c>Start-Process</c>,
///     <c>[Diagnostics.Process]::Start</c> or <c>Invoke-Item</c> in a script that
///     names the app's exe or prepares its environment (<c>DOTCHARTER_DATA_ROOT</c>,
///     <c>DOTCHARTER_UI_AUTOPILOT</c>) is a visible launch, and so is any line that
///     starts a process (the call operator <c>&amp;</c> included) and names the exe
///     itself. A bare <c>&amp; $exe</c> is NOT flagged: PowerShell variables are
///     case-insensitive and <c>New-Release.ps1</c> runs ffmpeg as <c>&amp; $Exe</c>,
///     so text alone cannot tell the two apart.
/// </remarks>
public sealed class ScriptedAppLaunchLintTests
{
    /// <summary>
    ///     Scripts that must launch on the VISIBLE desktop, each with its reason.
    ///     Only add one that truly cannot use the helper.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedVisibleLaunches = new(StringComparer.Ordinal)
    {
        ["tools/agent/screen/Capture-App.ps1"] =
            "screenshots the secondary monitor with CopyFromScreen; a hidden desktop is never composited, "
            + "and CopyFromScreen there throws 'invalid handle' (measured 2026-10-08). A manual capture tool.",
        ["tools/readme-media/AppDriver.psm1"] =
            "the README shots read the window back with PrintWindow, which a hidden desktop never composites; "
            + "the window is placed on the non-primary monitor without activation (SWP_NOACTIVATE), every input is "
            + "a posted message and the launch uses an isolated DOTCHARTER_DATA_ROOT. A manual capture tool (#912).",
    };

    private static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".py", ".sh", ".cmd", ".bat"];

    private static readonly Regex AppExe =
        new(@"DotCharter(\.RigLab|\.Gallery)?\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PreparesAppLaunch =
        new(@"DotCharter(\.RigLab|\.Gallery)?\.exe|DOTCHARTER_DATA_ROOT|DOTCHARTER_UI_AUTOPILOT",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StartsAProcess = new(
        @"\bStart-Process\b|Process\]::Start\(|\bInvoke-Item\b|\bsubprocess\.|\bos\.startfile\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>PowerShell's call operator, flagged only on a line that also names the app's exe.</summary>
    private static readonly Regex CallOperator = new(@"(^|[\s(=;])&\s", RegexOptions.CultureInvariant);

    private static string ToolsRoot() => Path.Combine(TestPaths.RepoRoot(), "tools");

    private static string Relative(string path) =>
        Path.GetRelativePath(TestPaths.RepoRoot(), path).Replace('\\', '/');

    /// <summary>The non-comment lines of a script (whole-line <c>#</c> / <c>REM</c> comments and <c>&lt;# #&gt;</c> blocks dropped).</summary>
    private static IEnumerable<(int Line, string Text)> CodeLines(string path)
    {
        var inBlock = false;
        var number = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            var line = raw.Trim();
            if (inBlock)
            {
                if (line.Contains("#>", StringComparison.Ordinal)) inBlock = false;
                continue;
            }

            if (line.StartsWith("<#", StringComparison.Ordinal))
            {
                inBlock = !line.Contains("#>", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith('#') || line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (number, raw);
        }
    }

    internal static List<string> VisibleLaunches(string toolsRoot, Func<string, string> relative)
    {
        var found = new List<string>();
        var scripts = Directory.EnumerateFiles(toolsRoot, "*", SearchOption.AllDirectories)
            .Where(f => ScriptExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        foreach (var script in scripts)
        {
            var lines = CodeLines(script).ToList();
            var prepares = lines.Any(l => PreparesAppLaunch.IsMatch(l.Text));
            foreach (var (line, text) in lines)
            {
                var namesApp = AppExe.IsMatch(text);
                if ((StartsAProcess.IsMatch(text) && (prepares || namesApp)) || (namesApp && CallOperator.IsMatch(text)))
                    found.Add($"{relative(script)}:{line}: {text.Trim()}");
            }
        }

        return found;
    }

    [Fact]
    public void No_script_under_tools_starts_the_app_on_the_visible_desktop()
    {
        var offenders = VisibleLaunches(ToolsRoot(), Relative)
            .Where(o => !AllowedVisibleLaunches.ContainsKey(o[..o.IndexOf(':', StringComparison.Ordinal)]))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These scripts start DotCharter on the VISIBLE desktop, where it can take the foreground (#419). "
            + "Use Start-HiddenDesktopProcess from tools/release/HiddenDesktop.psm1:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>An allow-list entry for a script that no longer launches anything is a stale exemption.</summary>
    [Fact]
    public void Every_allowed_visible_launch_still_exists()
    {
        var launches = VisibleLaunches(ToolsRoot(), Relative);
        foreach (var allowed in AllowedVisibleLaunches.Keys)
        {
            Assert.True(launches.Any(l => l.StartsWith(allowed + ":", StringComparison.Ordinal)),
                $"{allowed} is allowed a visible launch but no longer makes one: delete its entry.");
        }
    }

    /// <summary>The detector itself: a plain Start-Process of the app is caught, the helper is not.</summary>
    [Fact]
    public void The_detector_flags_a_visible_launch_and_passes_the_helper()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-launch-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllLines(Path.Combine(root, "bad.ps1"),
            [
                "# Start-Process in a comment is not a launch",
                "$exe = Join-Path $dir 'DotCharter.exe'",
                "$p = Start-Process -FilePath $exe -PassThru",
            ]);
            File.WriteAllLines(Path.Combine(root, "bad-call.ps1"),
                ["& \"$dir\\DotCharter.exe\" --smoke", "$p = & (Join-Path $dir 'DotCharter.exe')"]);
            File.WriteAllLines(Path.Combine(root, "good.ps1"),
            [
                "$env:DOTCHARTER_DATA_ROOT = $root",
                "$launch = Start-HiddenDesktopProcess -FilePath (Join-Path $dir 'DotCharter.exe')",
                "& dotnet build",
                "$out = @(& $Exe @Arguments 2>$null)",
            ]);

            var found = VisibleLaunches(root, p => Path.GetFileName(p));

            var flagged = found.Select(f => string.Join(':', f.Split(':').Take(2))).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "bad-call.ps1:1", "bad-call.ps1:2", "bad.ps1:3" }, flagged);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The release smoke and its self-test use the helper, and the helper really sets the desktop.</summary>
    [Fact]
    public void The_release_launch_smoke_runs_on_a_hidden_desktop()
    {
        var release = Path.Combine(ToolsRoot(), "release");
        var script = File.ReadAllText(Path.Combine(release, "New-Release.ps1"));
        var helper = File.ReadAllText(Path.Combine(release, "HiddenDesktop.psm1"));

        Assert.Contains("Import-Module (Join-Path $PSScriptRoot 'HiddenDesktop.psm1')", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\$launch = Start-HiddenDesktopProcess -FilePath \$exe"), script);
        Assert.Contains("Stop-HiddenDesktopProcess $launch", script, StringComparison.Ordinal);
        Assert.Contains("Get-ChildProcess -ParentId $launch.Id", script, StringComparison.Ordinal);
        Assert.Contains("$probeLaunch = Start-HiddenDesktopProcess", script, StringComparison.Ordinal);
        Assert.Contains("CreateDesktopW(desktopName,", helper, StringComparison.Ordinal);
        Assert.Contains("si.lpDesktop = desktopName;", helper, StringComparison.Ordinal);
    }
}
