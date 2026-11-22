using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>GitHub's delayed merge state must not turn a landed train into a premature failure.</summary>
public sealed class MergeTrainConfirmationTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-train-confirm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData("delayed", 1)]
    [InlineData("staggered", 2)]
    [InlineData("transient", 1)]
    [InlineData("missing-commit", 1)]
    [InlineData("malformed", 1)]
    public void Confirmation_WaitsForEveryMergedCommit_ThenCommentsOnce(string scenario, int count)
    {
        using var result = Run(scenario, count);
        var row = result.RootElement;

        Assert.Equal("landed", row.GetProperty("Outcome").GetString());
        Assert.Equal(1, row.GetProperty("Pushes").GetInt32());
        Assert.Equal(count, row.GetProperty("Comments").GetArrayLength());
        Assert.Equal(0, row.GetProperty("EarlyComments").GetInt32());
        Assert.True(row.GetProperty("DelaySeconds").GetDouble() > 0);
        Assert.Equal(Enumerable.Range(1, count), row.GetProperty("Comments").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public void Confirmation_AtTheCap_NamesOnlyPendingPrs_AndSaysTheTipIsAlreadyOnTheBase()
    {
        using var result = Run("forever", 2);
        var row = result.RootElement;
        var outcome = row.GetProperty("Outcome").GetString()!;

        Assert.Contains("could not confirm #2 MERGED", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("#1", outcome, StringComparison.Ordinal);
        Assert.Contains("IS on origin/main", outcome, StringComparison.Ordinal);
        Assert.Contains("do not re-run", outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(60, row.GetProperty("DelaySeconds").GetDouble());
        Assert.Equal(1, row.GetProperty("Pushes").GetInt32());
        Assert.Empty(row.GetProperty("Comments").EnumerateArray());
        Assert.Equal(0, row.GetProperty("EarlyComments").GetInt32());
    }

    private JsonDocument Run(string scenario, int count)
    {
        var output = HeadlessShardsModule.RunModule(_scratch, $$$"""
            $global:ConfirmNow = [DateTime]::new(2020, 1, 1)
            $global:ConfirmDelays = 0; $global:ConfirmPushes = 0; $global:ConfirmComments = @(); $global:ConfirmEarly = 0
            $global:ConfirmQueries = @{}; $global:Confirmed = @{}
            $git = {
                param([string[]]$a)
                if ($a[0] -eq 'config') { return @{ Code = 0; Out = $(if ($a[-1] -eq 'user.name') { 'Bot' } else { 'bot@users.noreply.github.com' }) } }
                if ($a[0] -eq 'ls-remote') { return @{ Code = 0; Out = (('b' * 40) + ' refs/heads/main') } }
                if ($a[0] -eq 'push') { $global:ConfirmPushes++; return @{ Code = 0; Out = '' } }
                throw "unexpected git: $a"
            }
            $gh = {
                param([string[]]$a)
                $n = [int]$a[2]
                if ($a[1] -eq 'comment') {
                    if ($global:Confirmed.Count -ne {{{count}}}) { $global:ConfirmEarly++ }
                    $global:ConfirmComments += $n
                    return @{ Code = 0; Out = '' }
                }
                if ($a[-1] -eq 'state,headRefOid') { return @{ Code = 0; Out = (@{ state = 'OPEN'; headRefOid = "head$n" } | ConvertTo-Json -Compress) } }
                $global:ConfirmQueries[$n] = [int]$global:ConfirmQueries[$n] + 1
                $attempt = $global:ConfirmQueries[$n]
                if ('{{{scenario}}}' -eq 'transient' -and $attempt -eq 1) { return @{ Code = 1; Out = 'temporary API failure' } }
                if ('{{{scenario}}}' -eq 'malformed' -and $attempt -eq 1) { return @{ Code = 0; Out = 'invalid JSON' } }
                $after = if ('{{{scenario}}}' -eq 'staggered' -and $n -eq 2) { 3 } elseif ('{{{scenario}}}' -eq 'forever') { 0 } else { 2 }
                $ready = $attempt -gt $after -and -not ('{{{scenario}}}' -eq 'forever' -and $n -eq 2)
                $state = if ($ready -or '{{{scenario}}}' -eq 'missing-commit') { 'MERGED' } else { 'OPEN' }
                $oid = if ($ready) { 'a' * 40 } else { '' }
                if ($ready) { $global:Confirmed[$n] = $true }
                @{ Code = 0; Out = (@{ state = $state; mergeCommit = @{ oid = $oid } } | ConvertTo-Json -Compress) }
            }
            $timing = @{}
            # Old modules have no timing seam and fail immediately; new modules never sleep here.
            if ((Get-Command Invoke-MergeTrainMerges).Parameters.ContainsKey('ConfirmationClock')) {
                $timing = @{ ConfirmationClock = { $global:ConfirmNow }; ConfirmationDelay = { param([double]$Seconds) $global:ConfirmDelays += $Seconds; $global:ConfirmNow = $global:ConfirmNow.AddSeconds($Seconds) } }
            }
            $prs = @(1..{{{count}}} | ForEach-Object { [pscustomobject]@{ Number = $_; Head = "head$_" } })
            $outcome = try { $null = Invoke-MergeTrainMerges -Prs $prs -Repo o/r -ExpectedBase ('b' * 40) -Tip ('a' * 40) -Git $git -Gh $gh -Comment tested @timing 6>$null; 'landed' } catch { $_.Exception.Message }
            [pscustomobject]@{ Outcome = $outcome; Pushes = $global:ConfirmPushes; Comments = @($global:ConfirmComments); EarlyComments = $global:ConfirmEarly; DelaySeconds = $global:ConfirmDelays } | ConvertTo-Json -Depth 4 -Compress
            """, "tools", "agent", "MergeTrain.psm1");
        return JsonDocument.Parse(output);
    }
}
