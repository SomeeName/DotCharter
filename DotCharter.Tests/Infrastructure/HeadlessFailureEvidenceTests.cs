using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>Save-HeadlessFailureEvidence</c> in <c>tools/ci/HeadlessShards.psm1</c> (#55):
///     a failed headless run keeps its TRX, the failed cases' messages, stack traces
///     and stdout in a timestamped folder, prunes old folders, and lets a later
///     re-run add to the SAME folder so the original failure survives a green re-run.
///     Executed against the module itself, over a TRX in the shape the runner writes.
/// </summary>
public sealed class HeadlessFailureEvidenceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-failure-evidence-" + Guid.NewGuid().ToString("N"));

    public HeadlessFailureEvidenceTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    private const string Name = "DotCharter.Tests.Perf.SomeUiPerfSmokeTests.Hits(x: 1)";

    [Fact]
    public void AFailedCase_LeavesItsMessageStackAndStdout_InATimestampedFolder()
    {
        var trx = WriteTrx(failed: true);
        var root = Path.Combine(_scratch, "root");

        var folder = Save(trx, root);

        Assert.StartsWith(root, folder, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^\d{8}-\d{6}", Path.GetFileName(folder));
        var text = File.ReadAllText(Path.Combine(folder, "failures.txt"));
        Assert.Contains(Name, text);
        Assert.Contains("the hit test missed the button", text);
        Assert.Contains("at Some.Frame() in X.cs:line 42", text);
        Assert.Contains("captured stdout line", text);
        Assert.Single(Directory.GetFiles(folder, "*.trx"));
    }

    [Fact]
    public void AGreenRun_KeepsNothing()
    {
        var root = Path.Combine(_scratch, "root");

        var folder = Save(WriteTrx(failed: false), root);

        Assert.Equal("", folder);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ALaterCapture_IsAddedToTheSameFolder_SoTheOriginalSurvivesAGreenRerun()
    {
        var root = Path.Combine(_scratch, "root");
        var first = Save(WriteTrx(failed: true), root);

        var json = HeadlessShardsModule.Run(_scratch, $$"""
            $r = Save-HeadlessFailureEvidence -TrxPath @('{{WriteTrx(failed: false).Replace("'", "''")}}') -Label 'rerun1' -Folder '{{first.Replace("'", "''")}}'
            if ($r) { $r } else { '' }
            """);

        Assert.Equal("", json);
        Assert.Contains("the hit test missed the button", File.ReadAllText(Path.Combine(first, "failures.txt")));
        Assert.Single(Directory.GetFiles(first, "*.trx")); // a green re-run adds nothing and removes nothing
    }

    [Fact]
    public void OnlyTheNewestFolders_AreKept()
    {
        var root = Path.Combine(_scratch, "root");
        foreach (var i in Enumerable.Range(1, 25))
            Directory.CreateDirectory(Path.Combine(root, $"20260101-0000{i:00}"));
        Directory.CreateDirectory(Path.Combine(root, "not-a-run"));

        var folder = Save(WriteTrx(failed: true), root);

        var kept = Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(21, kept.Count); // 20 timestamped + the one that is not a run folder
        Assert.Contains(Path.GetFileName(folder), kept);
        Assert.DoesNotContain("20260101-000001", kept);
        Assert.Contains("20260101-000025", kept);
        Assert.Contains("not-a-run", kept);
    }

    private string Save(string trx, string root) =>
        HeadlessShardsModule.Run(_scratch, $$"""
            $r = Save-HeadlessFailureEvidence -TrxPath @('{{trx.Replace("'", "''")}}') -Root '{{root.Replace("'", "''")}}' -Label 'first-run'
            if ($r) { $r } else { '' }
            """);

    private string WriteTrx(bool failed)
    {
        var body = failed
            ? $"""<UnitTestResult testName="{Name}" outcome="Failed"><Output><StdOut>captured stdout line</StdOut><ErrorInfo><Message>the hit test missed the button</Message><StackTrace>   at Some.Frame() in X.cs:line 42</StackTrace></ErrorInfo></Output></UnitTestResult>"""
            : $"""<UnitTestResult testName="{Name}" outcome="Passed" />""";
        var path = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path,
            $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{body}</Results><ResultSummary outcome="{(failed ? "Failed" : "Completed")}"><Counters total="1" executed="1" passed="{(failed ? 0 : 1)}" failed="{(failed ? 1 : 0)}" /></ResultSummary></TestRun>""");
        return path;
    }
}
