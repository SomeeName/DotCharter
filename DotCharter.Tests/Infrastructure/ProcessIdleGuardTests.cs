namespace DotCharter.Tests.Infrastructure;

public sealed class ProcessIdleGuardTests
{
    [Fact]
    public void LoadedExport_RemainsAlive_AndOutputRenewsTheIdleWindow()
    {
        var clock = new StepTimeProvider();
        var guard = new ProcessIdleGuard(ProcessIdleGuard.PublicExportTimeout, clock);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(guard.Expired, "The measured loaded export (224 s) must fit inside the hang guard.");
        guard.ReportActivity();
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.False(guard.Expired);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(guard.Expired, "A child silent beyond the idle window must still be killed.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HungChild_IsKilled_AndItsOutputIsKept(bool totalCap)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "[Console]::Error.WriteLine(\"ready:$PID\"); [Threading.Thread]::Sleep(-1)" })
            info.ArgumentList.Add(argument);

        var clock = new StepTimeProvider();
        var observed = new System.Text.StringBuilder();
        var failure = Assert.ThrowsAny<Exception>(() => BoundedProcess.Run(info,
            totalCap ? TimeSpan.FromMinutes(3) : Timeout.InfiniteTimeSpan, "silent child", idleCap: ProcessIdleGuard.PublicExportTimeout, timeProvider: clock, onOutput: chunk =>
            {
                observed.Append(chunk);
                if (observed.ToString().Contains('\n', StringComparison.Ordinal)) clock.Advance(TimeSpan.FromMinutes(totalCap ? 4 : 16));
            }));

        Assert.Contains(totalCap ? "180 s total or 900 s of silence" : "900 s of silence", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ready", failure.Message, StringComparison.Ordinal);
        var marker = System.Text.RegularExpressions.Regex.Match(failure.Message, @"ready:(\d+)");
        var childId = int.Parse(marker.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcesses(), p => p.Id == childId);
    }

    [Fact]
    public void SlowTalkingChild_CanExceedTheOldTotalCap_WithoutBeingKilled()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-idle-progress-").FullName;
        try
        {
            var path = scratch.Replace("'", "''", StringComparison.Ordinal);
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe");
            var script = $"foreach ($i in 1..5) {{ [Console]::Write(\"tick:$i;\"); while (-not [IO.File]::Exists('{path}/' + $i)) {{ [Threading.Thread]::Sleep(10) }} }}";
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
                info.ArgumentList.Add(argument);
            var clock = new StepTimeProvider();
            var observed = new System.Text.StringBuilder();
            var next = 1;
            var result = BoundedProcess.Run(info, Timeout.InfiniteTimeSpan, "talking child",
                idleCap: ProcessIdleGuard.PublicExportTimeout, timeProvider: clock, onOutput: chunk =>
                {
                    observed.Append(chunk);
                    if (!observed.ToString().Contains($"tick:{next};", StringComparison.Ordinal)) return;
                    clock.Advance(TimeSpan.FromMinutes(4));
                    File.WriteAllText(Path.Combine(scratch, next.ToString(System.Globalization.CultureInfo.InvariantCulture)), "continue");
                    next++;
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("tick:5", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromMinutes(20), clock.GetElapsedTime(0));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }
}
