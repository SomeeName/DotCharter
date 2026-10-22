namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>Export-PublicSnapshot.ps1 -Push</c> (#83) against fixture "public" bare repos: it pushes
///     each repo with <c>--atomic</c> only when everything holds, and every refusal leaves BOTH
///     public repos untouched. gitleaks and the snapshot's <c>Invoke-Gate.ps1</c> are stubs whose
///     exit code the case chooses; PATH is cleared of any real gitleaks.
/// </summary>
public sealed partial class PublicSnapshotExportTests
{
    private static void Push_WhenEverythingHolds_PushesBothRepos_AndAnUnchangedJuceSharpIsLeftAsItIs()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-push-").FullName;
        try
        {
            var fixture = NewPushFixture(scratch);
            var run = RunPush(scratch, fixture, "p1");
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.True(File.Exists(fixture.GateMarker), "the push ran without the gate:\n" + run.Output);
            var dc = Git(Path.Combine(scratch, "p1", "DotCharter"), "rev-parse", "main");
            var juce = Git(Path.Combine(scratch, "p1", "JuceSharp.git"), "rev-parse", "main");
            Assert.Equal(dc, Git(fixture.PublicDc, "rev-parse", "main"));
            Assert.Equal(dc, Git(fixture.PublicDc, "rev-parse", "v1.2.3^{commit}"));
            Assert.Equal(juce, Git(fixture.PublicJuce, "rev-parse", "main"));
            Assert.Equal(juce, Git(fixture.PublicJuce, "rev-parse", "v9.8.7^{commit}"));

            // Release 2, JuceSharp unchanged: its commit and tag are public already, so only
            // DotCharter is pushed (the existing JuceSharp tag is never re-pushed).
            WriteTree(fixture.DevDc, "README.md", "More.\n");
            WriteTree(fixture.DevDc, "DotCharter.UI/DotCharter.UI.csproj", Csproj("1.2.4"));
            Commit(fixture.DevDc, "release 2", "README.md", "DotCharter.UI/DotCharter.UI.csproj");
            var second = RunPush(scratch, fixture, "p2");
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.Contains($"JuceSharp: {juce} and tag v9.8.7 are already public; nothing to push.", second.Output, StringComparison.Ordinal);
            Assert.Equal(dc, Git(fixture.PublicDc, "rev-parse", "main^"));
            Assert.Equal(Git(fixture.PublicDc, "rev-parse", "main"), Git(fixture.PublicDc, "rev-parse", "v1.2.4^{commit}"));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    // What each refusal says. "no-gitleaks" and "main-moved" are the slowest refusals, so each
    // has its own class in PublicSnapshotExportTests.Cases.cs; the rest share one.
    private static readonly (string Failure, string Expected)[] PushRefusals =
    [
        ("no-gate", "-Push needs -Gate in the same run"),
        ("gate-fails", "  - build: Invoke-Gate.ps1 -Public -Gate exited 1"),
        ("no-gitleaks", "gitleaks is not installed"),
        ("gitleaks-findings", "gitleaks reported findings (exit 1)"),
        ("pending-pin", "the native pin of the public JuceSharp commit is PENDING"),
        ("identity", "identity: JuceSharp: commit "),
        ("tag-exists", "preflight: DotCharter: tag v1.2.3 already exists for commit "),
        ("main-moved", "  - JuceSharp: public main is ")
    ];

    private static string RefusalMessage(string failure) => PushRefusals.Single(r => r.Failure == failure).Expected;

    private static void Push_Refuses_AndLeavesBothPublicReposUntouched(string failure, string expected)
    {
        var scratch = Directory.CreateTempSubdirectory("dc-public-push-").FullName;
        try
        {
            var fixture = NewPushFixture(scratch, failure);
            var before = (Dc: Refs(fixture.PublicDc), Juce: Refs(fixture.PublicJuce));
            var run = RunPush(scratch, fixture, "p1", failure);
            Assert.True(run.ExitCode != 0, run.Output);
            Assert.True(run.Output.Contains(expected, StringComparison.Ordinal), $"'{expected}' not in:\n{run.Output}");
            if (failure == "main-moved")
                Assert.Contains("-Push refused: the public repos moved during the run, nothing pushed:", run.Output, StringComparison.Ordinal);
            Assert.Equal(before.Dc, Refs(fixture.PublicDc));
            // "main-moved": only the stranger's commit is on public JuceSharp; no snapshot, no tag.
            if (failure == "main-moved")
                Assert.Matches(@"^refs/heads/main [0-9a-f]{40}$", Refs(fixture.PublicJuce));
            else
                Assert.Equal(before.Juce, Refs(fixture.PublicJuce));
            // The cheap refusals come before the gate runs.
            Assert.Equal(failure is "gate-fails" or "main-moved", File.Exists(fixture.GateMarker));
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    private sealed record PushFixture(string DevDc, string DevJuce, string PublicDc, string PublicJuce, string StubDir, string GateMarker);

    // Dev repos that pass every -Push check: the snapshot identity, a JuceSharp with nothing for
    // the rename map (so the dev pin carries over), a stub gate committed in the tree, the native
    // pair files beside the dev checkout, empty public repos. $failure breaks exactly one thing.
    private static PushFixture NewPushFixture(string scratch, string failure = "")
    {
        var identity = PublicSyncGuardTests.SnapshotIdentity();
        var devJuce = Path.Combine(scratch, "dev-juce");
        Git(scratch, "init", "-q", "-b", "main", devJuce);
        WriteTree(devJuce, "Directory.Build.props", Csproj("9.8.7"));
        WriteTree(devJuce, "src/JuceSharp.Native/include/dotcharter_audio.h", "#define DCA_ABI_VERSION 47\n");
        WriteTree(devJuce, "README.md", "Issues: the tracker\n");
        Commit(devJuce, "native work", ".");
        var devJuceCommit = Git(devJuce, "rev-parse", "HEAD");

        var devDc = Path.Combine(scratch, "dev-dc");
        Git(scratch, "init", "-q", "-b", "main", devDc);
        Git(devDc, "config", "user.name", identity.Name);
        Git(devDc, "config", "user.email", failure == "identity" ? "owner@example.com" : identity.Email);
        WriteTree(devDc, "DotCharter.UI/DotCharter.UI.csproj", Csproj("1.2.3"));
        WriteTree(devDc, "README.md", "DotCharter.\n");
        WriteTree(devDc, "tools/setup/native-runtime.json",
            failure == "pending-pin" ? Manifest() : Manifest((devJuceCommit, DllHash, HostHash)));
        WriteTree(devDc, "tools/agent/Invoke-Gate.ps1",
            "param([switch]$Public, [switch]$Build, [switch]$Gate)\n" +
            "Set-Content -LiteralPath $env:DC_FAKE_GATE_MARKER -Value $(if ($Gate) { 'ran -Gate' } else { 'ran -Build' })\n" +
            // "main-moved": someone pushes to public JuceSharp while the gate runs.
            "if ($env:DC_FAKE_GATE_MOVE) {\n" +
            "    $m = Join-Path $env:DC_FAKE_GATE_MOVE_SCRATCH 'mover'; git init -q -b main $m; Set-Content (Join-Path $m 'x.txt') x\n" +
            "    git -C $m add x.txt; git -C $m -c user.name=X -c user.email=x@example.com commit -q -m x; git -C $m push -q $env:DC_FAKE_GATE_MOVE main\n" +
            "}\n" +
            "exit [int]$env:DC_FAKE_GATE_EXIT\n");
        Git(devDc, "add", "--", ".");
        Git(devDc, "update-index", "--add", "--cacheinfo", $"160000,{devJuceCommit},JuceSharp");
        Commit(devDc, "first dev commit");
        foreach (var file in new[] { "juce_sharp.dll", "juce_vst_host.exe" })
            WriteTree(devDc, Path.Combine("JuceSharp", "src", "JuceSharp", "runtimes", "win-x64", "native", file), "not a real binary\n");

        var publicDc = Path.Combine(scratch, "public-dc.git");
        var publicJuce = Path.Combine(scratch, "public-juce.git");
        Git(scratch, "init", "-q", "--bare", "-b", "main", publicDc);
        Git(scratch, "init", "-q", "--bare", "-b", "main", publicJuce);
        if (failure == "tag-exists")
        {
            // Public DotCharter already has v1.2.3 for its first snapshot; dev is still 1.2.3 with
            // a new tree. JuceSharp is empty, so a push that went repo by repo would have pushed it.
            var seed = Path.Combine(scratch, "seed-dc");
            Git(scratch, "init", "-q", "-b", "main", seed);
            WriteTree(seed, "README.md", "older\n");
            Git(seed, "add", "--", ".");
            Git(seed, "-c", $"user.name={identity.Name}", "-c", $"user.email={identity.Email}", "commit", "-q", "-m", "Initial public commit of DotCharter");
            Git(seed, "-c", $"user.name={identity.Name}", "-c", $"user.email={identity.Email}", "tag", "-a", "-m", "Initial public commit of DotCharter", "v1.2.3");
            Git(seed, "push", "-q", publicDc, "main", "v1.2.3");
        }

        var stubDir = Path.Combine(scratch, "stubs");
        WriteTree(stubDir, "gitleaks.ps1", "exit [int]$env:DC_FAKE_GITLEAKS_EXIT\n");
        return new PushFixture(devDc, devJuce, publicDc, publicJuce, stubDir, Path.Combine(scratch, "gate-ran.txt"));
    }

    private static (int ExitCode, string Output) RunPush(string scratch, PushFixture f, string root, string failure = "")
    {
        if (File.Exists(f.GateMarker)) File.Delete(f.GateMarker);
        var stubs = failure == "no-gitleaks" ? "" : Quote(f.StubDir) + " + [IO.Path]::PathSeparator + ";
        var gate = failure == "no-gate" ? "" : " -Gate";
        var move = failure == "main-moved" ? Quote(f.PublicJuce) : "''";
        return RunPwsh(scratch, $$"""
            $sep = [IO.Path]::PathSeparator
            $env:PATH = {{stubs}}(($env:PATH -split $sep | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'gitleaks*')) }) -join $sep)
            $env:DC_FAKE_GATE_MARKER = {{Quote(f.GateMarker)}}
            $env:DC_FAKE_GATE_EXIT = '{{(failure == "gate-fails" ? 1 : 0)}}'
            $env:DC_FAKE_GITLEAKS_EXIT = '{{(failure == "gitleaks-findings" ? 1 : 0)}}'
            $env:DC_FAKE_GATE_MOVE = {{move}}
            $env:DC_FAKE_GATE_MOVE_SCRATCH = {{Quote(scratch)}}
            & {{Quote(ExportScript)}} -DevRepo {{Quote(f.DevDc)}} -DevRef main -DevJuceSharpGitDir {{Quote(Path.Combine(f.DevJuce, ".git"))}} -ScratchRoot {{Quote(Path.Combine(scratch, root))}} -PublicDotCharterUrl {{Quote(f.PublicDc)}} -PublicJuceSharpUrl {{Quote(f.PublicJuce)}}{{gate}} -Push
            exit $LASTEXITCODE
            """);
    }

    // Every ref of a bare repo with its id ("" when it has none).
    private static string Refs(string bareRepo) => Git(bareRepo, "for-each-ref", "--format=%(refname) %(objectname)");
}
