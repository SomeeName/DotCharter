using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// Crash-orphaned <c>.staging-*</c> directories.
///
/// <para><c>SaveAsync</c> stages a full copy of every encoded stem PLUS the
/// finished archive under <c>&lt;cache&gt;/.staging-&lt;token&gt;-&lt;guid&gt;/</c>,
/// removed only by that call's own <c>finally</c> — and the app's normal exit path
/// is <c>App.TerminateNow</c> → <c>TerminateProcess</c>, which runs no
/// <c>finally</c>. Quitting during a cache save therefore left hundreds of MB (GB,
/// on the WAV fallback) behind permanently. It was invisible in BOTH directions:
/// <c>MeasureUsage()</c> and <c>Clear()</c> each enumerated <c>*.dcstems</c>
/// top-directory only, so the Settings ▸ Stem Models "Stem cache" row
/// under-reported the footprint and the "Clear" button could not reclaim it.</para>
///
/// <para>The age guard is the load-bearing part: a sweep runs from the SAVE path,
/// so it must never be able to take a save that is running right now — including
/// one in a second instance of the app.</para>
/// </summary>
public sealed class StemCacheStagingSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dc-stemstaging-" + Guid.NewGuid().ToString("N"));

    public StemCacheStagingSweepTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private StemCacheStore Store() => new(rootOverride: _root);

    private void Archive(string token, int bytes) =>
        File.WriteAllBytes(Path.Combine(_root, token + StemArchive.ArchiveExtension), new byte[bytes]);

    /// <summary>A staging dir with one payload file, aged as requested. The
    /// directory's write time is set LAST, because writing a file inside it bumps
    /// the directory's own timestamp.</summary>
    private string Staging(string token, int payloadBytes, TimeSpan age)
    {
        var dir = Path.Combine(_root, ".staging-" + token + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "drums.wav"), new byte[payloadBytes]);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public void An_abandoned_staging_dir_is_swept()
    {
        var stale = Staging("aaa", 4096, TimeSpan.FromHours(2));

        Assert.Equal(1, Store().SweepAbandonedStaging());
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void A_staging_dir_a_live_save_could_still_own_is_left_alone()
    {
        var fresh = Staging("bbb", 4096, TimeSpan.FromMinutes(1));

        Assert.Equal(0, Store().SweepAbandonedStaging());
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void The_sweep_never_touches_an_archive()
    {
        Archive("keep", 1234);
        Staging("ccc", 10, TimeSpan.FromHours(2));

        Store().SweepAbandonedStaging();

        // Nothing here is cache eviction: the .dcstems archive is the artifact the
        // never-regenerate invariant protects.
        Assert.True(File.Exists(Path.Combine(_root, "keep" + StemArchive.ArchiveExtension)));
    }

    [Fact]
    public void Usage_counts_the_bytes_an_abandoned_staging_dir_is_holding()
    {
        Archive("aaa", 1000);
        Staging("orphan", 4096, TimeSpan.FromHours(2));

        var usage = Store().MeasureUsage();

        Assert.Equal(1, usage.Songs);          // scratch is not a cached song…
        Assert.Equal(1000 + 4096, usage.Bytes); // …but it IS space the user can reclaim
    }

    [Fact]
    public void Usage_does_not_count_a_staging_dir_a_live_save_owns()
    {
        Archive("aaa", 1000);
        Staging("live", 4096, TimeSpan.FromMinutes(1));

        Assert.Equal(1000, Store().MeasureUsage().Bytes);
    }

    [Fact]
    public void Clear_reclaims_the_staging_dirs_as_well_as_the_archives()
    {
        Archive("aaa", 10);
        var stale = Staging("ddd", 4096, TimeSpan.FromHours(2));
        var fresh = Staging("eee", 4096, TimeSpan.FromMinutes(1));

        var store = Store();
        Assert.Equal(1, store.Clear()); // the return value stays "archives deleted"

        Assert.Empty(Directory.GetFiles(_root, "*" + StemArchive.ArchiveExtension));
        Assert.False(Directory.Exists(stale));
        // Even an explicit user Clear must not destroy a save that is running.
        Assert.True(Directory.Exists(fresh));
    }

    /// <summary>The save path is the one that runs on every real user session, so
    /// it is the one that has to reclaim what an earlier hard exit stranded.</summary>
    [Fact]
    public async Task Saving_sweeps_what_an_earlier_hard_exit_stranded()
    {
        var ct = TestContext.Current.CancellationToken;
        var stale = Staging("fff", 4096, TimeSpan.FromHours(2));

        var wav = Path.Combine(_root, "src-drums.wav");
        await File.WriteAllBytesAsync(wav, new byte[64], ct);

        var store = new StemCacheStore(new CopyEncoder(), logger: null, rootOverride: _root);
        var id = StemCacheIdentity.ForLibrary("lib:SWEEP");
        var sig = new StemAudioSignature(64, "sw");
        await store.SaveAsync(StemCacheKey.Derive(id, sig), id, sig,
            [new StemTrack { Name = "drums", Path = wav }], "model", "1.0", ct);

        Assert.False(Directory.Exists(stale));
        // …and the save's own scratch dir is gone by its own finally, as before.
        Assert.Empty(Directory.GetDirectories(_root, ".staging-*"));
    }

    /// <summary>Encodes by copying — the store round-trips without ffmpeg.</summary>
    private sealed class CopyEncoder : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Wav;
        public bool IsExternalEncoderAvailable => false;

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
