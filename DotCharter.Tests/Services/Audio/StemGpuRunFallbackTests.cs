using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.AudioIo;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #864: a GPU session that opens and then fails during the run must not fail the
///     separation; the run repeats once on the CPU, which then keeps the model for the session.
/// </summary>
public sealed class StemGpuRunFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharter_StemRunFb_" + Guid.NewGuid().ToString("N"));

    public StemGpuRunFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private sealed class FakeRun(OnnxExecutionProvider provider, bool failsRun) : IOnnxStemRun
    {
        public OnnxExecutionProvider ActiveExecutionProvider => provider;

        public string BackendDescription => provider.ToString();

        public string? FallbackReason => null;

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<OnnxStemBuffer>> SeparateAsync(
            float[] interleavedStereo, int sampleRate, IProgress<double>? progress, CancellationToken ct) =>
            failsRun
                ? throw new InvalidOperationException("DirectML run failed")
                : Task.FromResult<IReadOnlyList<OnnxStemBuffer>>([new OnnxStemBuffer("vocals", interleavedStereo, sampleRate)]);

        public void Dispose() => Disposed = true;
    }

    private (OnnxStemSeparationService Service, List<OnnxExecutionProvider> Opened, FakeAppLogger Log, string Wav)
        Rig(bool gpuFails)
    {
        var cfg = new FakeConfigurationService();
        cfg.AppConfig.StemSeparationExecutionProvider = "DirectML";
        var mgr = new StemModelManager(cfg, logger: null, modelsDirectoryOverride: Path.Combine(_dir, "models"));
        var model = StemModelCatalog.FindById("htdemucs-6s-onnx")!;
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        File.WriteAllBytes(mgr.GetLocalPath(model), new byte[16]);
        mgr.ActiveModelId = model.Id;
        var wav = Path.Combine(_dir, "song.wav");
        WavIo.WriteFloat32(wav, new AudioBuffer(new float[4410], 44100, 2));
        var opened = new List<OnnxExecutionProvider>();
        var log = new FakeAppLogger();
        var service = new OnnxStemSeparationService(mgr, log, null,
            (_, _, provider, _, _, _) =>
            {
                opened.Add(provider);
                return new FakeRun(provider, failsRun: gpuFails && provider != OnnxExecutionProvider.Cpu);
            });
        return (service, opened, log, wav);
    }

    [Fact]
    public async Task AGpuRunThatThrowsIsRerunOnTheCpuAndTheResultSaysSo()
    {
        var (service, opened, log, wav) = Rig(gpuFails: true);

        var result = await service.SeparateAsync(wav, Path.Combine(_dir, "out"), null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.GpuRunFellBackToCpu);
        Assert.Equal([OnnxExecutionProvider.DirectML, OnnxExecutionProvider.Cpu], opened);
        Assert.True(File.Exists(Path.Combine(_dir, "out", "vocals.wav")));
        var warn = Assert.Single(log.Entries, e => e.Level == "WARN" && e.Exception != null);
        Assert.IsType<InvalidOperationException>(warn.Exception);
    }

    [Fact]
    public async Task TheCpuIsKeptForTheNextSeparationOfTheSession()
    {
        var (service, opened, _, wav) = Rig(gpuFails: true);
        var ct = TestContext.Current.CancellationToken;
        await service.SeparateAsync(wav, Path.Combine(_dir, "out1"), null, ct);
        opened.Clear();

        var second = await service.SeparateAsync(wav, Path.Combine(_dir, "out2"), null, ct);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.False(second.GpuRunFellBackToCpu);
        Assert.Equal([OnnxExecutionProvider.Cpu], opened);
    }

    [Fact]
    public async Task AWorkingGpuRunIsNotRetriedAndCarriesNoNotice()
    {
        var (service, opened, _, wav) = Rig(gpuFails: false);

        var result = await service.SeparateAsync(wav, Path.Combine(_dir, "out"), null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.GpuRunFellBackToCpu);
        Assert.Equal([OnnxExecutionProvider.DirectML], opened);
    }
}
