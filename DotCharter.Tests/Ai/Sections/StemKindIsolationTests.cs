using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     Stem-kind isolation: all four <see cref="ToneStems" /> out of ONE cached
///     separation, over a fake separator and a throwaway cache root.
///     <para>
///         <b>The two cases that matter are the last two.</b> Every stem coming out of
///         one separation is what makes a four-section song affordable at all; and a
///         stem that is not the instrument asked for must REFUSE rather than
///         substitute, because a bass part scored against the <c>other</c> stem
///         produces a perfectly plausible distance and a rig that is wrong for a
///         reason nothing in the result can show.
///     </para>
/// </summary>
public sealed class StemKindIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-sections-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string NewSong(string name = "song.wav")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        WavIo.WriteFloat32(path, new AudioBuffer(Tone(440, 8.0), 48_000, 1));
        return path;
    }

    private static float[] Tone(double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(seconds * 48_000)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Sin(2 * Math.PI * hz * i / 48_000.0) * amplitude);
        return samples;
    }

    private StemIsolator NewIsolator(FakeSeparator separator) =>
        new(separator, new StemCacheStore(new CopyEncoder(), rootOverride: Path.Combine(_root, "cache")), FakeLocalization.English());

    /// <summary>Each stem gets its OWN pitch, so a test can prove which one it got
    /// rather than only that it got something.</summary>
    private sealed class FakeSeparator(params string[] stemNames) : IStemSeparationService
    {
        public static readonly IReadOnlyDictionary<string, double> Pitches = new Dictionary<string, double>
        {
            ["drums"] = 100, ["bass"] = 200, ["other"] = 300,
            ["vocals"] = 400, ["guitar"] = 500, ["piano"] = 600
        };

        public int Separations { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("fake-htdemucs");

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Separations++;
            Directory.CreateDirectory(outputDirectory);
            var tracks = new List<StemTrack>();
            foreach (var name in stemNames)
            {
                var path = Path.Combine(outputDirectory, name + ".wav");
                WavIo.WriteFloat32(path, new AudioBuffer(Tone(Pitches[name], 8.0), 48_000, 1));
                tracks.Add(new StemTrack { Name = name, Path = path });
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

    /// <summary>The dominant frequency of a clip, by counting zero crossings — enough
    /// to tell 200 Hz from 500 Hz, which is all this needs.</summary>
    private static double DominantHz(MonoAudio audio)
    {
        var crossings = 0;
        for (var i = 1; i < audio.Samples.Length; i++)
            if ((audio.Samples[i - 1] < 0) != (audio.Samples[i] < 0)) crossings++;
        return crossings / 2.0 / audio.Duration.TotalSeconds;
    }

    [Theory]
    [InlineData(ToneStems.Guitar, 500.0)]
    [InlineData(ToneStems.Bass, 200.0)]
    [InlineData(ToneStems.Piano, 600.0)]
    [InlineData(ToneStems.Other, 300.0)]
    public async Task EveryToneStem_ComesBackAsItself(string stem, double expectedHz)
    {
        var separator = new FakeSeparator("drums", "bass", "other", "vocals", "guitar", "piano");
        var isolator = NewIsolator(separator);

        var audio = await isolator.IsolateAsync(
            NewSong(), stem, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(4.0, audio.Duration.TotalSeconds, 2);
        Assert.Equal(expectedHz, DominantHz(audio), 1.0);
    }

    /// <summary>
    ///     Four sections of one song cost ONE separation. This is the rule the whole
    ///     feature's runtime rests on, and the only way to hold it is to count.
    /// </summary>
    [Fact]
    public async Task AllFourStems_CostOneSeparation()
    {
        var separator = new FakeSeparator("drums", "bass", "other", "vocals", "guitar", "piano");
        var isolator = NewIsolator(separator);
        var song = NewSong();
        var ct = TestContext.Current.CancellationToken;

        foreach (var stem in ToneStems.All)
            await isolator.IsolateAsync(song, stem, TimeSpan.Zero, TimeSpan.FromSeconds(4), null, ct);

        Assert.Equal(1, separator.Separations);
    }

    /// <summary>
    ///     <c>other</c> is a documented, degraded answer for a guitar or a piano a
    ///     four-source model never emitted — and is NEVER an answer for the bass,
    ///     which every model emits and which <c>other</c> is defined as excluding.
    /// </summary>
    [Fact]
    public async Task FourSourceModel_FallsBackForGuitarAndPiano_ButNeverForBass()
    {
        var separator = new FakeSeparator("drums", "bass", "other", "vocals");
        var isolator = NewIsolator(separator);
        var song = NewSong();
        var ct = TestContext.Current.CancellationToken;

        var guitar = await isolator.IsolateAsync(song, ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(4), null, ct);
        var piano = await isolator.IsolateAsync(song, ToneStems.Piano, TimeSpan.Zero, TimeSpan.FromSeconds(4), null, ct);
        Assert.Equal(300.0, DominantHz(guitar), 1.0);
        Assert.Equal(300.0, DominantHz(piano), 1.0);

        // …and the bass is still the bass, not the fallback.
        var bass = await isolator.IsolateAsync(song, ToneStems.Bass, TimeSpan.Zero, TimeSpan.FromSeconds(4), null, ct);
        Assert.Equal(200.0, DominantHz(bass), 1.0);
    }

    [Fact]
    public async Task AMissingBassStemRefuses_RatherThanSubstituting()
    {
        var separator = new FakeSeparator("drums", "other", "vocals");
        var isolator = NewIsolator(separator);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => isolator.IsolateAsync(
            NewSong(), ToneStems.Bass, TimeSpan.Zero, TimeSpan.FromSeconds(4), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("bass", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never an acceptable substitute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown stem name is refused BEFORE the separator is asked: finding
    /// out at the end of a twenty-minute separation is the worst moment to find out.</summary>
    [Fact]
    public async Task AnUnknownStemIsRefusedWithoutSeparating()
    {
        var separator = new FakeSeparator("guitar", "bass");
        var isolator = NewIsolator(separator);

        await Assert.ThrowsAsync<NotSupportedException>(() => isolator.IsolateAsync(
            NewSong(), "guitars", TimeSpan.Zero, TimeSpan.FromSeconds(4), null,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, separator.Separations);
    }

    /// <summary>
    ///     The interface's DEFAULT implementation is the narrow one: an isolator that
    ///     only knows the guitar must SAY so rather than hand back a guitar for a bass
    ///     section.
    /// </summary>
    [Fact]
    public async Task TheDefaultImplementation_ForwardsGuitarAndRefusesTheRest()
    {
        IStemIsolator legacy = new GuitarOnlyIsolator();
        var ct = TestContext.Current.CancellationToken;

        var guitar = await legacy.IsolateAsync("song.wav", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct);
        Assert.Equal(SectionFakes.Rate, guitar.SampleRate);

        await Assert.ThrowsAsync<NotSupportedException>(() => legacy.IsolateAsync(
            "song.wav", ToneStems.Bass, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, ct));
    }

    private sealed class GuitarOnlyIsolator : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(
            string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct) =>
            Task.FromResult(new MonoAudio(new float[SectionFakes.Rate], SectionFakes.Rate));
    }
}
