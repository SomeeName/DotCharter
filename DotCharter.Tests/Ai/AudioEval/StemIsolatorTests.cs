using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The isolator's contract, with a fake separator and a throwaway cache root.
///     <para>
///         <b>The case that matters is the second one.</b> "Stems are NEVER
///         regenerated" is the rule the whole eval's runtime depends on — separating
///         35 songs is an evening, separating them twice is a weekend — and the only
///         way to hold it is a test that counts how many times the backend was asked.
///     </para>
/// </summary>
public sealed class StemIsolatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-match-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string NewSong(string name = "song.wav")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(_root);
        WriteWav(path, Tone(440, 6.0), 48_000);
        return path;
    }

    private static float[] Tone(double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(seconds * 48_000)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Sin(2 * Math.PI * hz * i / 48_000.0) * amplitude);
        return samples;
    }

    private static void WriteWav(string path, float[] mono, int rate) =>
        WavIo.WriteFloat32(path, new AudioBuffer(mono, rate, 1));

    private StemIsolator NewIsolator(FakeSeparator separator) =>
        new(separator, new StemCacheStore(new CopyEncoder(), rootOverride: Path.Combine(_root, "cache")), FakeLocalization.English());

    /// <summary>Writes the stems it is asked for and counts how often it ran.</summary>
    private sealed class FakeSeparator(params string[] stemNames) : IStemSeparationService
    {
        public int Separations { get; private set; }

        public bool Available { get; set; } = true;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("fake-htdemucs");

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Separations++;
            Directory.CreateDirectory(outputDirectory);
            var tracks = new List<StemTrack>();
            for (var i = 0; i < stemNames.Length; i++)
            {
                var path = Path.Combine(outputDirectory, stemNames[i] + ".wav");
                // Each stem gets its own pitch so a test can tell which one it got.
                WriteWav(path, Tone(220 * (i + 1), 6.0), 48_000);
                tracks.Add(new StemTrack { Name = stemNames[i], Path = path });
            }

            return Task.FromResult(new StemSeparationResult
            {
                Success = true, OutputDirectory = outputDirectory, Stems = tracks
            });
        }
    }

    private sealed class CopyEncoder(bool failDecode = false) : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Wav;

        public bool IsExternalEncoderAvailable => false;

        public Task<string> EncodeAsync(string wavPath, string destNoExt, StemCodec target, CancellationToken ct = default)
        {
            var dest = destNoExt + ".wav";
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath, CancellationToken ct = default)
        {
            if (failDecode) throw new IOException("decoder unavailable");
            File.Copy(encodedPath, destWavPath, overwrite: true);
            return Task.FromResult(destWavPath);
        }
    }

    [Fact]
    public async Task IsolatesTheGuitarStemAndCutsTheSpan()
    {
        var separator = new FakeSeparator("drums", "bass", "other", "vocals", "guitar", "piano");
        var isolator = NewIsolator(separator);

        var audio = await isolator.IsolateGuitarAsync(
            NewSong(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(48_000, audio.SampleRate);
        Assert.Equal(2.0, audio.Duration.TotalSeconds, 2);
        Assert.Contains(audio.Samples, s => Math.Abs(s) > 0.1f);
    }

    [Theory]
    [InlineData("guitar")]
    [InlineData("bass")]
    public async Task Tone_matching_caches_a_sample_collision_once_through_its_actual_save_path(string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var song = NewSong();
        WriteWav(song, Tone(440, 24), 48_000);
        var original = File.ReadAllBytes(song);
        var signature = StemAudioSignature.ComputeFile(song);
        var separator = new FakeSeparator("guitar", "bass");
        await NewIsolator(separator).IsolateAsync(song, stem, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct);
        var cache = Path.Combine(_root, "cache");
        var first = Assert.Single(Directory.GetFiles(cache, "*.dcstems"));
        var firstBytes = File.ReadAllBytes(first);
        var changed = (byte[])original.Clone();
        changed[1024 * 1024] ^= 1;
        File.WriteAllBytes(song, changed);
        File.SetLastWriteTimeUtc(song, File.GetLastWriteTimeUtc(song).AddSeconds(5));
        Assert.Equal(signature, StemAudioSignature.ComputeFile(song));

        await NewIsolator(separator).IsolateAsync(song, stem, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct);
        await NewIsolator(separator).IsolateAsync(song, stem, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct);

        Assert.Equal(2, separator.Separations); // one for each audio, across fresh callers
        var archives = Directory.GetFiles(cache, "*.dcstems").ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(2, archives.Count);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        File.WriteAllBytes(song, original);
        File.SetLastWriteTimeUtc(song, File.GetLastWriteTimeUtc(song).AddSeconds(10));
        await NewIsolator(separator).IsolateAsync(song, stem, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct);
        Assert.Equal(2, separator.Separations);
        Assert.All(archives, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
    }

    [Fact]
    public async Task A_retained_archive_that_cannot_decode_never_starts_a_second_separation()
    {
        var ct = TestContext.Current.CancellationToken;
        var separator = new FakeSeparator("guitar");
        var store = new StemCacheStore(new CopyEncoder(failDecode: true), rootOverride: Path.Combine(_root, "cache"));
        var isolator = new StemIsolator(separator, store, FakeLocalization.English());
        var song = NewSong();
        await isolator.ResolveGuitarStemAsync(song, null, ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => isolator.ResolveGuitarStemAsync(song, null, ct));

        Assert.Equal(1, separator.Separations);
        Assert.Single(Directory.GetFiles(store.Root, "*.dcstems"));
    }

    [Fact]
    public void Every_stem_saver_uses_the_store_aware_source()
    {
        var root = TestPaths.RepoRoot();
        foreach (var path in new[]
                 {
                     "DotCharter.Services/Ai/Match/StemIsolator.cs",
                     "DotCharter.Services/Import/Audio/AudioSilenceSplicer.cs",
                     "DotCharter.UI/ViewModels/Shell/StemSeparationViewModel.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            var save = source.LastIndexOf(".SaveAsync(", StringComparison.Ordinal);
            var resolve = source.LastIndexOf("StemCacheSource.ResolveForStore(", save, StringComparison.Ordinal);
            Assert.True(save >= 0 && resolve >= 0, path + " must resolve the store-aware source before saving");
            Assert.DoesNotContain("SaveAsync(key,", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSecondCallIsACacheHit_AndNeverSeparatesAgain()
    {
        var separator = new FakeSeparator("guitar", "other");
        var isolator = NewIsolator(separator);
        var song = NewSong();
        var ct = TestContext.Current.CancellationToken;

        await isolator.IsolateGuitarAsync(song, TimeSpan.Zero, TimeSpan.FromSeconds(2), null, ct);
        Assert.Equal(1, separator.Separations);

        // A fresh isolator over the SAME cache root: this is what a second eval run
        // looks like, and it must not cost a single separation.
        var second = NewIsolator(separator);
        await second.IsolateGuitarAsync(song, TimeSpan.Zero, TimeSpan.FromSeconds(2), null, ct);

        Assert.Equal(1, separator.Separations);
    }

    [Fact]
    public async Task TheCacheHitTestIsNotVacuous_ADifferentSongDoesSeparate()
    {
        var separator = new FakeSeparator("guitar");
        var isolator = NewIsolator(separator);
        var ct = TestContext.Current.CancellationToken;

        await isolator.IsolateGuitarAsync(NewSong("a.wav"), TimeSpan.Zero, TimeSpan.FromSeconds(2), null, ct);
        var other = Path.Combine(_root, "b.wav");
        WriteWav(other, Tone(330, 6.0, 0.4), 48_000);
        await isolator.IsolateGuitarAsync(other, TimeSpan.Zero, TimeSpan.FromSeconds(2), null, ct);

        Assert.Equal(2, separator.Separations);
    }

    /// <summary>
    ///     A four-source model has no <c>guitar</c>; <c>other</c> is the documented
    ///     fallback and the one the existing tone pipeline already uses.
    /// </summary>
    [Fact]
    public async Task FallsBackToOther_WhenTheModelHasNoGuitarSource()
    {
        var separator = new FakeSeparator("drums", "bass", "other", "vocals");
        var isolator = NewIsolator(separator);

        var audio = await isolator.IsolateGuitarAsync(
            NewSong(), TimeSpan.Zero, TimeSpan.FromSeconds(2), null, TestContext.Current.CancellationToken);

        Assert.Contains(audio.Samples, s => Math.Abs(s) > 0.1f);
    }

    /// <summary>
    ///     …and never falls back to something that is not a guitar. A bass stem is a
    ///     plausible-looking array of floats that would produce a plausible-looking
    ///     distance, and the whole row would be wrong with nothing to show for it.
    /// </summary>
    [Fact]
    public async Task RefusesWhenNoGuitarishSourceExists()
    {
        var separator = new FakeSeparator("drums", "bass", "vocals");
        var isolator = NewIsolator(separator);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => isolator.IsolateGuitarAsync(
            NewSong(), TimeSpan.Zero, TimeSpan.FromSeconds(2), null, TestContext.Current.CancellationToken));

        Assert.Contains("guitar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaysSoWhenThereIsNoCacheAndNoBackend()
    {
        var separator = new FakeSeparator("guitar") { Available = false };
        var isolator = NewIsolator(separator);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => isolator.IsolateGuitarAsync(
            NewSong(), TimeSpan.Zero, TimeSpan.FromSeconds(2), null, TestContext.Current.CancellationToken));

        Assert.Contains("no separation backend", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, separator.Separations);
    }
}
