using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The headless-UI suite (<c>*UiPerfSmokeTests</c>) cannot share a testhost
///     with the default gate — <c>Avalonia.Headless</c>'s dispatcher is
///     process-global and 314 of its 330 cases fail when it does. For a long
///     time that meant CI ran only the gate half. It now runs as its OWN job,
///     and this lint keeps it that way: the two `dotnet test` invocations must
///     live in DIFFERENT jobs, the headless one must go through the shared
///     grading script (so CI and the release script cannot drift on what counts
///     as a failure), and every caller that tells a harness fault from a product
///     failure — CI, the release preflight, <c>Invoke-Gate.ps1 -Headless</c> and,
///     through it, <c>Merge-Train.ps1</c> — must read the ONE needle list in
///     <c>tools/ci/HeadlessShards.psm1</c> rather than keep a copy.
/// </summary>
public sealed class CiWorkflowLintTests
{
    private static string Workflow() => File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), ".github", "workflows", "build.yml"));

    /// <summary>Top-level job ids under <c>jobs:</c>, with the text of each.</summary>
    private static Dictionary<string, string> Jobs(string yaml)
    {
        var jobsAt = yaml.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobsAt >= 0, "no jobs: block");
        var body = yaml[(jobsAt + "\njobs:".Length)..];
        var heads = Regex.Matches(body, @"^  ([a-z][\w-]*):\s*$", RegexOptions.Multiline);
        Assert.True(heads.Count >= 2, "fewer than two jobs");
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : body.Length;
            jobs[heads[i].Groups[1].Value] = body[start..end];
        }
        return jobs;
    }

    /// <summary>
    ///     CI runs only when started by hand (the "Run workflow" button), and a
    ///     newer run of the same branch cancels the older one. It used to run on
    ///     every push AND every PR, then on every PR, in Windows minutes that bill
    ///     at 2x against this private repo's 2,000 free a month, while merges were
    ///     decided on the local gate anyway (#63).
    /// </summary>
    [Fact]
    public void Ci_runs_only_when_started_by_hand_and_cancels_superseded_runs()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot(), ".github", "workflows"), "*.y*ml"))
        {
            var yaml = File.ReadAllText(file).Replace("\r\n", "\n");
            var on = Regex.Match(yaml, @"^on:[ \t]*\n((?:[ \t]+.*\n|[ \t]*\n)+)", RegexOptions.Multiline);
            Assert.True(on.Success, $"{Path.GetFileName(file)}: no multi-line on: block");
            var triggers = Regex.Matches(on.Groups[1].Value, @"^  ([a-z_]+):", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToArray();
            Assert.NotEmpty(triggers);
            Assert.True(triggers is ["workflow_dispatch"],
                $"{Path.GetFileName(file)} triggers on [{string.Join(", ", triggers)}]; only workflow_dispatch (the manual button) may start a run.");
        }

        Assert.Matches(@"(?m)^concurrency:[ \t]*\n(?:  .*\n)*  cancel-in-progress: true", Workflow().Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_headless_ui_suite_runs_in_its_own_job_never_in_the_gates_testhost()
    {
        var jobs = Jobs(Workflow());

        var gateJobs = jobs.Where(j => j.Value.Contains("--filter-not-class \"*UiPerfSmokeTests\"", StringComparison.Ordinal)).Select(j => j.Key).ToList();
        // A `run:` line, not a comment — the gate job's comment names the script too.
        var headlessJobs = jobs.Where(j => Regex.IsMatch(j.Value, @"run:[^\n]*Invoke-HeadlessUiCi\.ps1")).Select(j => j.Key).ToList();

        Assert.Single(gateJobs);
        Assert.Single(headlessJobs);
        Assert.NotEqual(gateJobs[0], headlessJobs[0]);

        var headless = jobs[headlessJobs[0]];
        Assert.Contains("runs-on: windows-latest", headless);
        Assert.Matches(@"timeout-minutes:\s*\d+", headless);
        // The gate half must not ALSO run inside the headless job's process, and
        // no job may run the suite unfiltered.
        Assert.DoesNotContain("--filter-not-class", headless);
        foreach (var job in jobs.Values)
            Assert.DoesNotMatch(@"dotnet test [^\n]*DotCharter\.Tests\.csproj(?![^\n]*--filter)(?![^\n]*--list-tests)", job);

        // The shard count is a CHOICE made against the runner's cores and memory
        // (the workflow comment says which), not an inherited script default.
        Assert.Matches(@"run:[^\n]*Invoke-HeadlessUiCi\.ps1[^\n]*-Shards \d+", headless);
    }

    /// <summary>
    ///     ONE list of harness-fault needles, in the shared module, and every
    ///     caller reads it. They used to be two linted-identical copies (CI and
    ///     the release), and the merge train had none at all: it stopped on the
    ///     known setup race that both of them re-run (#192).
    /// </summary>
    [Fact]
    public void Every_headless_caller_reads_the_one_shared_harness_fault_list()
    {
        static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));
        var ci = Read("tools", "ci", "Invoke-HeadlessUiCi.ps1");
        var release = Read("tools", "release", "New-Release.ps1");
        var gate = Read("tools", "agent", "Invoke-Gate.ps1");
        var train = Read("tools", "agent", "Merge-Train.ps1");
        var module = Read("tools", "ci", "HeadlessShards.psm1");

        var at = module.IndexOf("$script:HeadlessHarnessFaults = @(", StringComparison.Ordinal);
        Assert.True(at >= 0, "HeadlessShards.psm1 no longer defines $script:HeadlessHarnessFaults");
        var needles = Regex.Matches(module[at..module.IndexOf(')', at)], @"""([^""]+)""|'([^']+)'")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        Assert.NotEmpty(needles);

        foreach (var (name, text) in new[] { ("Invoke-HeadlessUiCi.ps1", ci), ("New-Release.ps1", release), ("Invoke-Gate.ps1", gate), ("Merge-Train.ps1", train) })
        {
            Assert.DoesNotMatch(@"HarnessFaults\s*=\s*@\(", text);
            // Code only: a comment (a # line or a <# block #>) may name what a needle matches.
            var code = string.Join('\n', Regex.Replace(text, "<#.*?#>", "", RegexOptions.Singleline)
                .Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            foreach (var needle in needles)
            {
                Assert.False(code.Contains(needle, StringComparison.Ordinal),
                    $"{name} spells the harness-fault needle '{needle}' itself; read the shared list in tools/ci/HeadlessShards.psm1.");
            }
        }

        Assert.Contains("Test-HeadlessHarnessFault $message $stack", ci);
        Assert.Contains("Test-HeadlessHarnessFault $message $stack", release);
        // Invoke-Gate decides the race re-run with the module's decision, over
        // the module's failure reader…
        Assert.Contains("Get-HeadlessRaceRerunDecision @(Read-HeadlessTrxFailures $Trx) $Absent", gate);
        Assert.Contains("Invoke-HeadlessRaceReruns $Label $trx $absent", gate);
        // …and the train gets it by running the headless suite through Invoke-Gate.
        Assert.Matches(@"& powershell -NoProfile -File \$gate -Headless [^\n]*-RaceReruns \$RaceReruns", train);

        // The script runs the suite with the FILTER, as its own process, and
        // records the two numbers CI exists to measure.
        Assert.Contains("'--filter-class', '*UiPerfSmokeTests'", ci);
        Assert.Contains("--no-build", ci);
        Assert.Contains("WorkingSet64", ci);
        Assert.Contains("GITHUB_STEP_SUMMARY", ci);

        // The first run is sharded (tools/ci/HeadlessShards.psm1), and the shard
        // runner measures each shard's peak working set the same way.
        var shards = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "HeadlessShards.psm1"));
        Assert.Contains("HeadlessShards.psm1", ci);
        Assert.Contains("WorkingSet64", shards);
        // …and both scripts still compare what discovery listed against what ran.
        Assert.Contains("@($run.RanClasses) -notcontains $_", ci);
        Assert.Contains("Test-HeadlessCoverage -Filter $Filter -ResultsDir $ResultsDir -Run $run", release);
    }

    /// <summary>
    ///     The software-Vulkan lane of the GPU goldens (#315) runs in its own job,
    ///     only when the dispatch input asks for it, on the hash-checked ICD, with
    ///     the lane switched on for the test step and nowhere else — the gate job
    ///     must keep skipping the GPU tests.
    /// </summary>
    [Fact]
    public void The_software_gpu_lane_is_its_own_opt_in_job()
    {
        var yaml = Workflow().Replace("\r\n", "\n");
        Assert.Matches(@"(?m)^      software-gpu-tests:\n(?:        .*\n)*        type: boolean\n        default: false", yaml);

        var jobs = Jobs(yaml);
        // A script line (inside a `run: |` block), not a comment.
        var lane = jobs.Where(j => Regex.IsMatch(j.Value, @"(?m)^[ \t]+[^#\s][^\n]*Get-SoftwareVulkan\.ps1")).Select(j => j.Key).ToList();
        Assert.Single(lane);
        var job = jobs[lane[0]];
        Assert.Contains("if: ${{ inputs.software-gpu-tests }}", job);
        Assert.Matches(@"DOTCHARTER_GPU_TESTS: sw\n(?:[^\n]*\n)*?\s*run: dotnet test [^\n]*--filter-class ""DotCharter\.Tests\.Rendering\.Headless\.\*""", job);

        foreach (var text in jobs.Where(j => j.Key != lane[0]).Select(j => j.Value))
            Assert.DoesNotContain("DOTCHARTER_GPU_TESTS", text);
    }

    /// <summary>
    ///     CI cannot be run on a hosted runner (no free minutes, #278), so the
    ///     workflow is verified statically. A script path that moved, a floating
    ///     action reference or an unchecked SDK would only show on the next hosted
    ///     run, which is the run nobody can afford.
    /// </summary>
    [Fact]
    public void Every_script_the_workflow_runs_exists_and_every_action_is_pinned()
    {
        var yaml = Workflow();
        var scripts = Regex.Matches(yaml, @"\btools/[\w./-]+\.ps1").Select(m => m.Value).Distinct().ToList();
        Assert.Contains("tools/ci/Invoke-HeadlessUiCi.ps1", scripts);
        Assert.Contains("tools/ci/Get-SoftwareVulkan.ps1", scripts);
        Assert.Contains("tools/setup/Get-NativeRuntime.ps1", scripts);
        foreach (var script in scripts)
        {
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot(), script.Replace('/', Path.DirectorySeparatorChar))),
                $"build.yml runs {script}, which does not exist.");
        }

        var uses = Regex.Matches(yaml, @"(?m)^\s*(?:-\s+)?uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(uses);
        foreach (var action in uses)
            Assert.Matches(@"^[\w.-]+/[\w./-]+@(v\d+(\.\d+)*|[0-9a-f]{40})$", action);
    }

    /// <summary>
    ///     There is no SDK pin in global.json, so <c>setup-dotnet</c> installs the
    ///     newest 10.0.x. The release floor (SDK 10.0.402 / runtime 10.0.13) is
    ///     therefore a step in every job that sets .NET up, not an assumption.
    /// </summary>
    [Fact]
    public void Every_job_that_sets_up_dotnet_then_asserts_the_release_floor()
    {
        var jobs = Jobs(Workflow()).Where(j => j.Value.Contains("actions/setup-dotnet@", StringComparison.Ordinal)).ToList();
        Assert.True(jobs.Count >= 3, "expected the build, headless-ui and software GPU jobs to set .NET up");
        foreach (var (name, text) in jobs)
        {
            var setup = text.IndexOf("actions/setup-dotnet@", StringComparison.Ordinal);
            var assert = text.IndexOf("run: pwsh -NoProfile -File tools/ci/Assert-DotnetSdk.ps1", StringComparison.Ordinal);
            Assert.True(assert > setup, $"job '{name}' sets .NET up without asserting the floor right after it.");
            var restore = text.IndexOf("dotnet restore", StringComparison.Ordinal);
            Assert.True(assert < restore, $"job '{name}' restores before the floor is checked.");
        }

        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "Assert-DotnetSdk.ps1"));
        Assert.Contains("[version]$MinimumSdk = '10.0.402'", script);
        Assert.Contains("[version]$MinimumRuntime = '10.0.13'", script);
    }

    /// <summary>
    ///     The self-contained UI build must come AFTER the test build: the tests
    ///     reference the UI, so the other order leaves a framework-dependent
    ///     runtimeconfig.json next to the self-contained payload (#278 dry run).
    /// </summary>
    [Fact]
    public void The_self_contained_ui_build_runs_after_the_test_build()
    {
        var build = Jobs(Workflow())["build"];
        var tests = build.IndexOf("dotnet build DotCharter.Tests/DotCharter.Tests.csproj", StringComparison.Ordinal);
        var ui = build.IndexOf("dotnet build DotCharter.UI/DotCharter.UI.csproj", StringComparison.Ordinal);
        Assert.True(tests >= 0 && ui > tests, "build the tests first, the self-contained UI last");
        Assert.Contains("-p:SelfContained=true", build[ui..build.IndexOf('\n', ui)]);
    }

    /// <summary>
    ///     The native job takes the prebuilt pair for the gitlink first (#832) and
    ///     builds C++ only when asked for the native suite or when the download
    ///     fails; the release is private, so the download needs the PAT as GH_TOKEN.
    ///     And CI is the PUBLIC build: it never mentions the plugin folder.
    /// </summary>
    [Fact]
    public void The_native_job_downloads_the_prebuilt_runtime_first_and_ci_is_the_public_build()
    {
        var yaml = Workflow().Replace("\r\n", "\n");
        var native = Jobs(yaml)["native"];

        var prebuilt = native.IndexOf("tools/setup/Get-NativeRuntime.ps1", StringComparison.Ordinal);
        var cmake = native.IndexOf("cmake -S ", StringComparison.Ordinal);
        Assert.True(prebuilt >= 0 && cmake > prebuilt, "the prebuilt download must come before the from-source build");
        Assert.Matches(@"id: prebuilt\n(?:[^\n]*\n)*?\s*continue-on-error: true\n(?:[^\n]*\n)*?\s*GH_TOKEN: \$\{\{ secrets\.SUBMODULES_TOKEN \}\}", native);
        Assert.Matches(@"- name: Build JUCE native runtime\n\s*if: \$\{\{ inputs\.native-tests \|\| steps\.prebuilt\.outcome == 'failure' \}\}", native);

        Assert.DoesNotContain("plugins/", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DotCharterPlugins", yaml, StringComparison.Ordinal);
    }
}
