using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Diagnostic output is per invocation: two shards never share a file, and the folder exists before a write.</summary>
public sealed class TestArtifactRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-artifact-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void Two_shards_started_together_get_two_folders()
    {
        Assert.NotEqual(TestArtifactRun.For("20261114-120000", 100), TestArtifactRun.For("20261114-120000", 101));
        Assert.NotEqual(TestArtifactRun.For("20261114-120000", 100), TestArtifactRun.For("20261114-120001", 100));
        Assert.Contains($"pid{Environment.ProcessId}", TestArtifactRun.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_and_its_missing_parents_are_created_before_anything_is_written()
    {
        var root = Path.Combine(_root, "not", "there", "yet");

        var dir = TestArtifactRun.Directory(root);
        Assert.True(Directory.Exists(dir), "the artifact folder and all parents must exist before a diagnostic write");
        File.WriteAllText(Path.Combine(dir, "probe.txt"), "ok");

        Assert.Equal(Path.Combine(root, TestArtifactRun.Name), dir);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(dir, "probe.txt")));
    }
}
