using System.Reflection;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>tools/ci/HeadlessShards.psm1</c> — the module that splits the
///     headless-UI suite across concurrent testhosts for
///     <c>tools/agent/Invoke-Gate.ps1 -Headless</c>, CI's <c>headless-ui</c> job
///     and the release preflight — executed in the DEFAULT gate.
///     <para>
///         The three properties a shard split can silently break are pinned
///         here against the script itself (re-implementing it in C# would only
///         test the copy): the plan covers every discovered class EXACTLY once
///         and never splits a class; it is deterministic whatever order
///         discovery hands the classes over in, and a class the weights file
///         has never seen is still assigned; and a shard that wrote no TRX
///         leaves its classes ABSENT from the merge, so the callers'
///         discovered-vs-reported check names them instead of reading green.
///     </para>
///     <para>
///         Run through Windows PowerShell 5.1, the oldest host
///         <c>Invoke-Gate.ps1</c> supports. Missing is a skip, never a failure.
///     </para>
/// </summary>
public sealed class HeadlessShardPlanTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-shards-" + Guid.NewGuid().ToString("N"));

    public HeadlessShardPlanTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    private const string Classes = """
        $counts = [ordered]@{}
        foreach ($i in 1..23) { $counts["DotCharter.Tests.Perf.C$($i.ToString('00'))UiPerfSmokeTests"] = ($i % 5) + 1 }
        # Measured weights for all but two classes: C22 and C23 are NEW.
        $weights = @{}
        foreach ($i in 1..21) { $weights["DotCharter.Tests.Perf.C$($i.ToString('00'))UiPerfSmokeTests"] = [double](($i * 37) % 90 + 1) }
        """;

    [Fact]
    public void ThePlan_CoversEveryClassExactlyOnce_WholeClasses_Balanced_AndIsOrderIndependent()
    {
        var json = RunModule(Classes + """
            $plan = @(New-HeadlessShardPlan $counts $weights 4)
            # Same classes, REVERSED insertion order: the plan must not depend on it.
            $reversed = @{}
            foreach ($k in @($counts.Keys)[($counts.Count - 1)..0]) { $reversed[$k] = $counts[$k] }
            $again = @(New-HeadlessShardPlan $reversed $weights 4)
            $one = @(New-HeadlessShardPlan $counts $weights 1)
            $many = @(New-HeadlessShardPlan $counts $weights 40)
            # Strings, not nested arrays: Windows PowerShell's ConvertTo-Json mangles those.
            [pscustomobject]@{
                Input = (@($counts.Keys) -join '|')
                Shards = [string[]]@($plan | ForEach-Object { $_.Classes -join '|' })
                Weights = [double[]]@($plan | ForEach-Object { $_.Weight })
                Again = [string[]]@($again | ForEach-Object { $_.Classes -join '|' })
                OneShard = $one[0].Classes.Count
                ManyShards = $many.Count
                Estimated = (@($plan | ForEach-Object { $_.Estimated } | Where-Object { $_ }) -join '|')
                MaxWeight = [double]($weights.Values | Measure-Object -Maximum).Maximum
            } | ConvertTo-Json -Depth 5 -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var input = root.GetProperty("Input").GetString()!.Split('|').ToList();
        var shards = root.GetProperty("Shards").EnumerateArray()
            .Select(s => s.GetString()!.Split('|').ToList()).ToList();

        Assert.Equal(4, shards.Count);
        var assigned = shards.SelectMany(s => s).ToList();
        Assert.Equal(input.Count, assigned.Count);                       // no class twice
        Assert.Equal(input.OrderBy(c => c, StringComparer.Ordinal), assigned.OrderBy(c => c, StringComparer.Ordinal)); // none dropped
        Assert.Equal(root.GetProperty("Shards").GetRawText(), root.GetProperty("Again").GetRawText());

        // A class the weights file has never seen is assigned and reported as estimated.
        var estimated = root.GetProperty("Estimated").GetString()!.Split('|').ToList();
        Assert.Equal(["DotCharter.Tests.Perf.C22UiPerfSmokeTests", "DotCharter.Tests.Perf.C23UiPerfSmokeTests"],
            estimated.OrderBy(c => c, StringComparer.Ordinal));

        // Longest-processing-time first: no shard exceeds the mean by more than the heaviest class.
        var w = root.GetProperty("Weights").EnumerateArray().Select(e => e.GetDouble()).ToList();
        Assert.True(w.Max() - w.Average() <= root.GetProperty("MaxWeight").GetDouble() + 1e-6,
            $"unbalanced plan: {string.Join(", ", w)}");

        // One shard is the whole suite; more shards than classes drops the empty ones.
        Assert.Equal(input.Count, root.GetProperty("OneShard").GetInt32());
        Assert.Equal(input.Count, root.GetProperty("ManyShards").GetInt32());
    }

    [Fact]
    public void AShardThatWroteNoTrx_LeavesItsClassesAbsentFromTheMerge_AndTheCountersAreSummed()
    {
        var a = WriteTrx("a.trx", "DotCharter.Tests.Perf.AlphaUiPerfSmokeTests", passed: 2, failed: 1);
        var b = WriteTrx("b.trx", "DotCharter.Tests.Perf.GammaUiPerfSmokeTests", passed: 3, failed: 0);
        var merged = Path.Combine(_scratch, "merged.trx");
        var missing = Path.Combine(_scratch, "crashed-shard.trx"); // never written: the shard died

        var json = RunModule($$"""
            $out = Merge-HeadlessTrx @('{{a}}', $null, '{{missing}}', '{{b}}') '{{merged}}'
            [xml]$doc = Get-Content -LiteralPath $out -Raw
            $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
            $c = (Select-Xml -Xml $doc -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
            [pscustomobject]@{
                Classes = (@((Select-Xml -Xml $doc -XPath '//t:UnitTestResult' -Namespace $ns) |
                    ForEach-Object { Get-HeadlessClassName $_.Node.testName } | Sort-Object -Unique) -join '|')
                Total = [int]$c.total; Passed = [int]$c.passed; Failed = [int]$c.failed
                Outcome = (Select-Xml -Xml $doc -XPath '//t:ResultSummary' -Namespace $ns).Node.outcome
                NothingAtAll = $null -eq (Merge-HeadlessTrx @('{{missing}}') '{{merged}}.none')
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(
            ["DotCharter.Tests.Perf.AlphaUiPerfSmokeTests", "DotCharter.Tests.Perf.GammaUiPerfSmokeTests"],
            root.GetProperty("Classes").GetString()!.Split('|'));
        Assert.Equal(6, root.GetProperty("Total").GetInt32());
        Assert.Equal(5, root.GetProperty("Passed").GetInt32());
        Assert.Equal(1, root.GetProperty("Failed").GetInt32());
        Assert.Equal("Failed", root.GetProperty("Outcome").GetString());
        Assert.True(root.GetProperty("NothingAtAll").GetBoolean(), "a merge of no results must report none, not an empty green file");
    }

    /// <summary>The three callers really go through the module — a caller that
    /// quietly went back to one wildcard process would still pass everything above.</summary>
    [Fact]
    public void EveryHeadlessRunner_ShardsThroughTheModule()
    {
        static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));
        var gate = Read("tools", "agent", "Invoke-Gate.ps1");
        var ci = Read("tools", "ci", "Invoke-HeadlessUiCi.ps1");
        var release = Read("tools", "release", "New-Release.ps1");

        foreach (var (name, text) in new[] { ("Invoke-Gate.ps1", gate), ("Invoke-HeadlessUiCi.ps1", ci), ("New-Release.ps1", release) })
        {
            Assert.True(text.Contains("HeadlessShards.psm1", StringComparison.Ordinal), $"{name} no longer imports HeadlessShards.psm1");
            Assert.True(text.Contains("Invoke-HeadlessShards ", StringComparison.Ordinal), $"{name} no longer runs the shards");
            Assert.True(text.Contains("Merge-HeadlessTrx ", StringComparison.Ordinal), $"{name} no longer merges the shards' results");
        }
        Assert.Contains("if ($Headless) { $exit = Invoke-HeadlessShardedRun ", gate);
        Assert.Contains("-Label 'run1' -Culture 'en-US' -Shards $HeadlessShards", release);
        Assert.Contains("$run = Invoke-HeadlessUiShardedRun -RunFilter $Filter -Label 'run1'", ci);
    }

    /// <summary>
    ///     Every headless case has a name discovery's listing parser
    ///     (<c>Read-HeadlessTestListing</c>) reads. The shards run only the classes
    ///     that parser returns, so a case it rejects is never RUN, and the
    ///     discovered-vs-reported check, which reads the same list, never misses it.
    ///     The parser takes <c>DotCharter.Tests.Ns.Class.Method(args)</c>: a NESTED
    ///     class (<c>Outer+Inner</c>), a generic method or class, or a custom
    ///     <c>DisplayName</c> lists in another shape.
    /// </summary>
    [Fact]
    public void EveryHeadlessCase_ListsInTheShapeTheShardPlannerReads()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(HeadlessShardPlanTests).Assembly.GetTypes())
        {
            var cases = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes<FactAttribute>(true).Any())
                .ToList();
            if (cases.Count == 0 || !type.Name.EndsWith("UiPerfSmokeTests", StringComparison.Ordinal)) continue;

            if (type.IsNested) offenders.Add($"{type.FullName}: nested class");
            if (type.IsGenericType) offenders.Add($"{type.FullName}: generic class");
            if (type.Namespace?.StartsWith("DotCharter.Tests", StringComparison.Ordinal) != true)
                offenders.Add($"{type.FullName}: outside DotCharter.Tests.*");
            foreach (var method in cases)
            {
                if (method.IsGenericMethodDefinition) offenders.Add($"{type.Name}.{method.Name}: generic method");
                if (method.GetCustomAttributes<FactAttribute>(true).Any(f => !string.IsNullOrEmpty(f.DisplayName)))
                    offenders.Add($"{type.Name}.{method.Name}: custom DisplayName");
            }
        }

        Assert.True(offenders.Count == 0,
            "These headless cases would be skipped by the sharded run without anyone noticing:\n  " +
            string.Join("\n  ", offenders));
    }

    private string WriteTrx(string name, string cls, int passed, int failed)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">""");
        sb.Append("""<Times creation="2026-10-04T10:00:00" start="2026-10-04T10:00:00" finish="2026-10-04T10:01:00" />""");
        sb.Append("<Results>");
        for (var i = 0; i < passed + failed; i++)
            sb.Append($"""<UnitTestResult testName="{cls}.Case{i}(x: 1.5)" outcome="{(i < failed ? "Failed" : "Passed")}" />""");
        sb.Append("</Results>");
        sb.Append($"""<ResultSummary outcome="{(failed > 0 ? "Failed" : "Completed")}"><Counters total="{passed + failed}" executed="{passed + failed}" passed="{passed}" failed="{failed}" /></ResultSummary>""");
        sb.Append("</TestRun>");
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private string RunModule(string body) => HeadlessShardsModule.Run(_scratch, body);
}
