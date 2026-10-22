using System.Collections.Concurrent;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Library.Prefetch;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     The neutral library (#886) over FAKE package sources: the scan walks every
///     registered source's extensions and nothing else, serves an unchanged file from the
///     index without asking its source again, folds byte-copies with
///     <see cref="LibraryDedupe" /> across sources, and hands every per-song question to
///     the source that owns the file. No archive format is read here.
/// </summary>
public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_library_service_").FullName;
    private readonly FakeAppLogger _logger = new();

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Library => Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;

    [Fact]
    public async Task A_scan_reads_each_file_through_the_source_that_owns_its_extension_and_skips_the_rest()
    {
        var archives = new FakeSource(".psarc");
        var packages = new FakeSource(".dcsong");
        var a = Touch("b/zeta.psarc");
        var b = Touch("alpha.dcsong");
        Touch("notes.txt");
        Touch("cover.png");
        archives.Rows[a] = [Row(a, "Zeta", "Zed")];
        packages.Rows[b] = [Row(b, "Alpha", "Abba")];
        var service = new LibraryService(_logger, [archives, packages]);

        var songs = await service.ScanAsync(Library);

        // Sorted by artist, then title, whichever source read the row.
        Assert.Equal(["Alpha", "Zeta"], songs.Select(s => s.SongKey));
        Assert.Equal([a], archives.Read);
        Assert.Equal([b], packages.Read);
        Assert.Equal(1, archives.ScansBegun);
        Assert.Equal(1, packages.ScansBegun);
    }

    [Fact]
    public async Task A_second_scan_serves_unchanged_files_from_the_index_without_asking_their_source()
    {
        var archives = new FakeSource(".psarc");
        var packages = new FakeSource(".dcsong");
        var a = Touch("one.psarc");
        var b = Touch("two.dcsong");
        archives.Rows[a] = [Row(a, "One", "Artist")];
        packages.Rows[b] = [Row(b, "Two", "Artist")];
        var index = new SqliteLibraryIndex(_logger, Path.Combine(_dir, "library-index.db"));

        var cold = await new LibraryService(_logger, [archives, packages], index).ScanAsync(Library);
        var warm = await new LibraryService(_logger, [archives, packages], index).ScanAsync(Library);

        Assert.Equal(cold.Select(s => (s.SongKey, s.PackagePath)), warm.Select(s => (s.SongKey, s.PackagePath)));
        Assert.Equal([a], archives.Read);
        Assert.Equal([b], packages.Read);

        // A changed file is read again — by its own source only.
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5));
        await new LibraryService(_logger, [archives, packages], index).ScanAsync(Library);
        Assert.Equal([a], archives.Read);
        Assert.Equal([b, b], packages.Read);
    }

    [Fact]
    public async Task Copies_of_one_song_fold_to_one_row_across_files_and_a_package_of_another_kind_stays_its_own_row()
    {
        var archives = new FakeSource(".psarc");
        var packages = new FakeSource(".dcsong");
        var copyB = Touch("b/song_p.psarc");
        var copyA = Touch("a/song_p.psarc");
        var package = Touch("song.dcsong");
        var other = Touch("other_p.psarc");
        archives.Rows[copyB] = [Row(copyB, "Song", "Band")];
        archives.Rows[copyA] = [Row(copyA, "Song", "Band")];
        archives.Rows[other] = [Row(other, "Other", "Band")];
        packages.Rows[package] = [Row(package, "Song", "Band")];
        var service = new LibraryService(_logger, [archives, packages]);

        var songs = await service.ScanAsync(Library);
        var listed = LibraryDedupe.CollapseByIdentity(songs);

        Assert.Equal(4, songs.Count);
        // The byte-copies fold to the smallest path; the .dcsong of the same identity is
        // another kind of file (LibraryDedupe keys on the source kind) and stays.
        Assert.Equal(3, listed.Count);
        Assert.Single(listed, s => s.SongKey == "Song" && s.PackagePath == copyA);
        Assert.Single(listed, s => s.SongKey == "Song" && s.PackagePath == package);
        Assert.DoesNotContain(listed, s => s.PackagePath == copyB);
    }

    [Fact]
    public async Task A_source_that_throws_on_one_file_costs_that_file_only()
    {
        var archives = new FakeSource(".psarc");
        var packages = new FakeSource(".dcsong");
        var bad = Touch("bad.dcsong");
        var good = Touch("good.psarc");
        archives.Rows[good] = [Row(good, "Good", "Band")];
        packages.Fails.Add(bad);

        var songs = await new LibraryService(_logger, [archives, packages]).ScanAsync(Library);

        Assert.Equal(["Good"], songs.Select(s => s.SongKey));
        Assert.Contains(_logger.Entries, e => e.Level == "WARN" && e.Message.Contains(bad, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_a_scan_ends_a_blocked_package_read_promptly_and_reports_no_partial_row()
    {
        var packages = new FakeSource(".dcsong");
        var slow = Touch("slow.dcsong");
        packages.Rows[slow] = [Row(slow, "Slow", "Band")];
        packages.Blocks.Add(slow);
        var found = new List<LibrarySongEntry>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scan = Task.Run(() => new LibraryService(_logger, [packages]) { ScanParallelismForTests = 1 }
            .ScanAsync(Library, null, new Progress<IReadOnlyList<LibrarySongEntry>>(found.AddRange), cts.Token),
            TestContext.Current.CancellationToken);
        Assert.True(packages.Entered.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        cts.Cancel();
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Same(scan, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Every_per_song_question_reaches_the_source_that_owns_the_file()
    {
        var archives = new FakeSource(".psarc");
        var packages = new FakeSource(".dcsong");
        var service = new LibraryService(_logger, [archives, packages]);
        var archiveSong = Row(Path.Combine(_dir, "x.PSARC"), "X", "Band");
        var packageSong = Row(Path.Combine(_dir, "y.dcsong"), "Y", "Band");
        var stray = Row(Path.Combine(_dir, "z.zip"), "Z", "Band");

        Assert.Same(archives.Loaded, (await service.LoadSongAsync(archiveSong)).Project);
        Assert.Same(packages.Loaded, (await service.LoadSongAsync(packageSong)).Project);
        Assert.Equal(LibraryAudioPrepareOutcome.Prepared, service.PrepareAudio(packageSong, CancellationToken.None));
        Assert.Equal(LibraryAudioPrepareOutcome.AlreadyPrepared, service.PrepareAudio(archiveSong, CancellationToken.None));
        Assert.Equal(LibrarySongBlock.AudioDecoderMissing, service.BlockFor(packageSong));
        Assert.Equal(LibrarySongBlock.None, service.BlockFor(archiveSong));
        Assert.Equal(".dcsong-cover", await service.LoadCoverImageAsync(packageSong));
        Assert.Equal(".psarc-cover", await service.LoadCoverImageAsync(archiveSong));
        Assert.Equal(AudioAvailability.Present, service.Probe(archiveSong.PackagePath).Audio);
        Assert.Equal(AudioAvailability.Missing, service.Probe(packageSong.PackagePath).Audio);

        // A file no source reads is refused, never guessed at.
        await Assert.ThrowsAsync<NotSupportedException>(() => service.LoadSongAsync(stray));
        Assert.Equal(LibraryAudioPrepareOutcome.NoAudio, service.PrepareAudio(stray, CancellationToken.None));
        Assert.Equal(LibrarySongBlock.None, service.BlockFor(stray));
        Assert.False(service.Probe(stray.PackagePath).Readable);
        Assert.Equal(stray.CoverImagePath, await service.LoadCoverImageAsync(stray));
    }

    private string Touch(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Library, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static LibrarySongEntry Row(string path, string key, string artist) => new()
    {
        SongKey = key,
        Title = key,
        Artist = artist,
        Charter = "Charter",
        Version = "1",
        PackagePath = path,
        CoverImageEntryPath = "cover",
        CoverImagePath = "row-cover.png",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "songs/lead.sng" }]
    };

    /// <summary>A package source that answers from a table and records what it was asked.</summary>
    private sealed class FakeSource(string extension) : ILibraryPackageSource
    {
        public Dictionary<string, IReadOnlyList<LibrarySongEntry>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ManualResetEventSlim Entered { get; } = new();
        public HashSet<string> Fails { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> ReadQueue { get; } = new();
        public List<string> Read => [.. ReadQueue];
        public int ScansBegun { get; private set; }
        public ChartProject Loaded { get; } = new();
        public IReadOnlyList<string> FileExtensions { get; } = [extension];

        public void BeginScan() => ScansBegun++;

        public IReadOnlyList<LibrarySongEntry> ReadForIndex(string path, CancellationToken cancellationToken)
        {
            Assert.EndsWith(extension, path, StringComparison.OrdinalIgnoreCase);
            ReadQueue.Enqueue(path);
            if (Blocks.Contains(path))
            {
                Entered.Set();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(120));
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Fails.Contains(path) ? throw new InvalidDataException("damaged") : Rows.GetValueOrDefault(path, []);
        }

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) =>
            Task.FromResult(new LibraryLoadedSong { Project = Loaded });

        public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken) =>
            extension == ".dcsong" ? LibraryAudioPrepareOutcome.Prepared : LibraryAudioPrepareOutcome.AlreadyPrepared;

        public string LoadCover(LibrarySongEntry song) => extension + "-cover";

        public LibrarySongBlock BlockFor(LibrarySongEntry song) =>
            extension == ".dcsong" ? LibrarySongBlock.AudioDecoderMissing : LibrarySongBlock.None;

        public PackageProbeResult Probe(string path) =>
            new(true, extension == ".dcsong" ? AudioAvailability.Missing : AudioAvailability.Present);
    }
}
