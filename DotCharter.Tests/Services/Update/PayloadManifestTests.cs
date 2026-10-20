using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The delta an update actually has to download. This is the arithmetic the
///     patch packaging and the apply step will both trust, so every rule that
///     could silently ship a broken install is pinned here.
/// </summary>
public sealed class PayloadManifestTests
{
    private static PayloadFile F(string path, string hash, long size = 10) => new(path, hash, size);

    private static PayloadManifest M(string version, params PayloadFile[] files) =>
        new(version, InstallLayout.SelfContained, files);

    /// <summary>
    ///     The layout-critical files, unchanged, so tests can focus on their own
    ///     case. Derived from <see cref="PayloadManifest.AlwaysInclude" /> rather
    ///     than restated, so extending that set cannot leave this fixture behind.
    /// </summary>
    private static PayloadFile[] Core(string hash = "c0") =>
        [.. PayloadManifest.AlwaysInclude.Select(name => F(name, hash))];

    [Fact]
    public void AnIdenticalFileIsNotResent()
    {
        var from = M("0.40.0", [.. Core(), F("juce_sharp.dll", "aaa", 9_000_000)]);
        var to = M("0.41.0", [.. Core(), F("juce_sharp.dll", "aaa", 9_000_000)]);

        var delta = PayloadDelta.Between(from, to);

        Assert.Contains("juce_sharp.dll", delta.Unchanged.Select(f => f.Path));
        Assert.DoesNotContain("juce_sharp.dll", delta.ToSend.Select(f => f.Path));
        Assert.Equal(9_000_000, delta.BytesSaved);
    }

    [Fact]
    public void AChangedFileIsResent()
    {
        var from = M("0.40.0", [.. Core(), F("DotCharter.dll", "aaa")]);
        var to = M("0.41.0", [.. Core(), F("DotCharter.dll", "bbb")]);

        var delta = PayloadDelta.Between(from, to);

        Assert.Contains("DotCharter.dll", delta.Changed.Select(f => f.Path));
    }

    [Fact]
    public void ANewFileIsAdded()
    {
        var delta = PayloadDelta.Between(M("0.40.0", Core()), M("0.41.0", [.. Core(), F("new.dll", "n")]));

        Assert.Equal(["new.dll"], delta.Added.Select(f => f.Path));
    }

    /// <summary>
    ///     An update is robocopied with NO PURGE, so a file the new release dropped
    ///     survives forever unless the apply step is told to remove it. That makes
    ///     this list load-bearing rather than informational.
    /// </summary>
    [Fact]
    public void ARemovedFileIsReportedForDeletion()
    {
        var delta = PayloadDelta.Between(M("0.40.0", [.. Core(), F("gone.dll", "g")]), M("0.41.0", Core()));

        Assert.Equal(["gone.dll"], delta.Deleted);
    }

    /// <summary>
    ///     <c>AppPayload.Validate</c> refuses a payload without these three, and it
    ///     runs against the STAGED patch before the irreversible updater script
    ///     launches. A patch that omitted them because their hash happened to match
    ///     would be rejected at apply time — or worse, strand the old hostfxr next
    ///     to a new runtimeconfig.
    /// </summary>
    [Fact]
    public void TheLayoutCriticalFilesAreAlwaysResentEvenWhenUnchanged()
    {
        var delta = PayloadDelta.Between(M("0.40.0", Core("same")), M("0.41.0", Core("same")));

        Assert.Empty(delta.Unchanged);
        foreach (var required in PayloadManifest.AlwaysInclude)
            Assert.Contains(required, delta.Changed.Select(f => f.Path));
    }

    [Fact]
    public void PathsCompareCaseInsensitively()
    {
        var from = M("0.40.0", [.. Core(), F("Runtimes/Win-X64/a.dll", "aaa")]);
        var to = M("0.41.0", [.. Core(), F("runtimes/win-x64/a.dll", "aaa")]);

        var delta = PayloadDelta.Between(from, to);

        // Same file on a Windows payload: neither re-sent nor reported deleted.
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Deleted);
    }

    [Fact]
    public void ARepublishedIdenticalReleaseIsNotEmptyBecauseTheCoreFilesAlwaysShip()
    {
        // Deliberate: IsEmpty means "nothing to do", and the core files always ship,
        // so an identical republish still produces a (tiny) patch.
        var delta = PayloadDelta.Between(M("0.40.0", Core()), M("0.40.0", Core()));

        Assert.False(delta.IsEmpty);
        Assert.Equal(PayloadManifest.AlwaysInclude.Length, delta.Changed.Count);
    }

    // ---- building from disk -------------------------------------------------

    [Fact]
    public void BuildHashesEveryFileAndForwardSlashesItsPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dc-manifest-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(root, "runtimes", "win-x64", "b.txt"), "hello");

            var manifest = PayloadManifest.Build(root, "1.2.3");

            Assert.Equal("1.2.3", manifest.Version);
            Assert.Contains("runtimes/win-x64/b.txt", manifest.Files.Select(f => f.Path));
            Assert.DoesNotContain(manifest.Files, f => f.Path.Contains('\\'));

            // Same bytes, same hash — which is exactly what lets a delta skip it.
            var a = manifest.Files.Single(f => f.Path == "a.txt");
            var b = manifest.Files.Single(f => f.Path == "runtimes/win-x64/b.txt");
            Assert.Equal(a.Sha256, b.Sha256);
            Assert.Equal(5, a.Size);
        }
        finally
        {
            BestEffortDelete.Directory(root);
        }
    }

    [Fact]
    public void BuildHonoursTheExclusionPredicate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dc-manifest-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
            File.WriteAllText(Path.Combine(root, "skip.log"), "x");

            var manifest = PayloadManifest.Build(root, "1.0.0",
                exclude: p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(["keep.txt"], manifest.Files.Select(f => f.Path));
        }
        finally
        {
            BestEffortDelete.Directory(root);
        }
    }

    [Fact]
    public void ManifestRoundTripsThroughJson()
    {
        var manifest = M("0.41.0", [.. Core(), F("x.dll", "abc", 42)]);

        var restored = PayloadManifest.FromJson(manifest.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("0.41.0", restored.Version);
        Assert.Equal(InstallLayout.SelfContained, restored.Layout);
        Assert.Equal(manifest.Files.Count, restored.Files.Count);
        Assert.Equal(42, restored.Files.Single(f => f.Path == "x.dll").Size);
    }

    /// <summary>
    ///     A manifest that cannot be read must degrade to "no manifest" so the
    ///     updater falls back to the FULL download, never to a wrong delta.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void AnUnreadableManifestIsNullRatherThanAThrow(string? json) => Assert.Null(PayloadManifest.FromJson(json));
}
