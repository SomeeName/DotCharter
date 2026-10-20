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

    private sealed class CopyEncoder : IStemEncoder
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
