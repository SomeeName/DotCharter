using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// S1g. The `.dcstems` cache had no cap, no eviction and no way for the user to
/// see or reclaim it: `Delete(key)` was the only removal path and it was called
/// solely from the corrupt-archive branch, so a library user separating a few
/// hundred songs accumulated tens of GB with no visibility.
/// <para>
/// <b>The answer is NOT an automatic cap.</b> Stems are NEVER regenerated — the
/// subsystem's central invariant — so every archive is minutes of separation the
/// user already paid for on a song they may not even have open. Age/LRU eviction
/// would silently destroy exactly the artifact the invariant protects. What was
/// missing is visibility plus an explicit, informed user action, which is what
/// these two methods back.
/// </para>
/// </summary>
public sealed class StemCacheUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dc-stemusage-" + Guid.NewGuid().ToString("N"));

    public StemCacheUsageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private StemCacheStore Store() => new(rootOverride: _root);

    private void Archive(string token, int bytes) =>
        File.WriteAllBytes(Path.Combine(_root, token + StemArchive.ArchiveExtension), new byte[bytes]);

    [Fact]
    public void AnEmptyCacheMeasuresAsEmpty()
    {
        var usage = Store().MeasureUsage();
        Assert.Equal(0, usage.Songs);
        Assert.Equal(0, usage.Bytes);
    }

    [Fact]
    public void AMissingCacheDirectoryIsNotAnError()
    {
        var store = new StemCacheStore(rootOverride: Path.Combine(_root, "never-created"));
        Assert.Equal(StemCacheStore.StemCacheUsage.Empty, store.MeasureUsage());
        Assert.Equal(0, store.Clear());
    }

    [Fact]
    public void UsageCountsOneSongPerArchiveAndSumsTheirBytes()
    {
        Archive("aaa", 1000);
        Archive("bbb", 2500);
        // Not a cached song: a stray file never counts, and a FRESH staging dir
        // does not either — one that old could belong to a save running right now.
        // An ABANDONED one is different and does count; see
        // StemCacheStagingSweepTests.
        File.WriteAllBytes(Path.Combine(_root, "notes.txt"), new byte[9999]);
        Directory.CreateDirectory(Path.Combine(_root, ".staging-x"));

        var usage = Store().MeasureUsage();

        Assert.Equal(2, usage.Songs);
        Assert.Equal(3500, usage.Bytes);
    }

    [Fact]
    public void ClearDeletesEveryArchiveAndReportsHowMany()
    {
        Archive("aaa", 10);
        Archive("bbb", 20);
        Archive("ccc", 30);

        var store = Store();
        Assert.Equal(3, store.Clear());

        Assert.Equal(0, store.MeasureUsage().Songs);
        Assert.Empty(Directory.GetFiles(_root, "*" + StemArchive.ArchiveExtension));
    }

    /// <summary>Clear touches nothing but `.dcstems` — a neighbouring file in the
    /// cache root is not ours to delete.</summary>
    [Fact]
    public void ClearLeavesNonArchiveFilesAlone()
    {
        Archive("aaa", 10);
        var stray = Path.Combine(_root, "keep-me.json");
        File.WriteAllText(stray, "{}");

        Assert.Equal(1, Store().Clear());

        Assert.True(File.Exists(stray));
    }

    /// <summary>
    /// S1b. `StemCacheManifest.Key` is documented as "lets a reader confirm it
    /// opened the right archive" and no reader ever confirmed — everything
    /// rested on the 128-bit DirectoryToken filename, so a token collision, a
    /// hand-copied cache directory or an archive written under a degenerate key
    /// was served silently as the right song's stems.
    /// </summary>
    [Fact]
    public void AnArchiveRecordingADifferentKeyIsAMiss()
    {
        var store = Store();
        var key = StemCacheKey.Derive(StemCacheIdentity.ForLibrary("song-a"), new StemAudioSignature(1234, "abc"));
        var manifest = new StemCacheManifest
        {
            Key = "some:other|b=1|h=zzz",
            Stems = [new StemManifestEntry { Name = "drums", FileName = "drums.wav" }],
        };
        StemArchive.Write(store.ArchivePath(key), manifest, []);

        Assert.False(store.TryGetManifest(key, out _));
    }

    /// <summary>
    /// …but an archive whose recorded key MATCHES is still a hit, and so is a
    /// legacy archive that never wrote the field. Rejecting the latter would
    /// re-separate a song the cache already holds — the exact outcome the
    /// never-regenerate invariant exists to prevent, so this check is defence in
    /// depth, never a migration.
    /// </summary>
    [Fact]
    public void AMatchingOrAbsentKeyIsStillAHit()
    {
        var store = Store();
        var key = StemCacheKey.Derive(StemCacheIdentity.ForLibrary("song-b"), new StemAudioSignature(99, "def"));
        var stems = new List<StemManifestEntry> { new() { Name = "drums", FileName = "drums.wav" } };

        StemArchive.Write(store.ArchivePath(key), new StemCacheManifest { Key = key.Canonical, Stems = stems }, []);
        Assert.True(store.TryGetManifest(key, out var read));
        Assert.Equal(key.Canonical, read.Key);

        StemArchive.Write(store.ArchivePath(key), new StemCacheManifest { Key = "", Stems = stems }, []);
        Assert.True(store.TryGetManifest(key, out _));
    }

    /// <summary>
    /// Best-effort per file: an archive held open (a song whose stems are being
    /// expanded right now) is skipped and left for the next attempt rather than
    /// failing the whole clear and stranding the rest.
    /// </summary>
    [Fact]
    public void AnArchiveHeldOpenIsSkippedWithoutFailingTheRest()
    {
        Archive("locked", 10);
        Archive("free1", 10);
        Archive("free2", 10);

        using (var _ = new FileStream(Path.Combine(_root, "locked" + StemArchive.ArchiveExtension),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(2, Store().Clear());
        }

        Assert.Equal(1, Store().MeasureUsage().Songs);
    }
}
