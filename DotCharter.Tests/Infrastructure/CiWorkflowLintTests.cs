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
    ///     The job-level guard that keeps the PRIVATE dev repo manual-only while the
    ///     public repo runs on push and pull_request. Both repos carry the same
    ///     workflow; on the private one a push or PR then creates a run whose jobs
    ///     are all skipped (0 minutes), while the "Run workflow" button still runs
    ///     everything. Windows minutes bill at 2x against the private repo's 2,000
    ///     free a month, and one PR run cost ~240 of them (#63).
    /// </summary>
    private const string PrivateRepoGate = "github.event_name == 'workflow_dispatch' || !github.event.repository.private";

    private static string[] Triggers(string yaml, string file)
    {
        var on = Regex.Match(yaml, @"^on:[ \t]*\n((?:[ \t]+.*\n|[ \t]*\n)+)", RegexOptions.Multiline);
        Assert.True(on.Success, $"{file}: no multi-line on: block");
        return [.. Regex.Matches(on.Groups[1].Value, @"^  ([a-z_]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    ///     The jobs that could start on the private repo without a manual dispatch:
    ///     none when <c>workflow_dispatch</c> is the only trigger, otherwise every job
    ///     whose job-level <c>if:</c> does not AND in <see cref="PrivateRepoGate"/>
    ///     (on its own, or as <c>${{ x &amp;&amp; (gate) }}</c> with no <c>||</c> in x).
    /// </summary>
    private static List<string> JobsThatCanRunUnattendedOnThePrivateRepo(string yaml, string file)
    {
        var triggers = Triggers(yaml, file);
        Assert.Contains("workflow_dispatch", triggers);
        if (triggers is ["workflow_dispatch"]) return [];

        const string suffix = $"&& ({PrivateRepoGate})";
        var ungated = new List<string>();
        foreach (var (id, body) in Jobs(yaml))
        {
            var condition = Regex.Match(body, @"^    if:[ \t]*(.+?)[ \t]*$", RegexOptions.Multiline);
            var expression = condition.Success ? condition.Groups[1].Value : "";
            if (expression.StartsWith("${{", StringComparison.Ordinal) && expression.EndsWith("}}", StringComparison.Ordinal))
                expression = expression[3..^2].Trim();

            var rest = expression == PrivateRepoGate ? ""
                : expression.EndsWith(suffix, StringComparison.Ordinal) ? expression[..^suffix.Length]
                : null;
            if (rest is null || rest.Contains("||", StringComparison.Ordinal))
                ungated.Add($"{file}: job '{id}' (if: {(condition.Success ? condition.Groups[1].Value : "<none>")})");
        }
        return ungated;
    }

    /// <summary>
    ///     A trigger besides the manual button is allowed only when EVERY job is
    ///     gated on <see cref="PrivateRepoGate"/>, so the private dev repo never
    ///     spends a minute it was not asked to; and a newer run of the same branch
    ///     cancels the older one.
    /// </summary>
    [Fact]
    public void Ci_runs_unattended_only_on_the_public_repo_and_cancels_superseded_runs()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot(), ".github", "workflows"), "*.y*ml"))
        {
            var yaml = File.ReadAllText(file).Replace("\r\n", "\n");
            // The one exemption: the daily public sync watch, a ~1 minute Linux job that
            // runs ONLY in the private *-Dev repos (schedule or dispatch); its shape and
            // its `endsWith(github.repository, '-Dev')` guard are pinned by
            // PublicSyncWatchWorkflowLintTests.
            if (Path.GetFileName(file) == PublicSyncWatchWorkflowLintTests.FileName)
            {
                var triggers = Triggers(yaml, PublicSyncWatchWorkflowLintTests.FileName);
                Assert.True(triggers.Order(StringComparer.Ordinal).SequenceEqual(["schedule", "workflow_dispatch"]),
                    $"{PublicSyncWatchWorkflowLintTests.FileName} triggers on [{string.Join(", ", triggers)}]; it may only run on schedule or by hand.");
                continue;
            }

            var ungated = JobsThatCanRunUnattendedOnThePrivateRepo(yaml, Path.GetFileName(file));
            Assert.True(ungated.Count == 0,
                $"Every job of a workflow with a trigger other than workflow_dispatch must carry `if: {PrivateRepoGate}` "
                + $"(or `${{{{ <condition> && ({PrivateRepoGate}) }}}}`), so the private dev repo stays manual-only:\n  "
                + string.Join("\n  ", ungated));
        }

        Assert.Matches(@"(?m)^concurrency:[ \t]*\n(?:  .*\n)*  cancel-in-progress: true", Workflow().Replace("\r\n", "\n"));
    }

    /// <summary>
    ///     The lint above is not vacuous: an ungated push trigger, a gate OR-ed
    ///     away and an <c>always()</c> without the gate are each reported, while the
    ///     manual-only and the fully gated shapes pass. Step-level <c>if:</c>s
    ///     (deeper indent) never count as the job's gate.
    /// </summary>
    [Theory]
    [InlineData("  push:\n    branches: [main]\n  workflow_dispatch:\n", "", "    if: always()\n", 2)]
    [InlineData("  pull_request:\n  workflow_dispatch:\n", $"    if: {PrivateRepoGate}\n", "", 1)]
    [InlineData("  push:\n  workflow_dispatch:\n", $"    if: ${{{{ always() || ({PrivateRepoGate}) }}}}\n", $"    if: {PrivateRepoGate}\n", 1)]
    [InlineData("  push:\n  workflow_dispatch:\n", $"    if: {PrivateRepoGate}\n", $"    if: ${{{{ always() && ({PrivateRepoGate}) }}}}\n", 0)]
    [InlineData("  workflow_dispatch:\n", "", "", 0)]
    public void The_trigger_lint_reports_every_job_that_could_run_unattended_on_the_private_repo(string on, string firstIf, string secondIf, int expected)
    {
        var yaml = $"name: t\n\non:\n{on}\njobs:\n  first:\n    name: First\n{firstIf}    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"
                 + $"  second:\n    needs: first\n{secondIf}    runs-on: ubuntu-latest\n    steps:\n      - if: {PrivateRepoGate}\n        run: echo\n";
        Assert.Equal(expected, JobsThatCanRunUnattendedOnThePrivateRepo(yaml, "fixture.yml").Count);
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
    ///     A failed headless run must leave its evidence (#55): the script keeps the
    ///     TRX and messages in a folder the workflow names, and the job uploads that
    ///     folder as <c>headless-failures</c>, only on failure, for a short while.
    /// </summary>
    [Fact]
    public void The_headless_job_uploads_the_failure_evidence_folder_on_failure()
    {
        var jobs = Jobs(Workflow());
        var job = jobs.Single(j => Regex.IsMatch(j.Value, @"run:[^\n]*Invoke-HeadlessUiCi\.ps1")).Value.Replace("\r\n", "\n");

        Assert.Contains("DOTCHARTER_HEADLESS_FAILURES_DIR: TestResults/headless-failures", job);
        Assert.Matches(@"if: failure\(\)\n\s*uses: actions/upload-artifact@[0-9a-f]{40}[^\n]*\n\s*with:\n\s*name: headless-failures\n\s*path: TestResults/headless-failures/\n(?:\s*if-no-files-found: \w+\n)?\s*retention-days: 7\b", job);

        var ci = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "ci", "Invoke-HeadlessUiCi.ps1"));
        Assert.Contains("Save-HeadlessFailureEvidence", ci);
        var gate = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "Invoke-Gate.ps1"));
        Assert.Contains("Save-HeadlessFailureEvidence", gate);
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
        Assert.Contains($"if: ${{{{ inputs.software-gpu-tests && ({PrivateRepoGate}) }}}}", job);
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

        // A full commit SHA only: a tag ("v7") can be moved by whoever controls the
        // action's repository, a SHA cannot. The version rides in a comment.
        var uses = Regex.Matches(yaml, @"(?m)^\s*(?:-\s+)?uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(uses);
        foreach (var action in uses)
            Assert.Matches(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$", action);

        // No checkout leaves the token in .git/config for the steps after it.
        var checkouts = Regex.Matches(yaml.Replace("\r\n", "\n"), @"(?m)^[ \t]*(?:-[ \t]+)?uses:[ \t]*actions/checkout@\S+.*\n((?:(?![ \t]*-[ \t])(?![ \t]*\n)[ \t]+.*\n)*)");
        Assert.True(checkouts.Count >= 4, $"only {checkouts.Count} checkouts found");
        foreach (Match checkout in checkouts)
            Assert.Matches(@"(?m)^\s+persist-credentials: false\s*$", checkout.Groups[1].Value);

        // The event is part of the concurrency group: a push run (all jobs skipped on
        // the private repo) must never cancel a manual run of the same ref.
        Assert.Matches(@"(?m)^  group: .*\$\{\{ github\.event_name \}\}", yaml);
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
    ///     fails. The dev repo's release is private, so ONLY the private repo hands
    ///     the download the PAT as GH_TOKEN; the public repo (and a fork's pull
    ///     request) uses the run's own token.
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
        Assert.Matches(@"id: prebuilt\n(?:[^\n]*\n)*?\s*continue-on-error: true\n(?:[^\n]*\n)*?\s*GH_TOKEN: \$\{\{ github\.event\.repository\.private && secrets\.SUBMODULES_TOKEN \|\| github\.token \}\}", native);
        Assert.Matches(@"- name: Build JUCE native runtime\n\s*if: \$\{\{ inputs\.native-tests \|\| steps\.prebuilt\.outcome == 'failure' \}\}", native);

        Assert.DoesNotContain("plugins/", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DotCharterPlugins", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The submodule PAT lives for ONE git command. It reaches the submodule
    ///     fetch through <c>git -c</c> (never <c>git config --global</c>, which every
    ///     later step - build, tests, branch scripts - could read), with the
    ///     credential helper switched off so nothing stores it, and only the steps
    ///     named here may mention the secret at all.
    /// </summary>
    [Fact]
    public void The_submodule_token_lives_for_one_git_command_and_reaches_no_other_step()
    {
        var yaml = Workflow().Replace("\r\n", "\n");
        Assert.DoesNotMatch(@"git\s+config\s+(?:--global|--system)", yaml);

        var checkouts = 0;
        foreach (var (job, text) in Jobs(yaml))
        {
            foreach (var step in Regex.Split(text, @"\n(?=      - )").Where(s => s.Contains("secrets.", StringComparison.Ordinal)))
            {
                var name = Regex.Match(step, @"- name: ([^\n]+)").Groups[1].Value.Trim();
                // A step holding a secret hands nothing on to the steps after it.
                Assert.DoesNotMatch(@"GITHUB_(ENV|OUTPUT|PATH|STATE)", step);
                if (name == "Prebuilt native runtime")
                {
                    Assert.Matches(@"\n\s+GH_TOKEN: \$\{\{ github\.event\.repository\.private && secrets\.SUBMODULES_TOKEN \|\| github\.token \}\}\n", step);
                    continue;
                }

                Assert.True(name == "Checkout submodules (JuceSharp)", $"job '{job}', step '{name}' reads a secret; only the submodule checkout may.");
                checkouts++;
                Assert.Matches(@"\n\s+SUBMODULES_TOKEN: \$\{\{ secrets\.SUBMODULES_TOKEN \}\}\n", step);
                var uses = step.Split('\n').Where(l => l.Contains("$env:SUBMODULES_TOKEN", StringComparison.Ordinal) && l.Trim() != "if ($env:SUBMODULES_TOKEN) {").ToList();
                Assert.NotEmpty(uses);
                foreach (var line in uses)
                {
                    Assert.Matches(@"^\s*git -c credential\.helper= -c ", line);
                    Assert.Contains("submodule update", line, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(checkouts >= 4, $"only {checkouts} submodule checkouts carry the token");
    }

    /// <summary>
    ///     Every DotCharter.* project commits a NuGet lock file (version + content
    ///     hash of each package, transitive ones included), and every CI restore
    ///     runs in locked mode, so a restore that would resolve anything else
    ///     fails instead of building it.
    /// </summary>
    [Fact]
    public void Every_restore_is_locked_to_the_committed_lock_files()
    {
        var root = TestPaths.RepoRoot();
        var yaml = Workflow().Replace("\r\n", "\n");
        var restores = Regex.Matches(yaml, @"(?m)^.*\bdotnet restore\b.*$").Select(m => m.Value).ToList();
        Assert.True(restores.Count >= 4, $"only {restores.Count} dotnet restore lines found");
        foreach (var restore in restores)
            Assert.Contains("--locked-mode", restore, StringComparison.Ordinal);

        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", props, StringComparison.Ordinal);

        var solution = File.ReadAllText(Path.Combine(root, "DotCharter.sln"));
        var projects = Regex.Matches(solution, @"""([^""]*\\(DotCharter[^""\\]*)\.csproj)""").Select(m => m.Groups[1].Value).ToList();
        Assert.True(projects.Count >= 10, $"only {projects.Count} DotCharter projects in the solution");
        foreach (var project in projects)
        {
            var folder = Path.GetDirectoryName(Path.Combine(root, project.Replace('\\', Path.DirectorySeparatorChar)))!;
            Assert.True(File.Exists(Path.Combine(folder, "packages.lock.json")), $"{project} has no committed packages.lock.json");
        }
    }
}
