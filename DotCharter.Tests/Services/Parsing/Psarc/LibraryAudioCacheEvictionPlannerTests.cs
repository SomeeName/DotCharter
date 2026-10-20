using DotCharter.Services.Library.Cache;

namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     The eviction POLICY, with no filesystem and no clock anywhere near it:
///     folder names, sizes and last-use stamps in, a delete list out.
///     <para>
///         The defect these pin is that nothing ever removed a byte of
///         <c>%LocalAppData%/DotCharter/PsarcAudioCache</c> — 127.6 GB across
///         2,543 folders / 7,762 files on the owner's machine. It is not key
///         churn (only ~5 duplicate folders existed): it is every song ever
///         opened, kept forever. So the two things worth proving are that the
///         cache comes back under its cap AT ALL, and that the folders which can
///         never be hit again go before any folder that still can.
///     </para>
/// </summary>
public sealed class LibraryAudioCacheEvictionPlannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LibraryAudioCacheEntry Entry(
        string name,
        long bytes,
        int minutesOld,
        LibraryAudioCacheReachability reach = LibraryAudioCacheReachability.Reachable) =>
        new(name, bytes, T0.AddMinutes(-minutesOld), reach);

    private static readonly LibraryAudioCacheEvictionPlanner Planner = new();

    [Fact]
    public void Nothing_is_evicted_while_the_cache_is_under_its_cap()
    {
        var plan = Planner.Plan(
            [Entry("a", 10, 5), Entry("b", 10, 1)],
            capBytes: 100);

        Assert.True(plan.IsEmpty);
        Assert.Equal(20, plan.BytesBefore);
        Assert.Equal(20, plan.BytesAfter);
        Assert.Equal(0, plan.BytesReclaimed);
    }

    [Fact]
    public void An_empty_cache_plans_nothing()
    {
        var plan = Planner.Plan([], capBytes: 0);
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.BytesBefore);
    }

    /// <summary>
    ///     An orphan is a folder whose archive is gone, moved or was
    ///     re-downloaded — the key embeds the archive's last-write ticks, so
    ///     nothing will ever compute that folder's name again. Keeping one has no
    ///     upside at any cache size, so it goes even on a cache that is nowhere
    ///     near its cap.
    /// </summary>
    [Fact]
    public void Orphans_are_reclaimed_even_when_the_cache_is_far_under_its_cap()
    {
        var plan = Planner.Plan(
            [
                Entry("live", 10, 100),
                Entry("dead", 30, 1, LibraryAudioCacheReachability.Orphaned)
            ],
            capBytes: 1_000_000);

        Assert.Equal(["dead"], plan.Orphans.Select(e => e.Name));
        Assert.Empty(plan.Evicted);
        // "live" is the LEAST recently used of the two and survives anyway: the
        // orphan pass is not an LRU pass.
        Assert.Equal(40, plan.BytesBefore);
        Assert.Equal(10, plan.BytesAfter);
    }

    [Fact]
    public void Orphans_are_reclaimed_before_anything_still_reachable_is_touched()
    {
        // 300 bytes total, cap 150. The orphan alone gets there, so no live
        // folder may be given up — even though two live folders are older.
        var plan = Planner.Plan(
            [
                Entry("ancient", 50, 900),
                Entry("old", 50, 800),
                Entry("orphan", 200, 1, LibraryAudioCacheReachability.Orphaned)
            ],
            capBytes: 150);

        Assert.Equal(["orphan"], plan.Orphans.Select(e => e.Name));
        Assert.Empty(plan.Evicted);
        Assert.Equal(100, plan.BytesAfter);
    }

    [Fact]
    public void Over_the_cap_the_least_recently_used_go_first_and_only_until_it_fits()
    {
        // 40 bytes, cap 25 => must free 15. Oldest first: oldest(10) then
        // middle(10) reaches 20 <= 25, so "newest" and "newer" survive.
        var plan = Planner.Plan(
            [
                Entry("newest", 10, 1),
                Entry("oldest", 10, 400),
                Entry("newer", 10, 10),
                Entry("middle", 10, 100)
            ],
            capBytes: 25);

        Assert.Empty(plan.Orphans);
        Assert.Equal(["oldest", "middle"], plan.Evicted.Select(e => e.Name));
        Assert.Equal(20, plan.BytesAfter);
        Assert.Equal(20, plan.BytesReclaimed);
    }

    /// <summary>
    ///     A folder with no source marker (everything written before the store
    ///     existed — i.e. all 2,543 of the owner's) cannot be PROVEN dead, so it
    ///     is never orphan-reclaimed. It is still ordinary LRU material: without
    ///     this the legacy folders would have been un-evictable and the cap would
    ///     have bounded nothing on the one machine that needed it.
    /// </summary>
    [Fact]
    public void Unknown_folders_are_never_orphaned_but_are_normal_lru_candidates()
    {
        var plan = Planner.Plan(
            [
                Entry("legacy", 100, 500, LibraryAudioCacheReachability.Unknown),
                Entry("fresh", 10, 1)
            ],
            capBytes: 50);

        Assert.Empty(plan.Orphans);
        Assert.Equal(["legacy"], plan.Evicted.Select(e => e.Name));
    }

    /// <summary>
    ///     The folder for the song being loaded right now. Deleting it mid-load is
    ///     the one way a background sweep could break a running app, and the size
    ///     pass would otherwise pick it: on a cold cache it can be the only
    ///     candidate there is.
    /// </summary>
    [Fact]
    public void The_folder_in_use_is_never_evicted_even_when_it_is_the_only_candidate()
    {
        var plan = Planner.Plan(
            [Entry("loading-now", 500, 0)],
            capBytes: 10,
            protectedName: "loading-now");

        Assert.True(plan.IsEmpty);
        Assert.Equal(500, plan.BytesAfter); // still over cap, and that is correct
    }

    [Fact]
    public void The_folder_in_use_is_not_reclaimed_as_an_orphan_either()
    {
        // Its archive genuinely looks gone (a psarc opened off a drive that
        // dropped out mid-load). Deleting the folder underneath the extraction
        // that is writing into it is still the worse outcome.
        var plan = Planner.Plan(
            [Entry("loading-now", 500, 0, LibraryAudioCacheReachability.Orphaned)],
            capBytes: 1_000_000,
            protectedName: "loading-now");

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Folders_with_identical_last_use_stamps_are_ordered_by_name_so_a_sweep_is_deterministic()
    {
        // A batch import writes many folders inside one filesystem timestamp
        // tick; a plan that depended on enumeration order would be untestable.
        var entries = new[] { Entry("cc", 10, 7), Entry("aa", 10, 7), Entry("bb", 10, 7) };
        var forward = Planner.Plan(entries, capBytes: 10);
        var reversed = Planner.Plan([.. entries.Reverse()], capBytes: 10);

        Assert.Equal(["aa", "bb"], forward.Evicted.Select(e => e.Name));
        Assert.Equal(forward.Evicted.Select(e => e.Name), reversed.Evicted.Select(e => e.Name));
    }

    [Fact]
    public void The_applied_order_is_orphans_first_then_the_lru_victims()
    {
        var plan = Planner.Plan(
            [
                Entry("live-old", 100, 900),
                Entry("dead", 100, 1, LibraryAudioCacheReachability.Orphaned)
            ],
            capBytes: 50);

        Assert.Equal(["dead", "live-old"], plan.All.Select(e => e.Name));
        Assert.Equal(2, plan.Count);
        Assert.Equal(200, plan.BytesReclaimed);
    }

    [Fact]
    public void A_negative_cap_is_clamped_rather_than_treated_as_unlimited()
    {
        var plan = Planner.Plan([Entry("a", 10, 1)], capBytes: -1);
        Assert.Equal(["a"], plan.Evicted.Select(e => e.Name));
        Assert.Equal(0, plan.BytesAfter);
    }

    /// <summary>
    ///     The shipped default, stated as a number so a silent change to it fails
    ///     here rather than on somebody's disk. 8 GiB is ~160 songs at the ~50 MB
    ///     per decoded song measured on the machine that reached 127.6 GB.
    /// </summary>
    [Fact]
    public void The_default_cap_is_eight_gibibytes()
    {
        Assert.Equal(8L * 1024 * 1024 * 1024, LibraryAudioCacheStore.DefaultMaxBytes);
        Assert.Equal(LibraryAudioCacheStore.DefaultMaxBytes, new LibraryAudioCacheStore(rootOverride: "x").MaxBytes);
    }
}
