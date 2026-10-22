using System.Reflection;
using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The release lane (<c>tools/ci/release-lane.json</c>): the heaviest corpus /
///     integration / determinism classes leave the default gate and run in
///     <c>Invoke-Gate.ps1 -ReleaseLane</c>, which the merge train, the release
///     preflight and CI always run. The property to keep is that a class never
///     VANISHES: moving one out of the gate must not leave it in no lane.
///     <para>
///         The lanes are replayed here the way the runner reads them: every test
///         class of this assembly (found by reflection, not listed) against the
///         exact filter arguments each lane starts a process with - the default
///         gate's and the release lane's SHARD plans from
///         <c>tools/ci/GateShards.psm1</c> over the real weights file and list, at
///         1, 2 and 4 shards, plus the headless suite and the golden screenshots.
///         A class that runs in no process, or in two, fails by name. A plain
///         <c>dotnet test</c> must equal the default gate. The scripts that must
///         ALWAYS run the lane (the train, the release preflight, CI) are pinned
///         by their text, because a lane nobody calls is the same loss one level
///         up.
///     </para>
/// </summary>
public sealed class ReleaseLaneLintTests : IDisposable
{
    private const string GoldenClass = "DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests";
    private static readonly string[] DotnetTest = ["--server", "dotnettestcli", "--dotnet-test-pipe", "pipe-1"];

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-releaselane-" + Guid.NewGuid().ToString("N"));

    public ReleaseLaneLintTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    /// <summary>One lane and the filter arguments of every process it starts.</summary>
    private sealed record Lane(string Name, IReadOnlyList<string[]> Processes);

    [Fact]
    public void EveryTestClass_RunsInExactlyOneLane_AtEveryShardCount()
    {
        var universe = TestClasses();
        Assert.True(universe.Count > 1000, $"reflection found only {universe.Count} test classes");
        var plans = RealPlans();
        foreach (var shards in new[] { 1, 2, 4 })
        {
            var lanes = Lanes(plans, shards);
            var problems = Problems(universe, lanes);
            Assert.True(problems.Count == 0,
                $"At {shards} shard(s), {problems.Count} test class(es) do not run in exactly one lane "
                + "(default gate, release lane, headless, goldens):\n  " + string.Join("\n  ", problems.Take(20)));
        }
    }

    [Fact]
    public void TheReleaseLane_RunsExactlyTheListedClasses_AndTheDefaultGateRunsNoneOfThem()
    {
        var listed = ReleaseLane.Read().Select(e => e.Class).ToHashSet(StringComparer.Ordinal);
        var universe = TestClasses();
        var lanes = Lanes(RealPlans(), 4);
        var release = lanes.Single(l => l.Name == "release lane");
        var gate = lanes.Single(l => l.Name == "default gate");

        Assert.Equal(listed.OrderBy(c => c, StringComparer.Ordinal), universe.Where(c => Runs(release, c)).OrderBy(c => c, StringComparer.Ordinal));
        Assert.DoesNotContain(listed, c => Runs(gate, c));
        Assert.True(universe.Count(c => Runs(gate, c)) > 1000, "the default gate runs almost nothing");
    }

    [Fact]
    public void APlainDotnetTest_RunsExactlyTheDefaultGate()
    {
        var plain = new Lane("plain dotnet test", [DefaultRunFilter.Apply(DotnetTest).Skip(DotnetTest.Length).ToArray()]);
        var gate = Lanes(RealPlans(), 4).Single(l => l.Name == "default gate");
        var differ = TestClasses().Where(c => Runs(plain, c) != Runs(gate, c)).ToList();
        Assert.True(differ.Count == 0, "a plain dotnet test and the default gate disagree on:\n  " + string.Join("\n  ", differ.Take(20)));
    }

    [Fact]
    public void TheList_NamesRealClasses_EachWithItsReasonAndSeconds()
    {
        var path = ReleaseLane.ListPath();
        Assert.DoesNotContain("\r", File.ReadAllText(path));
        var entries = ReleaseLane.Read();
        var universe = TestClasses().ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(e => e.Class).Distinct(StringComparer.Ordinal).Count());
        foreach (var e in entries)
        {
            Assert.True(universe.Contains(e.Class),
                $"{e.Class} is on the release lane list but is no test class (renamed or deleted?): fix tools/ci/release-lane.json.");
            Assert.False(e.Class.EndsWith("UiPerfSmokeTests", StringComparison.Ordinal),
                $"{e.Class} is a headless class: the headless suite is its own lane and cannot be on the release lane list.");
            Assert.False(e.Class.Contains('*', StringComparison.Ordinal), $"{e.Class}: the list holds exact class names, no wildcards.");
            Assert.True(e.Why.Length >= 40, $"{e.Class} needs a reason (what it tests and why it is not a per-change check).");
            Assert.True(e.Seconds > 0, $"{e.Class} needs its measured seconds (they balance the lane's shards).");
        }
    }

    [Fact]
    public void TheCoverageCheck_SeesAClassThatRunsInNoLane_AndOneThatRunsInTwo()
    {
        var gate = new Lane("default gate", [["--filter-not-class", "*UiPerfSmokeTests", "--filter-not-class", "N.Moved"]]);
        var release = new Lane("release lane", [["--filter-class", "N.Moved"], ["--filter-class", "N.Twice"]]);
        var headless = new Lane("headless", [["--filter-class", "*UiPerfSmokeTests"]]);

        // Healthy: each class in one lane.
        Assert.Empty(Problems(["N.Plain", "N.Moved", "N.ViewUiPerfSmokeTests"], [gate, release, headless]));

        // A class dropped from the lane it was moved to runs nowhere.
        var dropped = new Lane("release lane", [["--filter-class", "N.Other"]]);
        var lost = Assert.Single(Problems(["N.Plain", "N.Moved"], [gate, dropped, headless]));
        Assert.Contains("N.Moved", lost, StringComparison.Ordinal);
        Assert.Contains("no lane", lost, StringComparison.Ordinal);

        // A class in the gate AND the lane runs twice.
        var twice = Assert.Single(Problems(["N.Twice"], [gate, release, headless]));
        Assert.Contains("N.Twice", twice, StringComparison.Ordinal);
        Assert.Contains("2 times", twice, StringComparison.Ordinal);

        // Two shards of one lane listing the same class is also twice.
        var doubled = new Lane("release lane", [["--filter-class", "N.Moved"], ["--filter-class", "N.Moved"]]);
        Assert.Single(Problems(["N.Moved"], [gate, doubled, headless]));
    }

    [Fact]
    public void TheTrain_ThePreflightAndCi_AlwaysRunTheReleaseLane()
    {
        static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));
        var gate = Read("tools", "agent", "Invoke-Gate.ps1");
        var train = Read("tools", "agent", "Merge-Train.ps1");
        var release = Read("tools", "release", "New-Release.ps1");
        var workflow = Read(".github", "workflows", "build.yml");

        // The gate leaves the list out and the lane runs exactly it: both read the ONE list.
        Assert.Contains("@($GateExclusion) + @((Read-ReleaseLane $ReleaseLanePath).Classes.Keys)", gate, StringComparison.Ordinal);
        Assert.Contains("New-ReleaseLaneShardPlan $weights.Classes $Shards", gate, StringComparison.Ordinal);
        Assert.Contains("if ($ReleaseLane) { $exit = Invoke-ReleaseLaneRun 'release'; if ($exit -ne 0) { exit $exit } }", gate, StringComparison.Ordinal);

        // The train: after the green gate, on every train that is not docs-only, before the merge, and on record.
        Assert.Contains("if (-not $docsOnly -and $gateOk -and $headlessOk) {", train, StringComparison.Ordinal);
        var lane = train.IndexOf("-File $gate @cpuSwitch -ReleaseLane -NoBuild -Shards $Shards", StringComparison.Ordinal);
        var record = train.IndexOf("$recordPath = Save-GateRecord @recordArgs", StringComparison.Ordinal);
        var merge = train.IndexOf("gh pr merge", StringComparison.Ordinal);
        Assert.True(lane > 0 && record > lane && merge > record, "the train must run the release lane before it records the tree and merges.");
        Assert.Contains("elseif (-not $releaseOk) { 'release lane RED' }", train, StringComparison.Ordinal);
        Assert.Contains("ReleaseLane = $ranRelease", train, StringComparison.Ordinal);

        // The release preflight: not skippable, reuses only a green record of the same tree, runs before the publishes.
        Assert.DoesNotContain("SkipReleaseLane", release, StringComparison.Ordinal);
        var headlessCall = release.IndexOf("Invoke-HeadlessUiGate -ResultsDir", StringComparison.Ordinal);
        var laneCall = release.IndexOf("Invoke-ReleaseLaneGate -ResultsDir", StringComparison.Ordinal);
        var cleanBuild = release.IndexOf("# --- Clean Release build", StringComparison.Ordinal);
        Assert.True(headlessCall > 0 && laneCall > headlessCall && cleanBuild > laneCall, "the release lane runs in preflight, before the clean release build.");
        Assert.Contains("-Kind releaseLane", release, StringComparison.Ordinal);
        Assert.Contains("-ReleaseLane -Source 'New-Release.ps1 release lane'", release, StringComparison.Ordinal);
        Assert.Contains("'-ReleaseLane', '-NoBuild'", release, StringComparison.Ordinal);

        // CI: the one job that runs the gate also runs the lane.
        Assert.Matches(@"run:[^\n]*Invoke-Gate\.ps1 -Gate -NoBuild -ReleaseLane[^\n]*-Shards \d+", workflow);
    }

    // ---- the replay ---------------------------------------------------------------

    /// <summary>Every concrete class of this assembly with at least one xunit test method.</summary>
    private static IReadOnlyList<string> TestClasses() =>
    [
        .. typeof(ReleaseLaneLintTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any()))
            .Select(t => t.FullName!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
    ];

    /// <summary>The gate's and the lane's real shard plans (the module, over the real files), per shard count.</summary>
    private JsonElement RealPlans()
    {
        var root = TestPaths.RepoRoot();
        string Lit(params string[] parts) => Path.Combine([root, .. parts]).Replace("'", "''");
        var json = HeadlessShardsModule.RunModule(_scratch, $$"""
            $lane = Read-ReleaseLane '{{Lit("tools", "ci", "release-lane.json")}}'
            $w = Read-GateShardWeights '{{Lit("tools", "ci", "gate-shard-weights.json")}}'
            $exclusions = @('*UiPerfSmokeTests') + @($lane.Classes.Keys)
            $out = [ordered]@{}
            foreach ($n in 1, 2, 4) {
                $g = @(New-GateShardPlan $w.Classes $w.OtherSeconds $n $exclusions)
                $r = @(New-ReleaseLaneShardPlan $lane.Classes $n)
                $out["gate$n"] = [string[]]@($g | ForEach-Object { $_.CommandArgs -join '|' })
                $out["release$n"] = [string[]]@($r | ForEach-Object { $_.CommandArgs -join '|' })
            }
            $out | ConvertTo-Json -Compress
            """, "tools", "ci", "GateShards.psm1");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Lane[] Lanes(JsonElement plans, int shards) =>
    [
        new("default gate", Processes(plans.GetProperty($"gate{shards}"))),
        new("release lane", Processes(plans.GetProperty($"release{shards}"))),
        new("headless", [["--filter-class", "*UiPerfSmokeTests", "--filter-not-class", GoldenClass]]),
        new("goldens", [["--filter-class", GoldenClass]])
    ];

    // Windows PowerShell 5.1 writes a one-element array as a bare string.
    private static string[][] Processes(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? [element.GetString()!.Split('|')]
            : [.. element.EnumerateArray().Select(e => e.GetString()!.Split('|'))];

    /// <summary>The classes that run in no process of any lane, or in more than one, each with the lanes that run it.</summary>
    private static List<string> Problems(IReadOnlyList<string> universe, IReadOnlyList<Lane> lanes)
    {
        var problems = new List<string>();
        foreach (var cls in universe)
        {
            var hits = lanes.SelectMany(l => l.Processes.Where(p => Runs(p, cls)).Select(_ => l.Name)).ToList();
            if (hits.Count == 0) problems.Add($"{cls}: runs in no lane");
            else if (hits.Count > 1) problems.Add($"{cls}: runs {hits.Count} times ({string.Join(", ", hits)})");
        }
        return problems;
    }

    private static bool Runs(Lane lane, string cls) => lane.Processes.Any(p => Runs(p, cls));

    private static bool Runs(string[] args, string cls) => GateShardsModuleTests.Runs(args, cls);
}
