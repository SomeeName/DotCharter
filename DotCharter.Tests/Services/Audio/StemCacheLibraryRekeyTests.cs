using System.Security.Cryptography;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #898: a library song's stem archive is named after its <c>lib:</c> identity. Every
/// build before hashed the <c>psarc:</c> spelling, so an archive found under that old
/// name is RE-KEYED on lookup — copied with its recorded identity rewritten, verified,
/// renamed, and only then is the old file removed. Stems are never re-separated: every
/// test here drives the lookup the way the app does (separate only on a miss) and
/// asserts the separator is never called. Everything runs in a temp folder.
/// </summary>
public sealed class StemCacheLibraryRekeyTests : IDisposable
{
    private const string SongKey = "SONGKEY|Charter|1";

    private readonly string _root = Directory.CreateTempSubdirectory("dc-stem-rekey-").FullName;
    private readonly string _wem;
    private readonly string _wav;
    private readonly CountingSeparator _separator = new();

    public StemCacheLibraryRekeyTests()
    {
        var songDir = Directory.CreateDirectory(Path.Combine(_root, "PsarcAudioCache", "song_ABC_123")).FullName;
        _wem = Path.Combine(songDir, "72717684.wem");
        _wav = Path.ChangeExtension(_wem, ".wav");
        File.WriteAllBytes(_wem, Bytes(900_000, seed: 7));
        File.WriteAllBytes(_wav, Bytes(1_000_000, seed: 1));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>The identity as the app builds it today.</summary>
    private StemCacheIdentity Library => StemCacheIdentity.ForLibrary("lib:" + SongKey, _wav);

    /// <summary>The identity as every build before #898 hashed it.</summary>
    private StemCacheIdentity Legacy => new() { LogicalId = "psarc:" + SongKey, SourceLabel = _wav };

    // ---- the re-key ----------------------------------------------------------------

    [Theory]
    [InlineData(StemCodec.Wav)]
    [InlineData(StemCodec.Opus)]
    public async Task An_old_named_archive_is_rekeyed_with_byte_identical_stems_and_nothing_is_separated(StemCodec codec)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(codec);
        var legacyKey = await SaveLegacyWemAsync(store, ct);
        var oldPath = store.ArchivePath(legacyKey);
        var oldEntries = EntryBytes(oldPath);
        var key = StemCacheSource.TryResolve(Library, _wav)!.Key;
        Assert.Equal(legacyKey, key.LegacyLibraryForm);
        Assert.False(File.Exists(store.ArchivePath(key)));

        var hit = await LoadOrSeparateAsync(store, "1", ct);

        Assert.Equal(0, _separator.Calls);
        Assert.Equal(new[] { "drums", "bass" }, hit!.Select(s => s.Name));
        Assert.True(File.Exists(store.ArchivePath(key)));
        Assert.False(File.Exists(oldPath));

        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(key.Canonical, manifest.Key);
        Assert.Equal("lib:" + SongKey, manifest.LogicalId);
        Assert.Equal(codec, manifest.Codec);
        Assert.Equal(StemAudioSignature.ComputeFullFileHash(_wem), manifest.SourceFullHash);

        // Every stem's stored bytes, and the WAVs the hit expanded, are the originals.
        var newEntries = EntryBytes(store.ArchivePath(key));
        Assert.Equal(oldEntries.Keys.Order(), newEntries.Keys.Order());
        foreach (var (name, bytes) in oldEntries) Assert.Equal(bytes, newEntries[name]);
        Assert.Equal(File.ReadAllBytes(StemFile("drums", 9)), File.ReadAllBytes(hit![0].Path));
        Assert.Equal(File.ReadAllBytes(StemFile("bass", 10)), File.ReadAllBytes(hit[1].Path));
        Assert.Empty(StagingDirs(store));
    }

    [Fact]
    public async Task A_second_lookup_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        await SaveLegacyWemAsync(store, ct);
        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));

        var key = StemCacheSource.TryResolve(Library, _wav)!.Key;
        var path = store.ArchivePath(key);
        var files = Directory.GetFileSystemEntries(store.Root).Order().ToList();
        var digest = FileDigest(path);
        var written = File.GetLastWriteTimeUtc(path);

        Assert.NotNull(await LoadOrSeparateAsync(store, "2", ct));
        Assert.False(store.TryRekeyLegacyLibraryArchive(key));

        Assert.Equal(0, _separator.Calls);
        Assert.Equal(files, Directory.GetFileSystemEntries(store.Root).Order().ToList());
        Assert.Equal(digest, FileDigest(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task A_library_song_with_no_wem_beside_its_audio_is_rekeyed_on_its_playable_key()
    {
        var ct = TestContext.Current.CancellationToken;
        File.Delete(_wem);
        var store = Store(StemCodec.Opus);
        var signature = StemAudioSignature.ComputeFile(_wav);
        var legacyKey = StemCacheKey.TryDerive(Legacy, signature)!;
        await store.SaveAsync(legacyKey, Legacy, signature, Stems(), "m", "1.0", ct, StemAudioSignature.ComputeFullFileHash(_wav));

        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));

        var key = StemCacheKey.TryDerive(Library, signature)!;
        Assert.Equal(0, _separator.Calls);
        Assert.False(File.Exists(store.ArchivePath(legacyKey)));
        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(key.Canonical, manifest.Key);
    }

    // ---- failures leave the old archive findable ------------------------------------

    /// <summary>A failing write (the disk filled while the copy was staged): the old
    /// archive is untouched under its old name, the lookup still HITS it there, and the
    /// next lookup completes the re-key.</summary>
    [Fact]
    public async Task A_failing_write_leaves_the_old_archive_intact_and_still_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var legacyKey = await SaveLegacyWemAsync(store, ct);
        var oldPath = store.ArchivePath(legacyKey);
        var before = FileDigest(oldPath);
        var key = StemCacheSource.TryResolve(Library, _wav)!.Key;
        store.RekeyFaultHook = (stage, _) =>
        {
            if (stage == LegacyLibraryRekey.Stage.Written) throw new IOException("There is not enough space on the disk.");
        };

        var hit = await LoadOrSeparateAsync(store, "1", ct);

        Assert.Equal(0, _separator.Calls);
        Assert.Equal(new[] { "drums", "bass" }, hit!.Select(s => s.Name));
        Assert.Equal(before, FileDigest(oldPath));
        Assert.False(File.Exists(store.ArchivePath(key)));
        Assert.Empty(StagingDirs(store));

        store.RekeyFaultHook = null;
        Assert.NotNull(await LoadOrSeparateAsync(store, "2", ct));
        Assert.Equal(0, _separator.Calls);
        Assert.True(File.Exists(store.ArchivePath(key)));
        Assert.False(File.Exists(oldPath));
    }

    /// <summary>A copy that does not read back identical (here: torn in half) is
    /// discarded and never replaces anything.</summary>
    [Fact]
    public async Task A_copy_that_does_not_read_back_identical_is_discarded()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var legacyKey = await SaveLegacyWemAsync(store, ct);
        var oldPath = store.ArchivePath(legacyKey);
        var before = FileDigest(oldPath);
        store.RekeyFaultHook = (stage, staged) =>
        {
            if (stage != LegacyLibraryRekey.Stage.Written) return;
            var bytes = File.ReadAllBytes(staged);
            File.WriteAllBytes(staged, bytes[..(bytes.Length / 2)]);
        };

        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));

        Assert.Equal(0, _separator.Calls);
        Assert.Equal(before, FileDigest(oldPath));
        Assert.False(File.Exists(store.ArchivePath(StemCacheSource.TryResolve(Library, _wav)!.Key)));
    }

    /// <summary>A crash between the rename and the delete: both names exist. The new one
    /// is used and the old one is kept — a duplicate is never deleted automatically.</summary>
    [Fact]
    public async Task A_crash_after_the_rename_keeps_both_and_uses_the_new_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var legacyKey = await SaveLegacyWemAsync(store, ct);
        var oldPath = store.ArchivePath(legacyKey);
        var before = FileDigest(oldPath);
        var key = StemCacheSource.TryResolve(Library, _wav)!.Key;
        store.RekeyFaultHook = (stage, _) =>
        {
            if (stage == LegacyLibraryRekey.Stage.Moved) throw new IOException("power cut");
        };

        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));
        store.RekeyFaultHook = null;
        Assert.NotNull(await LoadOrSeparateAsync(store, "2", ct));

        Assert.Equal(0, _separator.Calls);
        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(key.Canonical, manifest.Key);
        Assert.Equal(before, FileDigest(oldPath));
    }

    /// <summary>An archive already under the new name wins; the old-named twin is left
    /// exactly as it was.</summary>
    [Fact]
    public async Task A_twin_under_the_new_name_wins_and_the_old_one_is_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var legacyKey = await SaveLegacyWemAsync(store, ct);
        var oldPath = store.ArchivePath(legacyKey);
        var before = FileDigest(oldPath);
        var source = StemCacheSource.TryResolve(Library, _wav)!;
        await store.SaveAsync(source.Key, Library, source.Signature,
            [new StemTrack { Name = "vocals", Path = StemFile("vocals", 11) }], "m", "1.0", ct,
            StemAudioSignature.ComputeFullFileHash(_wem));

        var hit = await LoadOrSeparateAsync(store, "1", ct);

        Assert.Equal(0, _separator.Calls);
        Assert.Equal("vocals", Assert.Single(hit!).Name);
        Assert.Equal(before, FileDigest(oldPath));
    }

    // ---- with the #426 WEM-key adoption ----------------------------------------------

    /// <summary>An archive older than #426 too (keyed on the playable WAV, psarc:
    /// spelling): the lookup re-keys and adopts it onto the lib: WEM key.</summary>
    [Fact]
    public async Task A_pre_426_archive_is_rekeyed_and_adopted_onto_the_wem_key_by_the_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var oldPath = await SaveLegacyPlayableAsync(store, ct);

        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));

        Assert.Equal(0, _separator.Calls);
        Assert.False(File.Exists(oldPath));
        AssertAdoptedOntoWemKey(store);
    }

    /// <summary>The same archive through #431's startup pass: re-keyed, not just renamed,
    /// so the lib: WEM key accepts what it records.</summary>
    [Fact]
    public async Task The_startup_pass_rekeys_a_pre_426_archive_onto_the_wem_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(StemCodec.Opus);
        var oldPath = await SaveLegacyPlayableAsync(store, ct);

        Assert.Equal(1, WemKeyAdoption.AdoptAll(store, Path.Combine(_root, "PsarcAudioCache"), logger: null, ct));

        Assert.False(File.Exists(oldPath));
        AssertAdoptedOntoWemKey(store);
        Assert.NotNull(await LoadOrSeparateAsync(store, "1", ct));
        Assert.Equal(0, _separator.Calls);
        Assert.Equal(0, WemKeyAdoption.AdoptAll(store, Path.Combine(_root, "PsarcAudioCache"), logger: null, ct));
    }

    // ---- helpers ---------------------------------------------------------------------

    private void AssertAdoptedOntoWemKey(StemCacheStore store)
    {
        var key = StemCacheSource.TryResolve(Library, _wav)!.Key;
        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.Equal(StemCacheKey.TryDerive(Library, StemAudioSignature.ComputeFile(_wav))!.Canonical, manifest.Key);
        Assert.Equal("lib:" + SongKey, manifest.LogicalId);
    }

    /// <summary>The app's own lookup: the cache first, the separator only on a miss
    /// (<c>StemSeparationViewModel.SeparateStemsAsync</c>).</summary>
    private async Task<IReadOnlyList<StemTrack>?> LoadOrSeparateAsync(StemCacheStore store, string run, CancellationToken ct)
    {
        var playback = Path.Combine(_root, "playback-" + run);
        var hit = await new StemCacheCoordinator(store).TryLoadCachedAsync(Library, _wav, playback, ct);
        if (hit is null) await _separator.SeparateAsync(_wav, playback, ct: ct);
        return hit;
    }

    /// <summary>Saves the way a build between #426 and #898 did: the WEM key, psarc: basis.</summary>
    private async Task<StemCacheKey> SaveLegacyWemAsync(StemCacheStore store, CancellationToken ct)
    {
        var signature = StemAudioSignature.ComputeFile(_wem);
        var key = StemCacheKey.TryDeriveForWem(Legacy, signature)!;
        await store.SaveAsync(key, Legacy, signature, Stems(), "m", "1.0", ct, StemAudioSignature.ComputeFullFileHash(_wem));
        Assert.True(store.TryGetManifest(key, out var manifest));
        Assert.StartsWith("psarc:", manifest.Key, StringComparison.Ordinal);
        return key;
    }

    /// <summary>Saves the way a build before #426 did: the playable WAV's key, psarc: basis.</summary>
    private async Task<string> SaveLegacyPlayableAsync(StemCacheStore store, CancellationToken ct)
    {
        var signature = StemAudioSignature.ComputeFile(_wav);
        var key = StemCacheKey.TryDerive(Legacy, signature)!;
        await store.SaveAsync(key, Legacy, signature, Stems(), "m", "1.0", ct, StemAudioSignature.ComputeFullFileHash(_wav));
        return store.ArchivePath(key);
    }

    private StemCacheStore Store(StemCodec codec) =>
        new(new CopyEncoder(codec), logger: null, rootOverride: Path.Combine(_root, "StemCache"));

    private List<StemTrack> Stems() =>
    [
        new() { Name = "drums", Path = StemFile("drums", 9) },
        new() { Name = "bass", Path = StemFile("bass", 10) },
    ];

    private string StemFile(string name, int seed)
    {
        var path = Path.Combine(_root, "separated", name + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, Bytes(50_000, seed));
        return path;
    }

    private static Dictionary<string, byte[]> EntryBytes(string archive) =>
        StemArchive.ReadManifest(archive)!.Stems
            .ToDictionary(s => s.FileName, s => StemArchive.ReadEntryBytes(archive, s.FileName)!);

    private static string[] StagingDirs(StemCacheStore store) =>
        Directory.GetDirectories(store.Root, StemCacheStore.StagingPrefix + "*");

    private static string FileDigest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Counts every separation the lookup would have started.</summary>
    private sealed class CountingSeparator : IStemSeparationService
    {
        public int Calls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("counting");

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(StemSeparationResult.Failure("a cached song must never be separated"));
        }
    }

    /// <summary>"Encodes" by copying, under the codec's extension.</summary>
    private sealed class CopyEncoder(StemCodec codec) : IStemEncoder
    {
        public StemCodec PreferredCodec => codec;
        public bool IsExternalEncoderAvailable => codec != StemCodec.Wav;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec target,
            CancellationToken ct = default)
        {
            var dest = destPathWithoutExtension + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath,
            CancellationToken ct = default)
        {
            File.Copy(encodedPath, destWavPath, overwrite: true);
            return Task.FromResult(destWavPath);
        }
    }
}
