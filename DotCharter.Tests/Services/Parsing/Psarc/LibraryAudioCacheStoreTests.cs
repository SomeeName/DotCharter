using DotCharter.Services.Library.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     The disk half of the PSARC audio cache: naming, the last-use marker, the
///     reachability rules the pure planner is fed, and the measure/clear/sweep
///     verbs the Settings row drives.
///     <para>
///         These run against a private temp root, never
///         <c>%LocalAppData%/DotCharter/PsarcAudioCache</c> — that folder is the
///         owner's data and 127 GB of it.
///     </para>
/// </summary>
public sealed class LibraryAudioCacheStoreTests : IDisposable
{
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), "dc-psarc-cache-" + Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(_sandbox, "cache");
    private string Archives => Path.Combine(_sandbox, "archives");

    public LibraryAudioCacheStoreTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Archives);
    }

    public void Dispose() => BestEffortDelete.Directory(_sandbox);

    private LibraryAudioCacheStore NewStore(long maxBytes = LibraryAudioCacheStore.DefaultMaxBytes) =>
        new(rootOverride: Root, maxBytes: maxBytes);

    /// <summary>
    ///     The destructive default, pinned in both directions — this one is not
    ///     hypothetical caution. A package library source falls back to a store
    ///     over the REAL <c>%LocalAppData%</c> cache when nobody hands it one, and
    ///     <c>RocksmithPackageBuilderTests</c> builds a <c>goldentest_p.psarc</c> in
    ///     a temp folder and loads it through exactly that constructor. With
    ///     automatic sweeping on by default, one run of the gate pruned the owner's
    ///     live 127.6 GB cache down to the cap. Only the composition root opts in.
    /// </summary>
    [Fact]
    public void A_store_nobody_opted_in_measures_and_clears_but_never_prunes_on_its_own()
    {
        Fill(Path.Combine(Root, "Huge_ABCDEF0123456789_1"), "audio.wav", 4096);
        var store = NewStore(maxBytes: 1);

        Assert.False(store.AutomaticSweepEnabled);
        store.ScheduleSweep();
        Assert.Equal(1, store.MeasureUsage().Songs);

        // …while an EXPLICIT sweep is an explicit decision and still works.
        store.Sweep();
        Assert.Equal(0, store.MeasureUsage().Songs);
    }

    [Fact]
    public void An_opted_in_store_does_prune_from_ScheduleSweep()
    {
        // Proves the flag is a real switch rather than a permanent off.
        Fill(Path.Combine(Root, "Huge_ABCDEF0123456789_1"), "audio.wav", 4096);
        var store = new LibraryAudioCacheStore(rootOverride: Root, maxBytes: 1, enableAutomaticSweep: true);

        Assert.True(store.AutomaticSweepEnabled);
        // Synchronous path, so the assertion below cannot race the background task.
        store.Sweep();
        Assert.Equal(0, store.MeasureUsage().Songs);
    }

    private string NewArchive(string name, int bytes = 16)
    {
        var path = Path.Combine(Archives, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Fill(string directory, string fileName, int bytes)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), new byte[bytes]);
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The scheme is byte-for-byte what the package library source used
    ///     before the store existed, because the owner has 2,543 folders already
    ///     on disk: change it and every one of them misses and is re-decoded.
    /// </summary>
    [Fact]
    public void The_folder_name_is_still_archiveName_pathHash16_lastWriteTicks()
    {
        var archive = NewArchive("Some_Song_v2.psarc");
        var name = LibraryAudioCacheStore.DirectoryNameFor(archive);

        Assert.True(LibraryAudioCacheStore.TryParseName(name, out var hash, out var ticks));
        Assert.Equal(16, hash.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(archive).Ticks, ticks);
        Assert.StartsWith("Some_Song_v2_", name, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_that_is_not_there_gets_an_unknown_timestamp_and_does_not_parse_as_a_revision()
    {
        var name = LibraryAudioCacheStore.DirectoryNameFor(Path.Combine(Archives, "gone.psarc"));
        Assert.EndsWith("_unknown", name, StringComparison.Ordinal);
        // "unknown" folders must not group with each other as revisions of one
        // archive — they are not revisions of anything.
        Assert.False(LibraryAudioCacheStore.TryParseName(name, out _, out _));
    }

    [Fact]
    public void Touching_an_archive_changes_the_folder_it_maps_to()
    {
        // This IS the orphaning defect, stated as a fact: the last-write ticks
        // are in the key, so an updated or re-downloaded archive abandons its old
        // folder permanently.
        var archive = NewArchive("Updated.psarc");
        var before = LibraryAudioCacheStore.DirectoryNameFor(archive);
        File.SetLastWriteTimeUtc(archive, File.GetLastWriteTimeUtc(archive).AddDays(1));

        Assert.NotEqual(before, LibraryAudioCacheStore.DirectoryNameFor(archive));
    }

    [Theory]
    [InlineData("name_ABCDEF0123456789_637000000000000", true)]
    [InlineData("a_b_c_ABCDEF0123456789_1", true)]      // underscores in the archive name
    [InlineData("name_ABCDEF0123456789_unknown", false)] // un-statable archive
    [InlineData("name_TOOSHORT_12345", false)]           // hash is not 16 chars
    [InlineData("name_ZZZZZZZZZZZZZZZZ_12345", false)]   // hash is not hex
    [InlineData("nounderscores", false)]
    public void The_name_is_parsed_from_the_right_because_archive_names_contain_underscores(
        string name, bool parses) => Assert.Equal(parses, LibraryAudioCacheStore.TryParseName(name, out _, out _));

    // ── The last-use marker ───────────────────────────────────────────────────

    [Fact]
    public void PrepareForUse_creates_the_folder_and_records_which_archive_it_came_from()
    {
        var archive = NewArchive("Marked.psarc");
        var directory = NewStore().PrepareForUse(archive);

        Assert.True(Directory.Exists(directory));
        var marker = Path.Combine(directory, LibraryAudioCacheStore.SourceMarkerFileName);
        Assert.True(File.Exists(marker));
        Assert.Equal(Path.GetFullPath(archive), File.ReadAllText(marker));
    }

    /// <summary>
    ///     The marker is rewritten on every use because Windows ships with
    ///     last-ACCESS-time updates disabled, so a cache HIT leaves no trace in
    ///     the filesystem's own timestamps. Without this the LRU would rank by
    ///     when a song was first decoded and evict exactly the songs replayed most.
    /// </summary>
    [Fact]
    public void A_repeat_use_moves_the_last_use_stamp_forward()
    {
        var archive = NewArchive("Replayed.psarc");
        var store = NewStore();
        var directory = store.PrepareForUse(archive);
        var marker = Path.Combine(directory, LibraryAudioCacheStore.SourceMarkerFileName);
        var stale = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(marker, stale);

        store.PrepareForUse(archive);

        Assert.True(File.GetLastWriteTimeUtc(marker) > stale);
        Assert.True(store.ScanEntries().Single().LastUsedUtc > stale);
    }

    // ── Reachability ──────────────────────────────────────────────────────────

    [Fact]
    public void A_folder_whose_archive_is_still_there_unchanged_is_reachable()
    {
        var store = NewStore();
        store.PrepareForUse(NewArchive("Live.psarc"));

        Assert.Equal(LibraryAudioCacheReachability.Reachable, store.ScanEntries().Single().Reachability);
    }

    [Fact]
    public void A_folder_whose_archive_was_deleted_is_an_orphan()
    {
        var archive = NewArchive("Deleted.psarc");
        var store = NewStore();
        store.PrepareForUse(archive);
        File.Delete(archive);

        Assert.Equal(LibraryAudioCacheReachability.Orphaned, store.ScanEntries().Single().Reachability);
    }

    [Fact]
    public void A_folder_whose_archive_was_updated_is_an_orphan_even_though_the_file_still_exists()
    {
        var archive = NewArchive("Rebuilt.psarc");
        var store = NewStore();
        store.PrepareForUse(archive);
        File.SetLastWriteTimeUtc(archive, File.GetLastWriteTimeUtc(archive).AddHours(3));

        Assert.Equal(LibraryAudioCacheReachability.Orphaned, store.ScanEntries().Single().Reachability);
    }

    [Fact]
    public void A_folder_with_no_marker_is_unknown_rather_than_an_orphan()
    {
        // Every folder written before 0.58 looks like this. "Cannot be proven
        // dead" must not read as "is dead" — the cap is what bounds these.
        Fill(Path.Combine(Root, "Legacy_ABCDEF0123456789_637000000000000"), "audio.wem", 32);

        Assert.Equal(LibraryAudioCacheReachability.Unknown, NewStore().ScanEntries().Single().Reachability);
    }

    /// <summary>
    ///     The only orphan signal that reaches marker-less legacy folders: two
    ///     folders sharing a path hash are two revisions of ONE archive, and an
    ///     archive has exactly one current last-write time, so at most one of them
    ///     can ever be named again.
    /// </summary>
    [Fact]
    public void An_older_revision_of_the_same_archive_is_an_orphan_with_no_marker_at_all()
    {
        Fill(Path.Combine(Root, "Song_ABCDEF0123456789_100"), "audio.wem", 8);
        Fill(Path.Combine(Root, "Song_ABCDEF0123456789_200"), "audio.wem", 8);
        Fill(Path.Combine(Root, "Other_1234567890ABCDEF_100"), "audio.wem", 8);

        var byName = NewStore().ScanEntries().ToDictionary(e => e.Name, e => e.Reachability);

        Assert.Equal(LibraryAudioCacheReachability.Orphaned, byName["Song_ABCDEF0123456789_100"]);
        Assert.Equal(LibraryAudioCacheReachability.Unknown, byName["Song_ABCDEF0123456789_200"]);
        // A lone folder is nobody's older revision.
        Assert.Equal(LibraryAudioCacheReachability.Unknown, byName["Other_1234567890ABCDEF_100"]);
    }

    [Fact]
    public void A_marker_that_proves_a_folder_live_beats_the_supersession_heuristic()
    {
        // An archive restored from a backup can carry an OLDER mtime than a
        // revision already cached. Its marker says it is live; that wins.
        var archive = NewArchive("Restored.psarc");
        var store = NewStore();
        var live = Path.GetFileName(store.PrepareForUse(archive));
        LibraryAudioCacheStore.TryParseName(live, out var hash, out var ticks);
        Fill(Path.Combine(Root, $"Restored_{hash}_{ticks + 1000}"), "audio.wem", 8);

        var byName = store.ScanEntries().ToDictionary(e => e.Name, e => e.Reachability);

        Assert.Equal(LibraryAudioCacheReachability.Reachable, byName[live]);
    }

    // ── Measure / clear / sweep ───────────────────────────────────────────────

    [Fact]
    public void MeasureUsage_counts_folders_and_every_byte_beneath_them()
    {
        // Stems are expanded into <folder>/stems, so a nested walk is the only
        // honest number — and those WAVs are the biggest thing in here.
        Fill(Path.Combine(Root, "A_ABCDEF0123456789_1"), "audio.wem", 100);
        Fill(Path.Combine(Root, "A_ABCDEF0123456789_1", "stems"), "bass.wav", 400);
        Fill(Path.Combine(Root, "B_1234567890ABCDEF_1"), "audio.wem", 50);

        var usage = NewStore().MeasureUsage();

        Assert.Equal(2, usage.Songs);
        Assert.Equal(550, usage.Bytes);
    }

    [Fact]
    public void MeasureUsage_on_a_cache_that_was_never_created_is_empty_rather_than_a_throw()
    {
        var store = new LibraryAudioCacheStore(rootOverride: Path.Combine(_sandbox, "never-made"));
        Assert.Equal(LibraryAudioCacheUsage.Empty, store.MeasureUsage());
        Assert.Equal(0, store.Clear());
        Assert.True(store.Sweep().IsEmpty);
    }

    [Fact]
    public void Clear_removes_every_folder_and_reports_how_many_went()
    {
        Fill(Path.Combine(Root, "A_ABCDEF0123456789_1"), "audio.wem", 10);
        Fill(Path.Combine(Root, "B_1234567890ABCDEF_1"), "audio.wem", 10);
        var store = NewStore();

        Assert.Equal(2, store.Clear());
        Assert.Equal(LibraryAudioCacheUsage.Empty, store.MeasureUsage());
    }

    [Fact]
    public void Clear_skips_a_folder_whose_audio_is_still_open_instead_of_failing_the_whole_clear()
    {
        // Same best-effort-per-item contract as StemCacheStore.Clear: a song
        // being played right now must not turn the button into an error.
        var busy = Path.Combine(Root, "Busy_ABCDEF0123456789_1");
        Fill(busy, "audio.wav", 10);
        Fill(Path.Combine(Root, "Free_1234567890ABCDEF_1"), "audio.wem", 10);

        var store = NewStore();
        using (File.Open(Path.Combine(busy, "audio.wav"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(1, store.Clear());
        }

        Assert.True(Directory.Exists(busy));
        Assert.Equal(1, store.MeasureUsage().Songs);
    }

    [Fact]
    public void Sweep_reclaims_an_orphan_and_leaves_the_live_folder_alone()
    {
        var live = NewArchive("Live.psarc");
        var doomed = NewArchive("Doomed.psarc");
        var store = NewStore();
        var liveDir = store.PrepareForUse(live);
        var doomedDir = store.PrepareForUse(doomed);
        File.Delete(doomed);

        var plan = store.Sweep();

        Assert.Equal([Path.GetFileName(doomedDir)], plan.Orphans.Select(e => e.Name));
        Assert.False(Directory.Exists(doomedDir));
        Assert.True(Directory.Exists(liveDir));
    }

    [Fact]
    public void Sweep_brings_an_over_cap_cache_back_under_the_cap_oldest_first()
    {
        Fill(Path.Combine(Root, "Old_ABCDEF0123456789_1"), "audio.wem", 400);
        Fill(Path.Combine(Root, "New_1234567890ABCDEF_1"), "audio.wem", 400);
        // A marker-less folder's last-use stamp IS its directory write time —
        // which is what every folder written before 0.58 has to be ranked by.
        Directory.SetLastWriteTimeUtc(Path.Combine(Root, "Old_ABCDEF0123456789_1"),
            new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(Path.Combine(Root, "New_1234567890ABCDEF_1"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var store = NewStore(maxBytes: 500);
        var plan = store.Sweep();

        Assert.Equal(["Old_ABCDEF0123456789_1"], plan.Evicted.Select(e => e.Name));
        Assert.Equal(400, store.MeasureUsage().Bytes);
    }

    [Fact]
    public void Sweep_never_deletes_the_folder_of_the_song_being_loaded()
    {
        var archive = NewArchive("Loading.psarc");
        var store = NewStore(maxBytes: 0);
        var directory = store.PrepareForUse(archive);
        File.WriteAllBytes(Path.Combine(directory, "audio.wem"), new byte[1024]);

        store.Sweep(Path.GetFileName(directory));

        Assert.True(Directory.Exists(directory));
        Assert.Equal(1, store.MeasureUsage().Songs);
    }
}
