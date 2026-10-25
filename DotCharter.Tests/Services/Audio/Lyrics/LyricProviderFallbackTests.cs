using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     A lyric model that creates on DirectML and then fails its first <c>Run</c>
///     is answered on the CPU, and the CPU is remembered per model.
///     <para>
///         The real failure needs the 340 MB graph and a GPU, so a fake session
///         stands in for it: it reports the provider it was opened on and throws
///         from <c>Encode</c> when that provider is a GPU and the model is on the
///         "GPU broken" list, exactly where the int8 MMS-FA export fails.
///     </para>
/// </summary>
public sealed class LyricProviderFallbackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-lyric-fallback").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static LyricVocabulary Vocab() => LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);

    /// <summary>"a" then "b", five frames each, blank in between: a matrix the aligner can place.</summary>
    private static CtcEmissions Spell()
    {
        var vocab = Vocab();
        var width = vocab.WidthWithStar;
        const string letters = "ab";
        const int perLetter = 5;
        var frames = letters.Length * perLetter;
        var data = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var winner = f % perLetter == 0 && vocab.TryGetId(letters[f / perLetter], out var id) ? id : vocab.BlankId;
            for (var s = 0; s < width; s++)
                data[f * width + s] = (float)Math.Log(s == winner ? 0.9 : 0.1 / (width - 1));
        }

        return new CtcEmissions(data, frames, width, 0.02);
    }

    private string InstallModels(bool english)
    {
        var root = Path.Combine(_dir, "models");
        foreach (var id in english
                     ? new[] { LyricModelCatalog.MultilingualId, LyricModelCatalog.EnglishId }
                     : [LyricModelCatalog.MultilingualId])
        {
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "model.onnx"), "not a real graph");
            File.WriteAllText(Path.Combine(folder, "vocab.json"), "{}");
        }

        return root;
    }

    private string Audio()
    {
        var path = Path.Combine(_dir, "song.wav");
        File.WriteAllText(path, "audio");
        return path;
    }

    /// <summary>Opens fakes, records every open, and breaks the models it is told to on a GPU or on the CPU.</summary>
    private sealed class Rig
    {
        public readonly List<(string Id, OnnxExecutionProvider Provider)> Opened = [];
        public readonly List<FakeSession> Sessions = [];
        public readonly HashSet<string> GpuBroken = [];
        public readonly HashSet<string> CpuBroken = [];

        public ILyricAcousticSession Open(LyricModel model, OnnxExecutionProvider requested, Action<string>? log)
        {
            // What the factory's Auto ladder does on a machine with a DirectX 12 GPU.
            var actual = requested == OnnxExecutionProvider.Auto ? OnnxExecutionProvider.DirectML : requested;
            Opened.Add((model.Id, actual));
            var broken = actual == OnnxExecutionProvider.Cpu ? CpuBroken.Contains(model.Id) : GpuBroken.Contains(model.Id);
            var session = new FakeSession(actual, broken ? $"{actual} cannot run {model.Id}" : null);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession(OnnxExecutionProvider provider, string? failure) : ILyricAcousticSession
    {
        public bool Disposed { get; private set; }

        public LyricVocabulary Vocabulary { get; } = Vocab();

        public OnnxExecutionProvider ActiveProvider => provider;

        public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default) =>
            failure == null ? Spell() : throw new InvalidOperationException(failure);

        public void Dispose() => Disposed = true;
    }

    private LyricAutoSyncService Service(string roots, Rig rig, LyricProviderMemory memory) => new(
        FakeLocalization.English(),
        logger: null,
        modelDirectoryOverride: null,
        vocalStemLocator: _ => null,
        sessionOpener: rig.Open,
        audioLoader: (_, _) => new float[16000],
        modelRoots: [roots],
        providerMemory: memory);

    private static readonly LyricSyncOptions Options = new() { StarLogProb = -20.0 };

    [Fact]
    public async Task AGpuRunThatFailsIsRetriedOnTheCpuAndTheResultIsReturned()
    {
        var rig = new Rig();
        rig.GpuBroken.Add(LyricModelCatalog.MultilingualId);

        var result = await Service(InstallModels(english: false), rig, new LyricProviderMemory())
            .AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.Equal(2, result.Syllables.Count);
        Assert.Equal(
            [(LyricModelCatalog.MultilingualId, OnnxExecutionProvider.DirectML),
             (LyricModelCatalog.MultilingualId, OnnxExecutionProvider.Cpu)],
            rig.Opened);
        // The dead GPU session is released, and the CPU one is released with the sync.
        Assert.All(rig.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task TheCpuChoiceIsRememberedSoALaterSyncNeverOpensDirectMl()
    {
        var rig = new Rig();
        rig.GpuBroken.Add(LyricModelCatalog.MultilingualId);
        var service = Service(InstallModels(english: false), rig, new LyricProviderMemory());

        var first = await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);
        rig.Opened.Clear();
        var second = await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.True(first.Ran, first.Error);
        Assert.True(second.Ran, second.Error);
        Assert.Equal([(LyricModelCatalog.MultilingualId, OnnxExecutionProvider.Cpu)], rig.Opened);
    }

    /// <summary>
    ///     What Settings ▸ AI &amp; models shows: the provider each model last ran on,
    ///     and its "Retry GPU", which forgets the fallback so the next sync asks
    ///     for the GPU again.
    /// </summary>
    [Fact]
    public async Task TheLastProviderIsRecorded_AndRetryGpuOpensTheGpuAgain()
    {
        var rig = new Rig();
        rig.GpuBroken.Add(LyricModelCatalog.MultilingualId);
        var memory = new LyricProviderMemory();
        var service = Service(InstallModels(english: false), rig, memory);
        Assert.Null(memory.LastProvider(LyricModelCatalog.MultilingualId));

        await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);
        Assert.Equal(OnnxExecutionProvider.Cpu, memory.LastProvider(LyricModelCatalog.MultilingualId));
        Assert.True(memory.IsCpuOnly(LyricModelCatalog.MultilingualId));

        rig.GpuBroken.Clear();
        Assert.True(memory.ForgetCpu(LyricModelCatalog.MultilingualId));
        rig.Opened.Clear();
        await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.Equal([(LyricModelCatalog.MultilingualId, OnnxExecutionProvider.DirectML)], rig.Opened);
        Assert.Equal(OnnxExecutionProvider.DirectML, memory.LastProvider(LyricModelCatalog.MultilingualId));
        Assert.False(memory.ForgetCpu(LyricModelCatalog.MultilingualId));
    }

    [Fact]
    public async Task AFailureOnTheCpuIsReportedAndNotRetried()
    {
        var rig = new Rig();
        rig.CpuBroken.Add(LyricModelCatalog.MultilingualId);

        var result = await Service(InstallModels(english: false), rig, new LyricProviderMemory())
            .AlignAsync("a-b", Audio(), new LyricSyncOptions { Provider = OnnxExecutionProvider.Cpu },
                cancellationToken: Ct);

        Assert.False(result.Ran);
        Assert.Contains("Cpu cannot run", result.Error);
        Assert.Equal([(LyricModelCatalog.MultilingualId, OnnxExecutionProvider.Cpu)], rig.Opened);
    }

    [Fact]
    public async Task WhenTheRetryOnTheCpuFailsToo_TheCpuFailureIsTheOneReported()
    {
        var rig = new Rig();
        rig.GpuBroken.Add(LyricModelCatalog.MultilingualId);
        rig.CpuBroken.Add(LyricModelCatalog.MultilingualId);

        var result = await Service(InstallModels(english: false), rig, new LyricProviderMemory())
            .AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.False(result.Ran);
        Assert.Contains("Cpu cannot run", result.Error);
        Assert.Equal(2, rig.Opened.Count);
    }

    [Fact]
    public async Task TheEnsembleRemembersEachHeadOnItsOwn()
    {
        var rig = new Rig();
        // Only the optional English head is broken on the GPU.
        rig.GpuBroken.Add(LyricModelCatalog.EnglishId);
        var service = Service(InstallModels(english: true), rig, new LyricProviderMemory());

        var first = await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.True(first.Ran, first.Error);
        Assert.True(first.IsEnsemble);
        // The retry made the second head work: nothing degraded to the primary alone.
        Assert.Empty(first.Warnings);
        Assert.Single(rig.Opened, o => o.Id == LyricModelCatalog.MultilingualId);
        Assert.Contains((LyricModelCatalog.MultilingualId, OnnxExecutionProvider.DirectML), rig.Opened);
        Assert.Contains((LyricModelCatalog.EnglishId, OnnxExecutionProvider.DirectML), rig.Opened);
        Assert.Contains((LyricModelCatalog.EnglishId, OnnxExecutionProvider.Cpu), rig.Opened);

        rig.Opened.Clear();
        var second = await service.AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.True(second.Ran, second.Error);
        // The multilingual head keeps its GPU; only the English one went to the CPU for good.
        Assert.Contains((LyricModelCatalog.MultilingualId, OnnxExecutionProvider.DirectML), rig.Opened);
        Assert.Contains((LyricModelCatalog.EnglishId, OnnxExecutionProvider.Cpu), rig.Opened);
        Assert.DoesNotContain((LyricModelCatalog.EnglishId, OnnxExecutionProvider.DirectML), rig.Opened);
    }

    [Fact]
    public async Task ABrokenPrimaryAndABrokenSecondaryAreBothRetriedOnTheCpu()
    {
        var rig = new Rig();
        rig.GpuBroken.Add(LyricModelCatalog.MultilingualId);
        rig.GpuBroken.Add(LyricModelCatalog.EnglishId);

        var result = await Service(InstallModels(english: true), rig, new LyricProviderMemory())
            .AlignAsync("a-b", Audio(), Options, cancellationToken: Ct);

        Assert.True(result.Ran, result.Error);
        Assert.True(result.IsEnsemble);
        Assert.Equal(4, rig.Opened.Count);
        Assert.Equal(2, rig.Opened.Count(o => o.Provider == OnnxExecutionProvider.Cpu));
    }

    [Fact]
    public void CancellationIsNeverAnsweredWithARetry()
    {
        var opened = 0;
        using var session = new LyricProviderFallbackSession(
            new LyricModel("m", _dir, 0), OnnxExecutionProvider.Auto,
            (_, provider, _) =>
            {
                opened++;
                return new CancelledSession(provider);
            },
            new LyricProviderMemory(), null, null);

        Assert.Throws<OperationCanceledException>(() => session.Encode(new float[16000], Ct));
        Assert.Equal(1, opened);
    }

    private sealed class CancelledSession(OnnxExecutionProvider provider) : ILyricAcousticSession
    {
        public LyricVocabulary Vocabulary { get; } = Vocab();

        public OnnxExecutionProvider ActiveProvider => OnnxExecutionProvider.DirectML;

        public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(provider.ToString());

        public void Dispose() { }
    }
}
