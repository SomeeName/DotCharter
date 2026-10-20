using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// Behavioural tests for <see cref="StemModelManager"/>: catalog lookup,
/// install detection, delete-clears-active wiring, active-model fallback
/// chain, and the partial-file rename invariant.
/// </summary>
public sealed class StemModelManagerTests : IDisposable
{
    private readonly string _tmpDir;

    public StemModelManagerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "DotCharter_StemModelMgr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tmpDir);

    private StemModelManager Build(FakeConfigurationService? cfg = null)
    {
        cfg ??= new FakeConfigurationService();
        return new StemModelManager(cfg, logger: null, modelsDirectoryOverride: _tmpDir);
    }

    [Fact]
    public void Catalog_exposes_known_htdemucs_variants()
    {
        var mgr = Build();
        Assert.Contains(mgr.Catalog, m => m.Id == "htdemucs-4s");
        Assert.Contains(mgr.Catalog, m => m.Id == "htdemucs-6s");
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        Assert.All(mgr.Catalog, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.DownloadUrl));
            Assert.False(string.IsNullOrWhiteSpace(m.FileName));
            Assert.True(m.ApproxBytes > 0);
        });
    }

    [Fact]
    public void FindById_is_case_insensitive_and_returns_null_for_unknown()
    {
        Assert.Equal("htdemucs-4s", StemModelCatalog.FindById("HTDEMUCS-4S")!.Id);
        Assert.Null(StemModelCatalog.FindById("does-not-exist"));
        Assert.Null(StemModelCatalog.FindById(null));
        Assert.Null(StemModelCatalog.FindById(""));
    }

    [Fact]
    public void IsInstalled_reflects_disk_state()
    {
        var mgr = Build();
        var desc = mgr.Catalog[0];

        Assert.False(mgr.IsInstalled(desc.Id));

        File.WriteAllBytes(mgr.GetLocalPath(desc), new byte[16]);
        Assert.True(mgr.IsInstalled(desc.Id));

        Assert.Equal(16, mgr.GetInstalledSize(desc.Id));
    }

    [Fact]
    public void GetInstalledSize_returns_zero_when_missing()
    {
        var mgr = Build();
        Assert.Equal(0, mgr.GetInstalledSize("htdemucs-4s"));
        Assert.Equal(0, mgr.GetInstalledSize("not-a-model"));
    }

    [Fact]
    public void ResolveActiveModelPath_prefers_active_when_installed()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);
        var first = mgr.Catalog[0];
        var second = mgr.Catalog[1];

        File.WriteAllBytes(mgr.GetLocalPath(first), new byte[8]);
        File.WriteAllBytes(mgr.GetLocalPath(second), new byte[8]);
        mgr.ActiveModelId = second.Id;

        Assert.Equal(mgr.GetLocalPath(second), mgr.ResolveActiveModelPath());
    }

    [Fact]
    public void ResolveActiveModelPath_falls_back_to_first_installed_when_active_missing()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);
        var second = mgr.Catalog[1];

        File.WriteAllBytes(mgr.GetLocalPath(second), new byte[8]);
        mgr.ActiveModelId = "ghost-model"; // not installed, not in catalog

        Assert.Equal(mgr.GetLocalPath(second), mgr.ResolveActiveModelPath());
    }

    [Fact]
    public void ResolveActiveModelPath_returns_null_when_nothing_installed()
    {
        var mgr = Build();
        Assert.Null(mgr.ResolveActiveModelPath());
    }

    [Fact]
    public void ActiveModelId_round_trips_through_configuration()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);

        mgr.ActiveModelId = "htdemucs-6s";
        Assert.Equal("htdemucs-6s", mgr.ActiveModelId);
        Assert.Equal("htdemucs-6s", cfg.AppConfig.HtDemucsActiveModelId);
    }

    [Fact]
    public void ExecutionProvider_round_trips_through_configuration_and_defaults_to_auto()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);

        Assert.Equal("Auto", mgr.ExecutionProvider);

        mgr.ExecutionProvider = "DirectML";
        Assert.Equal("DirectML", mgr.ExecutionProvider);
        Assert.Equal("DirectML", cfg.AppConfig.StemSeparationExecutionProvider);

        // Setting the same value again is a no-op but the visible state stays consistent.
        mgr.ExecutionProvider = "DirectML";
        Assert.Equal("DirectML", mgr.ExecutionProvider);

        mgr.ExecutionProvider = "Cpu";
        Assert.Equal("Cpu", mgr.ExecutionProvider);
        Assert.Equal("Cpu", cfg.AppConfig.StemSeparationExecutionProvider);

        // Empty/whitespace falls back to the default.
        mgr.ExecutionProvider = "";
        Assert.Equal("Auto", mgr.ExecutionProvider);
    }

    /// <summary>
    ///     Versions that shipped the CUDA build stored "Cuda" as their automatic
    ///     choice, and this build's ONNX Runtime has no CUDA. A stored "Cuda" reaches
    ///     every separation through this getter, whether or not Settings is ever
    ///     opened, so it is read as the automatic choice (DirectML where a GPU can
    ///     run it) rather than requested and silently degraded.
    /// </summary>
    [Theory]
    [InlineData("Cuda")]
    [InlineData("cuda")]
    [InlineData(" Nvidia ")]
    public void A_stored_Cuda_from_an_older_version_reads_as_Auto(string stored)
    {
        var cfg = new FakeConfigurationService();
        cfg.AppConfig.StemSeparationExecutionProvider = stored;

        Assert.Equal("Auto", Build(cfg).ExecutionProvider);
    }

    [Fact]
    public void TheGpuChoice_RoundTripsThroughConfiguration_AndDefaultsToTheFastest()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);
        Assert.Equal("", mgr.GpuAdapterKey);

        mgr.GpuAdapterKey = " 1:NVIDIA GeForce RTX 3080 ";
        Assert.Equal("1:NVIDIA GeForce RTX 3080", cfg.AppConfig.StemSeparationGpuAdapter);
        Assert.Equal("1:NVIDIA GeForce RTX 3080", mgr.GpuAdapterKey);
    }

    [Fact]
    public void Delete_removes_file_and_clears_active_when_matched()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);
        var desc = mgr.Catalog[0];
        var path = mgr.GetLocalPath(desc);

        File.WriteAllBytes(path, new byte[4]);
        mgr.ActiveModelId = desc.Id;

        var changed = false;
        mgr.ModelsChanged += (_, _) => changed = true;

        mgr.Delete(desc.Id);

        Assert.False(File.Exists(path));
        Assert.Equal(string.Empty, mgr.ActiveModelId);
        Assert.True(changed);
    }

    [Fact]
    public void Delete_does_not_clear_active_when_a_different_model_is_active()
    {
        var cfg = new FakeConfigurationService();
        var mgr = Build(cfg);
        var first = mgr.Catalog[0];
        var second = mgr.Catalog[1];

        File.WriteAllBytes(mgr.GetLocalPath(first), new byte[4]);
        File.WriteAllBytes(mgr.GetLocalPath(second), new byte[4]);
        mgr.ActiveModelId = second.Id;

        mgr.Delete(first.Id);

        Assert.False(File.Exists(mgr.GetLocalPath(first)));
        Assert.Equal(second.Id, mgr.ActiveModelId);
    }

    [Fact]
    public void Delete_is_a_noop_for_unknown_id()
    {
        var mgr = Build();
        // Just make sure it doesn't throw.
        mgr.Delete("not-a-model");
    }

    [Fact]
    public void GetLocalPathIfInstalled_returns_null_when_file_absent()
    {
        var mgr = Build();
        Assert.Null(mgr.GetLocalPathIfInstalled("htdemucs-4s"));
        Assert.Null(mgr.GetLocalPathIfInstalled("nope"));
    }

    [Fact]
    public async Task DownloadAsync_propagates_invalid_id()
    {
        var mgr = Build();
        await Assert.ThrowsAsync<ArgumentException>(
            () => mgr.DownloadAsync("not-a-real-model", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Progress_fraction_clamps_to_unit_interval()
    {
        Assert.Equal(0.0, new ModelDownloadProgress(0, 0).Fraction);
        Assert.Equal(0.0, new ModelDownloadProgress(0, 100).Fraction);
        Assert.Equal(0.5, new ModelDownloadProgress(50, 100).Fraction);
        Assert.Equal(1.0, new ModelDownloadProgress(150, 100).Fraction); // never overshoots
    }

    [Theory]
    [InlineData("htdemucs_6s.onnx", StemModelKind.HtdemucsOnnx, 6)]
    [InlineData("htdemucs_4s.onnx", StemModelKind.HtdemucsOnnx, 4)]
    [InlineData("hdemucs_mmi.onnx", StemModelKind.HtdemucsOnnx, 4)]
    [InlineData("BS-RoFormer_vocals.onnx", StemModelKind.BsRoformerOnnx, 2)]
    [InlineData("Kim_Vocal_2.onnx", StemModelKind.MdxNetOnnx, 2)]
    [InlineData("UVR-MDX-NET-Inst_HQ_3.onnx", StemModelKind.MdxNetOnnx, 2)]
    [InlineData("htdemucs_6s_ggml.bin", StemModelKind.DemucsGgml, 6)]
    public void InferKind_classifies_known_filename_patterns(string fileName, StemModelKind expectedKind, int expectedStems)
    {
        var (kind, stems) = StemModelManager.InferKind(fileName);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedStems, stems);
    }

    [Fact]
    public void ImportFromUrl_registers_custom_model_and_RemoveCustom_unregisters_it()
    {
        var mgr = Build();
        var url = $"https://huggingface.co/test-{Guid.NewGuid():N}/repo/resolve/main/htdemucs_6s.onnx";

        var desc = mgr.ImportFromUrl(url);
        try
        {
            Assert.StartsWith("custom-", desc.Id);
            Assert.Equal(StemModelKind.HtdemucsOnnx, desc.Kind);
            Assert.Equal(6, desc.SourceCount);
            Assert.Equal("htdemucs_6s.onnx", desc.FileName);
            Assert.NotNull(StemModelCatalog.FindById(desc.Id));
            Assert.Contains(mgr.Catalog, m => m.Id == desc.Id);
        }
        finally
        {
            mgr.RemoveCustom(desc.Id);
        }

        Assert.Null(StemModelCatalog.FindById(desc.Id));
    }

    [Fact]
    public void ImportFromUrl_rejects_invalid_urls()
    {
        var mgr = Build();
        Assert.Throws<ArgumentException>(() => mgr.ImportFromUrl(""));
        Assert.Throws<ArgumentException>(() => mgr.ImportFromUrl("not a url"));
        // URL without a recognisable filename (no extension):
        Assert.Throws<ArgumentException>(() => mgr.ImportFromUrl("https://example.com/foo/bar"));
    }

    [Fact]
    public void RemoveCustom_refuses_builtin_entries()
    {
        var mgr = Build();
        Assert.False(mgr.RemoveCustom("htdemucs-4s"));
        Assert.NotNull(StemModelCatalog.FindById("htdemucs-4s"));
    }
}
