using System.Text;
using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/ci/GateShards.psm1</c> — the default gate's per-class durations
///     and its split across concurrent testhosts for
///     <c>tools/agent/Invoke-Gate.ps1 -Gate</c> (and so <c>Merge-Train.ps1</c> and
///     CI's gate job) — executed through Windows PowerShell 5.1 against the
///     script itself.
///     <para>
///         The property a gate split can silently break is COVERAGE: a case that
///         runs in no shard is never graded, and a case that runs in two is
///         counted twice. The plan is an exact cover by construction (listed
///         shards plus a remainder that excludes every listed class); the first
///         case replays the runner's filter semantics over a universe that holds
///         classes the weights never saw, a class the weights still name but
///         that was deleted, a nested class and a headless class, and checks
///         that every gate class runs exactly once and no headless class runs.
///         <c>Invoke-Gate.ps1 -SelfTest</c> proves the same against the runner's
///         own listing.
///     </para>
/// </summary>
public sealed class GateShardsModuleTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-gateshards-" + Guid.NewGuid().ToString("N"));

    public GateShardsModuleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    private const string Weights = """
        $weights = [ordered]@{}
        foreach ($i in 1..30) { $weights["DotCharter.Tests.Ns.C$($i.ToString('00'))Tests"] = [double](($i * 37) % 90 + 1) }
        $weights['DotCharter.Tests.Ns.Outer+NestedTests'] = 70.0
        $weights['DotCharter.Tests.Ns.DeletedSinceMeasuredTests'] = 12.0
        # A headless class in the weights must never be LISTED: a listed shard must stay inside the gate.
        $weights['DotCharter.Tests.Perf.SomethingUiPerfSmokeTests'] = 500.0
        """;

    [Fact]
    public void ThePlan_IsAnExactCoverOfTheGate_WhateverTheWeightsKnow_AndIsDeterministic()
    {
        var json = RunModule(Weights + "\n" + """
            $plan = @(New-GateShardPlan $weights 40.0 4 @('*UiPerfSmokeTests'))
            $reversed = @{}
            foreach ($k in @($weights.Keys)[($weights.Count - 1)..0]) { $reversed[$k] = $weights[$k] }
            $again = @(New-GateShardPlan $reversed 40.0 4 @('*UiPerfSmokeTests'))
            $one = @(New-GateShardPlan $weights 40.0 1 @('*UiPerfSmokeTests'))
            $many = @(New-GateShardPlan $weights 0.0 60 @('*UiPerfSmokeTests'))
            # Room for five names: the rest must still run, in the remainder.
            $tight = @(New-GateShardPlan $weights 40.0 4 @('*UiPerfSmokeTests') 250)
            [pscustomobject]@{
                Args = [string[]]@($plan | ForEach-Object { $_.CommandArgs -join '|' })
                Tight = [string[]]@($tight | ForEach-Object { $_.CommandArgs -join '|' })
                Remainder = [string[]]@($plan | ForEach-Object { [string]$_.Remainder })
                Weights = [double[]]@($plan | ForEach-Object { $_.Weight })
                Again = [string[]]@($again | ForEach-Object { $_.CommandArgs -join '|' })
                One = [string[]]@($one | ForEach-Object { $_.CommandArgs -join '|' })
                Many = $many.Count
                ManyLastIsRemainder = [bool]$many[-1].Remainder
            } | ConvertTo-Json -Depth 5 -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var shards = root.GetProperty("Args").EnumerateArray().Select(a => a.GetString()!.Split('|')).ToList();
        var remainderFlags = root.GetProperty("Remainder").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(4, shards.Count);
        Assert.Equal(["False", "False", "False", "True"], remainderFlags);
        Assert.Equal(root.GetProperty("Args").GetRawText(), root.GetProperty("Again").GetRawText());

        // Every shard keeps the gate's own exclusion.
        foreach (var args in shards)
            Assert.Equal(["--filter-not-class", "*UiPerfSmokeTests"], args.Take(2));

        // The universe the runner would see: the weights' classes minus the
        // deleted one, plus classes added since, plus the headless suite.
        var universe = Enumerable.Range(1, 30).Select(i => $"DotCharter.Tests.Ns.C{i:00}Tests")
            .Concat(["DotCharter.Tests.Ns.Outer+NestedTests", "DotCharter.Tests.Ns.BrandNewTests", "DotCharter.Tests.Other.AlsoNewTests",
                "DotCharter.Tests.Perf.SomethingUiPerfSmokeTests", "DotCharter.Tests.Perf.AnotherUiPerfSmokeTests"])
            .ToList();
        var gate = universe.Where(c => Runs(["--filter-not-class", "*UiPerfSmokeTests"], c)).ToList();
        Assert.Equal(universe.Count - 2, gate.Count);

        var tight = root.GetProperty("Tight").EnumerateArray().Select(a => a.GetString()!.Split('|')).ToList();
        foreach (var plan in new[] { shards, tight })
        {
            foreach (var cls in universe)
            {
                var count = plan.Count(args => Runs(args, cls));
                Assert.True(count == (gate.Contains(cls) ? 1 : 0), $"{cls} runs in {count} shard(s)");
            }
        }
        // The cap holds: the remainder names every listed class, and it is the longest line.
        var listedNames = tight.Take(tight.Count - 1).SelectMany(a => a.Skip(2).Where((_, i) => i % 2 == 1)).ToList();
        Assert.InRange(listedNames.Count, 1, 5);
        Assert.True(tight[^1].Skip(2).Sum(a => a.Length + 1) <= 250, string.Join(" ", tight[^1]));

        // Longest-processing-time first: no shard exceeds the mean by more than the heaviest listable class.
        var w = root.GetProperty("Weights").EnumerateArray().Select(e => e.GetDouble()).ToList();
        Assert.True(w.Max() - w.Average() <= 90 + 1e-6, $"unbalanced plan: {string.Join(", ", w)}");

        // One shard is the gate's own filter; more shards than classes drop the empty listed ones.
        Assert.Equal(["--filter-not-class|*UiPerfSmokeTests"], root.GetProperty("One").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(32 + 1, root.GetProperty("Many").GetInt32()); // 32 listable classes + the remainder
        Assert.True(root.GetProperty("ManyLastIsRemainder").GetBoolean());
    }

    /// <summary>Non-vacuity of the replay above: a remainder that forgot one
    /// listed class runs it twice, and a listed shard without the gate's
    /// exclusion would run a headless class.</summary>
    [Fact]
    public void TheFilterReplay_SeesADoubleRunAndALeak()
    {
        string[] listed = ["--filter-not-class", "*UiPerfSmokeTests", "--filter-class", "DotCharter.Tests.A"];
        string[] sloppyRemainder = ["--filter-not-class", "*UiPerfSmokeTests"];
        Assert.Equal(2, new[] { listed, sloppyRemainder }.Count(args => Runs(args, "DotCharter.Tests.A")));
        Assert.True(Runs(["--filter-class", "*UiPerfSmokeTests"], "DotCharter.Tests.Perf.XUiPerfSmokeTests"));
        Assert.False(Runs(listed, "DotCharter.Tests.Perf.XUiPerfSmokeTests"));
    }

    [Fact]
    public void ListingParity_IsCaseForCase_AndNamesWhatDiffers()
    {
        var json = RunModule("""
            $whole = [string[]]@('A.T.One', 'A.T.Two(x: 1)', 'A.T.Two(x: 1)', 'B.T.Three')
            $equal = Compare-TestListings $whole @([string[]]@('B.T.Three', 'A.T.Two(x: 1)'), [string[]]@('A.T.One', 'A.T.Two(x: 1)'))
            $bad = Compare-TestListings $whole @(, [string[]]@('A.T.One', 'A.T.One', 'A.T.Two(x: 1)', 'C.T.Ghost'))
            [pscustomobject]@{
                Equal = $equal.Equal; EqualCount = $equal.PartsCount
                Bad = $bad.Equal
                Missing = ($bad.Missing -join '|'); Extra = ($bad.Extra -join '|'); Dup = ($bad.Duplicated -join '|')
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("Equal").GetBoolean());
        Assert.Equal(4, root.GetProperty("EqualCount").GetInt32());
        Assert.False(root.GetProperty("Bad").GetBoolean());
        Assert.Equal("A.T.Two(x: 1)|B.T.Three", root.GetProperty("Missing").GetString());
        Assert.Equal("C.T.Ghost", root.GetProperty("Extra").GetString());
        Assert.Equal("A.T.One", root.GetProperty("Dup").GetString());
    }

    [Fact]
    public void Durations_AreReadPerClassFromTheTestDefinitions_AndSummarisedWithShares()
    {
        var trx = Path.Combine(_scratch, "gate.trx");
        File.WriteAllText(trx, Trx(
            ("DotCharter.Tests.Audio.SlowTests", "SlowTests.Case(x: 1.5)", "00:01:00.0000000"),
            ("DotCharter.Tests.Audio.SlowTests", "SlowTests.Other", "00:00:30.0000000"),
            // A nested class, and a case with a custom DisplayName: the class comes from the definition.
            ("DotCharter.Tests.Tone.Matcher+Floor", "A friendly display name", "00:00:45.5000000"),
            ("DotCharter.Tests.Core.FastTests", "FastTests.One", "00:00:00.2500000"),
            ("DotCharter.Tests.Core.FastTests", "FastTests.Two", "00:00:00.2500000")));
        var json = RunModule($$"""
            $d = Read-TrxClassDurations @('{{trx}}', '{{trx}}.missing')
            $s = Get-ClassDurationSummary $d 2
            $lines = Format-ClassDurationSummary $s
            Write-ClassDurationJson $s $d '{{trx}}.json'
            $back = Get-Content -LiteralPath '{{trx}}.json' -Raw | ConvertFrom-Json
            [pscustomobject]@{
                Classes = ($d | ForEach-Object { "$($_.Class)=$($_.Seconds)/$($_.Cases)" }) -join '|'
                Total = $s.TotalSeconds; TopShare = $s.TopShare; TopCount = $s.Top.Count
                Shares = ($s.Top | ForEach-Object { $_.Share.ToString([Globalization.CultureInfo]::InvariantCulture) }) -join '|'
                Lines = $lines -join "`n"
                JsonAll = $back.all.Count; JsonTop = $back.top[0].class
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("DotCharter.Tests.Audio.SlowTests=90/2|DotCharter.Tests.Tone.Matcher+Floor=45.5/1|DotCharter.Tests.Core.FastTests=0.5/2",
            root.GetProperty("Classes").GetString());
        Assert.Equal(136.0, root.GetProperty("Total").GetDouble());
        Assert.Equal(2, root.GetProperty("TopCount").GetInt32());
        Assert.Equal("66.2|33.5", root.GetProperty("Shares").GetString());
        Assert.Equal(99.6, root.GetProperty("TopShare").GetDouble());
        var lines = root.GetProperty("Lines").GetString()!.Split('\n');
        Assert.Equal("  slowest classes: top 2 of 3 = 99.6 % of 136 s summed case time (5 cases)", lines[0]);
        Assert.Contains("Audio.SlowTests (2)", lines[1]);
        Assert.Contains("90 s", lines[1]);
        Assert.Equal(3, root.GetProperty("JsonAll").GetInt32());
        Assert.Equal("DotCharter.Tests.Audio.SlowTests", root.GetProperty("JsonTop").GetString());
    }

    [Fact]
    public void TheWeightsFile_ListsClassesOverTheFloor_AndSumsTheRest()
    {
        var path = Path.Combine(_scratch, "weights.json");
        var json = RunModule($$"""
            $d = @(
                [pscustomobject]@{ Class = 'DotCharter.Tests.B'; Seconds = 12.34; Cases = 3 },
                [pscustomobject]@{ Class = 'DotCharter.Tests.A'; Seconds = 1.0; Cases = 1 },
                [pscustomobject]@{ Class = 'DotCharter.Tests.Small1'; Seconds = 0.4; Cases = 9 },
                [pscustomobject]@{ Class = 'DotCharter.Tests.Small2'; Seconds = 0.35; Cases = 2 })
            Write-GateShardWeights $d '{{path}}' 1.0
            $w = Read-GateShardWeights '{{path}}'
            $none = Read-GateShardWeights '{{path}}.missing'
            [pscustomobject]@{
                Classes = (@($w.Classes.Keys | Sort-Object) | ForEach-Object { "$_=$($w.Classes[$_])" }) -join '|'
                Other = $w.OtherSeconds; None = $none.Classes.Count
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("DotCharter.Tests.A=1|DotCharter.Tests.B=12.3", root.GetProperty("Classes").GetString());
        Assert.Equal(0.8, root.GetProperty("Other").GetDouble(), 3);
        Assert.Equal(0, root.GetProperty("None").GetInt32());
        Assert.DoesNotContain("\r", File.ReadAllText(path));
    }

    [Fact]
    public void TheSuitesRunConcurrently_OnlyWithEnoughFreeMemory()
    {
        var json = RunModule("""
            [pscustomobject]@{
                Plenty = (Get-GateConcurrencyDecision (20GB) 12).Concurrent
                Short = (Get-GateConcurrencyDecision (8GB) 12).Concurrent
                Reason = (Get-GateConcurrencyDecision (8GB) 12).Reason
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("Plenty").GetBoolean());
        Assert.False(root.GetProperty("Short").GetBoolean());
        Assert.Equal("sequential: 8 GB free, 12 GB needed", root.GetProperty("Reason").GetString());
    }

    /// <summary>The callers really go through the module.</summary>
    [Fact]
    public void TheGateAndItsCallers_ShardThroughTheModule()
    {
        static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));
        var gate = Read("tools", "agent", "Invoke-Gate.ps1");
        var train = Read("tools", "agent", "Merge-Train.ps1");

        Assert.Contains("GateShards.psm1", gate);
        Assert.Contains("New-GateShardPlan $weights.Classes $weights.OtherSeconds $Shards (Get-GateExclusions)", gate);
        Assert.Contains("Start-TestShards ", gate);
        Assert.Contains("Wait-TestShards ", gate);
        Assert.Contains("Merge-HeadlessTrx ", gate);
        Assert.Contains("if ($Gate) { $exit = Invoke-GateRun ", gate);
        // The gate's exclusion is the entry point's, character for character.
        Assert.Contains($"$GateExclusion = '{DefaultRunFilter.ExcludedClasses}'", gate);
        // The train runs both suites in ONE Invoke-Gate call, so they can overlap.
        Assert.Matches(@"& powershell -NoProfile -File \$gate @cpuSwitch -Gate -Headless [^\n]*-Shards \$Shards", train);
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "gate-shard-weights.json")));
    }

    // xUnit v3's simple filters: a case runs when it matches no --filter-not-class
    // and, if any --filter-class is given, at least one of them. '*' only at the ends.
    internal static bool Runs(string[] args, string cls)
    {
        var include = new List<string>();
        var exclude = new List<string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--filter-class") include.Add(args[i + 1]);
            else if (args[i] == "--filter-not-class") exclude.Add(args[i + 1]);
            else throw new InvalidOperationException($"unexpected filter argument {args[i]}");
        }
        if (exclude.Any(p => Matches(p, cls))) return false;
        return include.Count == 0 || include.Any(p => Matches(p, cls));
    }

    private static bool Matches(string pattern, string value) =>
        (pattern.StartsWith('*'), pattern.EndsWith('*')) switch
        {
            (true, true) => value.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase),
            (true, false) => value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase),
            (false, true) => value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)
        };

    private static string Trx(params (string Class, string Name, string Duration)[] cases)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>""");
        for (var i = 0; i < cases.Length; i++)
            sb.Append($"""<UnitTestResult testId="id{i}" testName="{cases[i].Name}" duration="{cases[i].Duration}" outcome="Passed" />""");
        sb.Append("</Results><TestDefinitions>");
        for (var i = 0; i < cases.Length; i++)
            sb.Append($"""<UnitTest id="id{i}" name="{cases[i].Name}"><TestMethod className="{cases[i].Class}" name="M{i}" /></UnitTest>""");
        sb.Append("""</TestDefinitions><ResultSummary outcome="Completed"><Counters total="5" executed="5" passed="5" failed="0" /></ResultSummary></TestRun>""");
        return sb.ToString();
    }

    private string RunModule(string body) => HeadlessShardsModule.RunModule(_scratch, body, "tools", "ci", "GateShards.psm1");
}
