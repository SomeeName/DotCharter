using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Controlled writers cannot restore each other's revoked evidence.</summary>
public sealed class GateRecordConcurrencyTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-record-lock-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transactions_ReReadUnderOneLock_InsteadOfWritingAStaleSnapshot(bool save)
    {
        var output = Run($$$"""
            [void](Save-GateRecord -Tree ('a' * 40) -Gate -Headless -Source seed)
            $other = [PowerShell]::Create()
            [void]$other.AddScript(@'
            Import-Module '{{{Module}}}' -Force
            try {
                $a = @{ Trees = @(('a' * 40)); Kinds = @('{{{(save ? "headless" : "gate")}}}') }
                if ((Get-Command Start-GateRecordRun).Parameters.ContainsKey('LockTimeoutMilliseconds')) { $a.LockTimeoutMilliseconds = 0 }
                Start-GateRecordRun @a
                'done'
            } catch { 'busy' }
            '@)
            $m = Get-Module ReleaseCache
            & $m {
                param($other)
                $script:OriginalRead = (Get-Command Read-GateRecord).ScriptBlock
                $script:OtherWriter = $other
                function script:Read-GateRecord([string]$Path) {
                    $snapshot = & $script:OriginalRead $Path
                    if ($script:OtherWriter) {
                        $writer = $script:OtherWriter; $script:OtherWriter = $null
                        $global:OtherResult = ($writer.Invoke() | Out-String).Trim()
                    }
                    return $snapshot
                }
            } $other
            try {
                if (${{{save.ToString().ToLowerInvariant()}}}) { [void](Save-GateRecord -Tree ('a' * 40) -Gate -Source writer) }
                else { Start-GateRecordRun -Trees @(('a' * 40)) -Kinds headless }
                if ($global:OtherResult -eq 'busy') { $null = $other.Invoke() }
            } finally { $other.Dispose() }
            "gate=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind gate))"
            "headless=$([bool](Find-GateRecord -Trees @(('a' * 40)) -Kind headless))"
            """);

        Assert.Contains($"gate={save}", output, StringComparison.Ordinal);
        Assert.Contains("headless=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordLockHeldByAnotherProcess_FailsWithABoundedDiagnostic_AndReleasesAfterward()
    {
        var cache = Path.Combine(_scratch, "cache");
        Directory.CreateDirectory(cache);
        using (var held = new FileStream(Path.Combine(cache, ".gate-records.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var output = Run("""
                $a = @{ Trees = @(('a' * 40)); Kinds = @('gate') }
                if ((Get-Command Start-GateRecordRun).Parameters.ContainsKey('LockTimeoutMilliseconds')) { $a.LockTimeoutMilliseconds = 1 }
                try { Start-GateRecordRun @a; 'unexpected-success' } catch { $_.Exception.Message }
                """);
            Assert.Contains("record store lock", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unexpected-success", output, StringComparison.Ordinal);
        }

        Assert.Contains("saved", Run("[void](Save-GateRecord -Tree ('a' * 40) -Gate -Source after); 'saved'"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARedCompletion_RemovesAGreenSavedDuringTheRun()
    {
        var output = Run("""
            $t = @(('a' * 40))
            Start-GateRecordRun -Trees $t -Kinds gate
            [void](Save-GateRecord -Tree $t[0] -Gate -Source concurrent-green)
            if (Get-Command Complete-GateRecordRun -ErrorAction SilentlyContinue) {
                Complete-GateRecordRun -Trees $t -Kinds gate -AcceptedKinds @()
            }
            "green=$([bool](Find-GateRecord -Trees $t -Kind gate))"
            """);

        Assert.Contains("green=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttemptKilledBeforeGrading_DoesNotEraseAnotherCompletedGreen()
    {
        var output = Run("""
            $t = @(('a' * 40))
            Start-GateRecordRun -Trees $t -Kinds gate
            [void](Save-GateRecord -Tree $t[0] -Gate -Source completed-B)
            # Attempt A dies here: no graded completion and no save.
            "source=$((Find-GateRecord -Trees $t -Kind gate).Source)"
            """);

        Assert.Contains("source=completed-B", output, StringComparison.Ordinal);
    }

    private static string Module => Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ReleaseCache.psm1");

    private string Run(string body) => HeadlessShardsModule.RunModule(_scratch,
        $"$env:DOTCHARTER_RELEASE_CACHE = '{Path.Combine(_scratch, "cache")}'\n{body}", "tools", "release", "ReleaseCache.psm1");
}
