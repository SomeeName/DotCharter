using System.Diagnostics;
using System.Text;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Runs a PowerShell body against the REAL <c>tools/ci/HeadlessShards.psm1</c>
///     under Windows PowerShell 5.1 — the oldest host <c>Invoke-Gate.ps1</c>
///     supports — and returns its trimmed stdout. Shared by
///     <c>HeadlessShardPlanTests</c> and
///     <c>HeadlessRaceRerunDecisionTests</c>: the module is tested as
///     itself, never as a C# re-implementation. Missing PowerShell is a skip,
///     never a failure. <see cref="RunModule" /> does the same for another
///     repo module (<c>MergeTrainModuleTests</c>, <c>ReleasePluginRefusalTests</c>).
///     Plain names, not crefs: the plugin's tests link this file (#925).
/// </summary>
internal static class HeadlessShardsModule
{
    public static string Run(string scratchDirectory, string body) =>
        RunModule(scratchDirectory, body, "tools", "ci", "HeadlessShards.psm1");

    /// <summary>Imports the repo module at <paramref name="modulePath" /> (repo-relative parts) and runs <paramref name="body" />.</summary>
    public static string RunModule(string scratchDirectory, string body, params string[] modulePath) =>
        RunModuleWithHost(scratchDirectory, body, modulePath, "powershell.exe");

    public static string RunModuleWithHost(string scratchDirectory, string body, string[] modulePath, string host)
    {
        var module = Path.Combine([TestPaths.RepoRoot(), .. modulePath]);
        var script = $"$ErrorActionPreference = 'Stop'\nImport-Module '{module.Replace("'", "''")}' -Force\n{body}\n";
        var file = Path.Combine(scratchDirectory, "case-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Windows PowerShell is not available, so the shard module cannot be executed here.");
            return "";
        }

        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEnd();
        // A deadlock detector for a hung child, never a budget.
        Assert.True(process.WaitForExit(120_000), "powershell did not finish within 120 s.");
        var stdout = stdoutTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"powershell exited {process.ExitCode}.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
        return stdout.Trim();
    }
}
