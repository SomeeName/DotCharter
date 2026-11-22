using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

public sealed class RocksmithTestPublicationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-test-publication-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_before_publication_keeps_installed_packages(bool cancel)
    {
        var path = Path.Combine(_root, "song_p.psarc");
        File.WriteAllText(path, "previous package");
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var build = cancel
            ? Task.FromCanceled<RocksmithBuildResult>(cancellation.Token)
            : Task.FromException<RocksmithBuildResult>(new InvalidDataException("encoding failed"));

        await Assert.ThrowsAnyAsync<Exception>(() => RocksmithTestExport.BuildAndCleanUpAsync(
            () => build, new ExportTargetWriter(), [path]));

        Assert.Equal("previous package", File.ReadAllText(path));
    }

    [Fact]
    public async Task Without_ownership_context_successful_publication_retains_superseded_packages()
    {
        var target = Path.Combine(_root, "song_p.psarc");
        var oldVersion = Path.Combine(_root, "song_v2_p.psarc");
        File.WriteAllText(target, "previous package");
        File.WriteAllText(oldVersion, "old version");

        await RocksmithTestExport.BuildAndCleanUpAsync(() =>
        {
            Assert.Equal("old version", File.ReadAllText(oldVersion));
            AtomicFile.WriteAllText(target, "complete replacement");
            return Task.FromResult(new RocksmithBuildResult(target, "song", [], []));
        }, new ExportTargetWriter(), [Path.Combine(_root, ".", "song_p.psarc"), oldVersion]);

        Assert.Equal("complete replacement", File.ReadAllText(target));
        Assert.Equal("old version", File.ReadAllText(oldVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_package_set_is_not_published_until_every_stage_is_complete(bool cancel)
    {
        var pc = Path.Combine(_root, "song_p.psarc");
        var mac = Path.Combine(_root, "song_m.psarc");
        File.WriteAllText(pc, "old pc");
        File.WriteAllText(mac, "old mac");
        using (var publication = new PackagePublication())
        {
            File.WriteAllText(publication.Stage(pc), "new pc");
            File.WriteAllText(publication.Stage(mac), "incomplete mac");
            if (cancel)
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => publication.Commit(cancellation.Token));
            }
        }

        Assert.Equal("old pc", File.ReadAllText(pc));
        Assert.Equal("old mac", File.ReadAllText(mac));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void Failure_publishing_the_second_package_restores_the_first()
    {
        var pc = Path.Combine(_root, "song_p.psarc");
        var mac = Path.Combine(_root, "song_m.psarc");
        File.WriteAllText(pc, "old pc");
        File.WriteAllText(mac, "old mac");
        using (var publication = new PackagePublication((stage, target) =>
        {
            if (target == mac) throw new IOException("publication refused");
            AtomicFile.Publish(stage, target);
        }))
        {
            File.WriteAllText(publication.Stage(pc), "new pc");
            File.WriteAllText(publication.Stage(mac), "new mac");
            Assert.Throws<IOException>(() => publication.Commit(TestContext.Current.CancellationToken));
        }

        Assert.Equal("old pc", File.ReadAllText(pc));
        Assert.Equal("old mac", File.ReadAllText(mac));
    }

    [Fact]
    public void A_refused_rollback_retains_the_original_packages_in_backup_files()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows file locking.");
        var pc = Path.Combine(_root, "song_p.psarc");
        var mac = Path.Combine(_root, "song_m.psarc");
        File.WriteAllText(pc, "old pc");
        File.WriteAllText(mac, "old mac");
        FileStream? held = null;
        try
        {
            using (var publication = new PackagePublication((stage, target) =>
            {
                if (target == mac)
                {
                    held = new FileStream(pc, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("publication refused");
                }
                AtomicFile.Publish(stage, target);
            }))
            {
                File.WriteAllText(publication.Stage(pc), "new pc");
                File.WriteAllText(publication.Stage(mac), "new mac");
                Assert.Throws<AggregateException>(() => publication.Commit(TestContext.Current.CancellationToken));
            }

            var backups = Directory.GetFiles(_root, "*.previous").Select(File.ReadAllText).ToArray();
            Assert.Contains("old pc", backups);
            Assert.Contains("old mac", backups);
        }
        finally
        {
            held?.Dispose();
        }
    }
}
