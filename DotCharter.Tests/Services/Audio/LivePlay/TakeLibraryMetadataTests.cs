using DotCharter.Services.Audio.LivePlay.Takes;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The take sweep's policy over the v0.27 take-library metadata: starred
///     takes and their re-amp renders are cap-eviction exempt, and orphaned
///     re-amp renders sweep like orphaned takes. The library UI that wrote the
///     star and the renders (History screen) was removed on 2026-09-19; the
///     policy stays because stores written before then still carry both, and
///     the player's keepers must not become cache fodder on upgrade.
/// </summary>
public sealed class TakeLibraryMetadataTests
{
    private static LiveTakeStore.TakeFileInfo File_(string name, long size, int ageDays) =>
        new(name, size, new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OrphanedReampRenders_AreSweptLikeOrphanedTakes()
    {
        var files = new[]
        {
            File_("take-1.wav", 100, 1),
            File_("take-1-reamp-clean.wav", 100, 1), // referenced render
            File_("take-9-reamp-clean.wav", 100, 9), // its take was detached
            File_("take-1-reamp-x.wav.tmp", 100, 1)  // interrupted write
        };

        var deletions = LiveTakeStore.SelectDeletions(files,
            Set("take-1.wav", "take-1-reamp-clean.wav"), maxTotalBytes: 10_000);

        Assert.Equal(Set("take-9-reamp-clean.wav", "take-1-reamp-x.wav.tmp"), Set([.. deletions]));
    }

    [Fact]
    public void StarredFiles_SurviveTheCapEviction_OldestUnpinnedGoesInstead()
    {
        var files = new[]
        {
            File_("starred-old.wav", 500, 30),
            File_("starred-old-reamp-clean.wav", 500, 30),
            File_("plain-older.wav", 500, 20),
            File_("plain-newer.wav", 500, 1)
        };
        var referenced = Set("starred-old.wav", "starred-old-reamp-clean.wav",
            "plain-older.wav", "plain-newer.wav");
        var pinned = Set("starred-old.wav", "starred-old-reamp-clean.wav");

        var deletions = LiveTakeStore.SelectDeletions(files, referenced, maxTotalBytes: 1500, pinned);

        // Without pinning the two OLDEST (the starred pair) would go; with it
        // the eviction skips them and takes the oldest unpinned instead.
        Assert.Equal(["plain-older.wav"], deletions);
    }

    [Fact]
    public void AnAllStarredStore_MayStayOverTheCap()
    {
        var files = new[] { File_("a.wav", 800, 2), File_("b.wav", 800, 1) };
        var referenced = Set("a.wav", "b.wav");

        Assert.Empty(LiveTakeStore.SelectDeletions(files, referenced, maxTotalBytes: 1000, referenced));
    }
}
