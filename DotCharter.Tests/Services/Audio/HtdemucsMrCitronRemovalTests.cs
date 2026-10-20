using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #254: MrCitron's <c>htdemucs.onnx</c> / <c>htdemucs_ft.onnx</c> could be
/// downloaded and set Active but never separated, and once JuceSharp could drive
/// their single-input layout they measured defective (stems summing to 63–69 %
/// of the mix at correlation 0.81–0.84, against the 6-stem export's 100 % /
/// 0.999). Both left the catalogue. What is pinned here: they cannot come back
/// through the app, a ggml <c>htdemucs-4s</c> user on a GPU is no longer
/// promoted to one, and a user who already HAS one Active falls back cleanly.
/// </summary>
public sealed class HtdemucsMrCitronRemovalTests : IDisposable
{
    private static readonly string[] RemovedIds = ["htdemucs-4s-onnx", "htdemucs-ft-onnx"];
    private static readonly string[] RemovedFiles = ["htdemucs.onnx", "htdemucs_ft.onnx"];

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "DotCharter_MrCitron_" + Guid.NewGuid().ToString("N"));

    public HtdemucsMrCitronRemovalTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => BestEffortDelete.Directory(_tmpDir);

    [Fact]
    public void Neither_MrCitron_export_is_offered()
    {
        foreach (var id in RemovedIds)
            Assert.Null(StemModelCatalog.FindById(id));
        Assert.DoesNotContain(StemModelCatalog.BuiltIn, d => RemovedFiles.Contains(d.FileName, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_ggml_four_stem_user_on_a_GPU_is_not_promoted()
    {
        Assert.Null(OnnxStemSeparationService.MapGgmlToOnnxTwin("htdemucs-4s"));
        Assert.Equal("htdemucs-6s-onnx", OnnxStemSeparationService.MapGgmlToOnnxTwin("htdemucs-6s"));
    }

    /// <summary>A promotion target that is not in the catalogue is a download nobody can start.</summary>
    [Fact]
    public void Every_promotion_target_is_a_catalogue_htdemucs_ONNX_model()
    {
        foreach (var ggml in StemModelCatalog.BuiltIn.Where(d => d.Kind == StemModelKind.DemucsGgml))
        {
            if (OnnxStemSeparationService.MapGgmlToOnnxTwin(ggml.Id) is not { } twinId) continue;
            var twin = StemModelCatalog.FindById(twinId);
            Assert.NotNull(twin);
            Assert.Equal(StemModelKind.HtdemucsOnnx, twin!.Kind);
            Assert.Contains(twin.Onnx!.HtdemucsLayout!.Value, HtdemucsIoBinding.SupportedLayouts);
        }
    }

    /// <summary>
    /// The user who downloaded one and set it Active before this change: the id
    /// is persisted and the file is still on disk. The ONNX backend soft-skips
    /// (no throw, nothing loaded) and the active-path resolution moves on to a
    /// model that IS in the catalogue — the stale file is never handed out.
    /// </summary>
    [Fact]
    public async Task A_user_with_a_removed_model_Active_falls_back_cleanly()
    {
        var cfg = new FakeConfigurationService();
        var mgr = new StemModelManager(cfg, logger: null, modelsDirectoryOverride: _tmpDir);
        File.WriteAllBytes(Path.Combine(_tmpDir, "htdemucs.onnx"), new byte[16]);
        var installed = StemModelCatalog.FindById("htdemucs-6s-onnx")!;
        File.WriteAllBytes(mgr.GetLocalPath(installed), new byte[16]);
        mgr.ActiveModelId = "htdemucs-4s-onnx";

        Assert.Equal(mgr.GetLocalPath(installed), mgr.ResolveActiveModelPath());

        var onnx = new OnnxStemSeparationService(mgr);
        Assert.False(await onnx.IsAvailableAsync(TestContext.Current.CancellationToken));
        var description = await onnx.DescribeBackendAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("htdemucs-4s-onnx", description, StringComparison.Ordinal);
    }
}
