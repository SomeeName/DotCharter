using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>Independent handles and injected commit boundaries model competing app instances.</summary>
public sealed class StemPublicationExclusionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-publication-").FullName;
    private string Destination => Directory.CreateDirectory(Path.Combine(_root, "stems")).FullName;
    private string Pending => Path.Combine(Destination, StemCacheExpansion.PendingFileName);

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Theory]
    [InlineData("PendingWritten")]
    [InlineData("WavPublished")]
    [InlineData("CompletionWritten")]
    [InlineData("Completed")]
    public void A_probe_never_adopts_or_deletes_an_active_publication(string boundaryName)
    {
        var boundary = Enum.Parse<StemCacheExpansion.Stage>(boundaryName);
        var calls = 0;
        StemCacheExpansion.Publish(Stage("a"), Destination, new string('a', 32), reached =>
        {
            if (reached != boundary) return;
            calls++;
            var marker = File.Exists(Pending) ? File.ReadAllText(Pending) : null;
            Assert.Empty(Probe().Recovered);
            Assert.Equal(marker, File.Exists(Pending) ? File.ReadAllText(Pending) : null);
        });

        Assert.True(calls > 0);
        Assert.Equal(2, Probe().Recovered.Count);
        Assert.False(File.Exists(Pending));
    }

    [Theory]
    [InlineData("PendingWritten", false)]
    [InlineData("WavPublished", false)]
    [InlineData("CompletionWritten", true)]
    [InlineData("Completed", true)]
    public void An_interrupted_publisher_releases_exclusion_and_preserves_the_crash_boundary(
        string boundaryName, bool complete)
    {
        var boundary = Enum.Parse<StemCacheExpansion.Stage>(boundaryName);
        Assert.Throws<InvalidOperationException>(() => StemCacheExpansion.Publish(Stage("a"), Destination, new string('a', 32), reached =>
        {
            if (reached == boundary) throw new InvalidOperationException("Injected interruption");
        }));

        Assert.Equal(complete ? 2 : 0, Probe().Recovered.Count);
        Assert.Equal(!complete, File.Exists(Pending));
    }

    [Fact]
    public void A_validated_recovery_excludes_a_second_publisher_until_its_marker_is_cleared()
    {
        CompleteWithPending();
        var before = File.ReadAllText(Pending);
        var staged = Stage("b");
        Assert.True(StemCacheExpansion.CanRecover(Destination, () =>
        {
            Assert.Throws<IOException>(() => StemCacheExpansion.Publish(staged, Destination, new string('b', 32)));
            Assert.Equal(before, File.ReadAllText(Pending));
            Assert.All(staged, stem => Assert.True(File.Exists(stem.Path)));
        }));

        Assert.False(File.Exists(Pending));
        StemCacheExpansion.Publish(staged, Destination, new string('b', 32));
        Assert.Equal(2, Probe().Recovered.Count);
    }

    [Fact]
    public void A_changed_marker_is_never_deleted_as_the_validated_publication()
    {
        CompleteWithPending();
        var next = new string('b', 32);
        Assert.False(StemCacheExpansion.CanRecover(Destination, () => File.WriteAllText(Pending, next)));
        Assert.Equal(next, File.ReadAllText(Pending));
        Assert.Empty(Probe().Recovered);
    }

    [Fact]
    public void An_independent_exclusive_handle_hides_even_a_folder_without_a_pending_marker()
    {
        StemCacheExpansion.Publish(Stage("a"), Destination, new string('a', 32));
        using (File.Open(Path.Combine(Destination, ".stemcache-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(Probe().Recovered);
            Assert.Throws<IOException>(() => StemCacheExpansion.Publish(Stage("b"), Destination, new string('b', 32)));
        }

        Assert.Equal(2, Probe().Recovered.Count);
    }

    private void CompleteWithPending()
    {
        StemCacheExpansion.Publish(Stage("a"), Destination, new string('a', 32));
        File.WriteAllText(Pending, new string('a', 32));
    }

    private StemProbe Probe() => StemFolderProbe.Probe(_root, _root, [], false, null);

    private List<StemTrack> Stage(string version)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "stage-" + version)).FullName;
        return [.. new[] { "bass", "guitar" }.Select(name =>
        {
            var path = Path.Combine(directory, name + ".wav");
            File.WriteAllText(path, version);
            return new StemTrack { Name = name, Path = path };
        })];
    }
}
