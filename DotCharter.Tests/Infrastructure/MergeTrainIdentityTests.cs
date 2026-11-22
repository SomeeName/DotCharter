using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Train commit identity must not poison the environment inherited by later gates (#673).</summary>
public sealed class MergeTrainIdentityTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dc-train-identity-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Commit_RestoresIdentityAndAllowsTheNextCommit(bool fails, bool previouslySet) =>
        Check("powershell", fails, previouslySet);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Commit_UnderPwsh_RestoresIdentityAndAllowsTheNextCommit(bool fails, bool previouslySet) =>
        Check("pwsh", fails, previouslySet);

    private void Check(string host, bool fails, bool previouslySet)
    {
        var run = PublicSnapshotExportTests.RunPowerShell(_scratch, $$$"""
            Import-Module '{{{Path.Combine(TestPaths.RepoRoot(), "tools", "agent", "MergeTrain.psm1").Replace("'", "''", StringComparison.Ordinal)}}}' -Force
            $repo = Join-Path (Get-Location) 'repo'
            $identityKeys = @('GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL')
            $keys = $identityKeys + @('GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_DIR', 'GIT_WORK_TREE')
            $saved = @{}
            foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
            function Invoke-FixtureGit([string[]]$Argv) {
                $savedPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                try { $out = & git -C $repo @Argv 2>&1; $code = $LASTEXITCODE }
                finally { $ErrorActionPreference = $savedPreference }
                [pscustomobject]@{ Code = $code; Out = ($out | Out-String).Trim() }
            }
            function Must-FixtureGit([string[]]$Argv) {
                $r = Invoke-FixtureGit $Argv
                if ($r.Code -ne 0) { throw "fixture git failed: $($r.Out)" }
                $r.Out
            }
            try {
                foreach ($key in $keys) { Remove-Item -LiteralPath "Env:\$key" -ErrorAction SilentlyContinue }
                $null = New-Item -ItemType Directory $repo
                $null = Must-FixtureGit @('init', '-q', '-b', 'main')
                $null = Must-FixtureGit @('config', 'user.name', 'Configured Bot')
                $null = Must-FixtureGit @('config', 'user.email', 'bot@users.noreply.github.com')
                $null = Must-FixtureGit @('config', 'commit.gpgsign', 'false')
                $null = Must-FixtureGit @('commit', '--allow-empty', '-qm', 'base')
                $null = Must-FixtureGit @('switch', '-qc', 'feature')
                $null = Must-FixtureGit @('commit', '--allow-empty', '-qm', 'feature')
                $head = Must-FixtureGit @('rev-parse', 'HEAD')
                $null = Must-FixtureGit @('switch', '-q', 'main')
                if (${{{previouslySet.ToString().ToLowerInvariant()}}}) {
                    $env:GIT_AUTHOR_NAME = 'Original Person'; $env:GIT_COMMITTER_NAME = 'Original Person'
                    $env:GIT_AUTHOR_EMAIL = 'original@example.invalid'; $env:GIT_COMMITTER_EMAIL = 'original@example.invalid'
                }
                if (${{{fails.ToString().ToLowerInvariant()}}}) { $head = 'missing-fixture-ref' }
                $pr = [pscustomobject]@{ Number = 1; Owner = 'owner'; Branch = 'feature'; Title = 'Feature'; Head = $head }
                $merge = New-MergeTrainCommit -Pr $pr -Git { param([string[]]$Argv) Invoke-FixtureGit $Argv }
                $restored = @(foreach ($key in $identityKeys) {
                    [pscustomobject]@{ Key = $key; Exists = (Test-Path -LiteralPath "Env:\$key"); Value = [Environment]::GetEnvironmentVariable($key, 'Process') }
                })
                $child = Invoke-FixtureGit @('-c', 'user.name=Dev', '-c', 'user.email=dev@example.com', 'commit', '--allow-empty', '-qm', 'after train')
                [pscustomobject]@{ MergeCode = $merge.Code; ChildCode = $child.Code; ChildOutput = $child.Out; Restored = $restored } | ConvertTo-Json -Depth 4 -Compress
            }
            finally {
                foreach ($key in $keys) {
                    if ($null -eq $saved[$key]) { Remove-Item -LiteralPath "Env:\$key" -ErrorAction SilentlyContinue }
                    else { Set-Item -LiteralPath "Env:\$key" -Value $saved[$key] }
                }
            }
            """, host);
        Assert.True(run.ExitCode == 0, run.Output);
        using var result = JsonDocument.Parse(run.Output.Trim());
        var row = result.RootElement;
        if (fails)
            Assert.NotEqual(0, row.GetProperty("MergeCode").GetInt32());
        else
            Assert.Equal(0, row.GetProperty("MergeCode").GetInt32());

        foreach (var restored in row.GetProperty("Restored").EnumerateArray())
        {
            Assert.Equal(previouslySet, restored.GetProperty("Exists").GetBoolean());
            var expected = previouslySet
                ? restored.GetProperty("Key").GetString()!.EndsWith("EMAIL", StringComparison.Ordinal) ? "original@example.invalid" : "Original Person"
                : null;
            Assert.Equal(expected, restored.GetProperty("Value").GetString());
        }

        Assert.True(row.GetProperty("ChildCode").GetInt32() == 0, row.GetProperty("ChildOutput").GetString());
    }
}
