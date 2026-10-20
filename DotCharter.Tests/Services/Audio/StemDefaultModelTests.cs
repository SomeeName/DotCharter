using System.Security.Cryptography;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Audio.StemSeparation.Onnx;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Onnx;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #258 + #267: the default stem model. On a GPU with 8 GB the default is BS-RoFormer SW at
/// its 13.35 s training chunk, which is a release asset of the app's PRIVATE repository, so
/// DOWNLOADING it needs the GitHub sign-in; without one (or when its download fails) the 4 s
/// SW from Hugging Face on a 6 GB GPU, else htdemucs_6s. "On the default" is an EMPTY Active
/// model id; an explicit choice is never changed; nothing downloads unasked; a default user who
/// already separates with the 4 s SW or htdemucs_6s keeps it until they agree to the 13 s one.
/// </summary>
public sealed class StemDefaultModelTests : IDisposable
{
    private const string Sw13 = "bs-roformer-sw-6s-13s";
    private const string Sw4 = "bs-roformer-sw-6s";
    private const string Ht = "htdemucs-6s-onnx";

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "DotCharter_StemDefault_" + Guid.NewGuid().ToString("N"));

    public StemDefaultModelTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => BestEffortDelete.Directory(_tmpDir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ what "default" means

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("htdemucs-6s-onnx", false)]
    [InlineData("htdemucs-6s", false)]
    [InlineData("bs-roformer-sw-6s", false)]
    [InlineData("bs-roformer-sw-6s-13s", false)]
    public void OnlyAnEmptyActiveId_IsTheDefault(string? active, bool expected) =>
        Assert.Equal(expected, StemDefaultModel.IsOnDefault(active));

    /// <summary>The 13 s SW peaked at ~5.2 GB above idle, the 4 s at ~4.1 GB, htdemucs_6s at
    /// ~2.5 GB: 8 GB for the first, 6 GB for the second, the rest and the CPU get htdemucs. A
    /// card counts by its NOMINAL size: DXGI reads it short (the two real cards below).</summary>
    [Theory]
    [InlineData(OnnxExecutionProvider.DirectML, 16_771_973_120UL, StemDefaultTier.LargeGpu)] // RTX 5070 Ti 16 GB, as DXGI reads it
    [InlineData(OnnxExecutionProvider.DirectML, 10_539_237_376UL, StemDefaultTier.LargeGpu)] // RTX 3080 10 GB, as DXGI reads it
    [InlineData(OnnxExecutionProvider.DirectML, 8UL << 30, StemDefaultTier.LargeGpu)]
    [InlineData(OnnxExecutionProvider.DirectML, (8UL << 30) - (300UL << 20), StemDefaultTier.LargeGpu)] // an 8 GB card read short
    [InlineData(OnnxExecutionProvider.DirectML, (8UL << 30) - (512UL << 20) - 1, StemDefaultTier.Gpu)]
    [InlineData(OnnxExecutionProvider.DirectML, 6UL << 30, StemDefaultTier.Gpu)]
    [InlineData(OnnxExecutionProvider.DirectML, (6UL << 30) - (300UL << 20), StemDefaultTier.Gpu)]   // a 6 GB card read short
    [InlineData(OnnxExecutionProvider.DirectML, (6UL << 30) - (512UL << 20) - 1, StemDefaultTier.Cpu)]
    [InlineData(OnnxExecutionProvider.DirectML, 4UL << 30, StemDefaultTier.Cpu)]
    [InlineData(OnnxExecutionProvider.Cpu, 16UL << 30, StemDefaultTier.Cpu)]
    public void TheTier_FollowsTheProviderAndTheVideoMemory(OnnxExecutionProvider provider, ulong vram, StemDefaultTier expected)
    {
        var plan = new StemExecutionPlan(provider, 0, StemExecutionReason.DirectMl,
            new StemGpuAdapter(0, "card", vram, IsSoftware: false));
        Assert.Equal(expected, StemDefaultModel.TierFor(plan));
    }

    [Fact]
    public void AnUnknownAdapter_CountsByTheProviderAlone()
    {
        Assert.Equal(StemDefaultTier.LargeGpu, StemDefaultModel.TierFor(
            new StemExecutionPlan(OnnxExecutionProvider.DirectML, 0, StemExecutionReason.DirectMl, null)));
        Assert.Equal(StemDefaultTier.Cpu, StemDefaultModel.TierFor(
            new StemExecutionPlan(OnnxExecutionProvider.Cpu, 0, StemExecutionReason.UserChoseCpu, null)));
    }

    [Fact]
    public void TheLadder_BestFirst_AndThe13sOnlyWhereItFits()
    {
        Assert.Equal([Sw13, Sw4, Ht], StemDefaultModel.Preference(StemDefaultTier.LargeGpu));
        Assert.Equal([Sw4, Ht], StemDefaultModel.Preference(StemDefaultTier.Gpu));
        Assert.Equal([Ht, Sw4], StemDefaultModel.Preference(StemDefaultTier.Cpu));
    }

    /// <summary>THE selection matrix: what a fresh default user is offered, by tier and
    /// sign-in. The 13 s model needs the sign-in to DOWNLOAD; the rest are public.</summary>
    [Theory]
    [InlineData(StemDefaultTier.LargeGpu, true, Sw13, Sw4)]
    [InlineData(StemDefaultTier.LargeGpu, false, Sw4, Ht)]
    [InlineData(StemDefaultTier.Gpu, true, Sw4, Ht)]
    [InlineData(StemDefaultTier.Gpu, false, Sw4, Ht)]
    [InlineData(StemDefaultTier.Cpu, true, Ht, Sw4)]
    [InlineData(StemDefaultTier.Cpu, false, Ht, Sw4)]
    public void TheFirstDownload_ByTierAndSignIn(StemDefaultTier tier, bool signedIn, string first, string fallback) =>
        Assert.Equal([first, fallback], StemDefaultModel.DownloadChain(tier, id => signedIn || id != Sw13));

    /// <summary>Running needs no sign-in: an installed 13 s file runs for a signed-out user.</summary>
    [Fact]
    public void ASeparationUsesTheFirstInstalledModel_AndNeverDownloads()
    {
        Assert.Equal(Sw13, StemDefaultModel.FirstInstalled(StemDefaultTier.LargeGpu, _ => true));
        Assert.Equal(Sw4, StemDefaultModel.FirstInstalled(StemDefaultTier.LargeGpu, i => i != Sw13));
        Assert.Equal(Sw4, StemDefaultModel.FirstInstalled(StemDefaultTier.Gpu, _ => true));
        Assert.Equal(Ht, StemDefaultModel.FirstInstalled(StemDefaultTier.Cpu, _ => true));
        Assert.Equal(Ht, StemDefaultModel.FirstInstalled(StemDefaultTier.LargeGpu, i => i == Ht));
        Assert.Null(StemDefaultModel.FirstInstalled(StemDefaultTier.Gpu, i => i == Sw13));
        Assert.Null(StemDefaultModel.FirstInstalled(StemDefaultTier.LargeGpu, _ => false));
    }

    /// <summary>The migration matrix in pure form: who is offered the 13 s model over the
    /// default they already run.</summary>
    [Theory]
    [InlineData(StemDefaultTier.LargeGpu, Sw4, true, "", Sw13)]
    [InlineData(StemDefaultTier.LargeGpu, Ht, true, "", Sw13)]
    [InlineData(StemDefaultTier.LargeGpu, Sw4, true, Sw13, null)]       // said no before
    [InlineData(StemDefaultTier.LargeGpu, Sw4, false, "", null)]        // not signed in
    [InlineData(StemDefaultTier.LargeGpu, Sw13, true, "", null)]        // already has it
    [InlineData(StemDefaultTier.LargeGpu, "", true, "", null)]          // nothing installed: the first-download prompt
    [InlineData(StemDefaultTier.Gpu, Sw4, true, "", null)]              // would not fit
    [InlineData(StemDefaultTier.Cpu, Ht, true, "", null)]
    public void TheUpgrade_IsOfferedOnlyToAnInstalledDefaultOnALargeGpu(
        StemDefaultTier tier, string installed, bool signedIn, string declined, string? expected) =>
        Assert.Equal(expected, StemDefaultModel.UpgradeTarget(tier, id => id == installed,
            id => signedIn || id != Sw13, declined));

    [Fact]
    public async Task AnAgreedDownload_TakesTheChainInOrder()
    {
        var installed = new HashSet<string>();
        var downloads = new List<string>();
        var id = await StemDefaultModel.DownloadFirstAsync([Ht, Sw4], installed.Contains,
            (i, _) => { downloads.Add(i); installed.Add(i); return Task.CompletedTask; }, null, Ct);

        Assert.Equal(Ht, id);
        Assert.Equal([Ht], downloads);
    }

    /// <summary>The 13 s download fails (no access, the pin rejects the bytes): the agreed
    /// download takes the 4 s SW the prompt also named.</summary>
    [Fact]
    public async Task AFailedDownload_FallsBackToTheNextModel_AndSaysWhy()
    {
        var installed = new HashSet<string>();
        var failures = new List<string>();
        var id = await StemDefaultModel.DownloadFirstAsync([Sw13, Sw4], installed.Contains,
            (i, _) =>
            {
                if (i == Sw13) throw new InvalidDataException("hash mismatch");
                installed.Add(i);
                return Task.CompletedTask;
            },
            (i, ex) => failures.Add($"{i}: {ex.Message}"), Ct);

        Assert.Equal(Sw4, id);
        Assert.Equal([$"{Sw13}: hash mismatch"], failures);
    }

    [Fact]
    public async Task ACancelledDownload_Propagates_AndDoesNotStartTheFallback()
    {
        var downloads = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => StemDefaultModel.DownloadFirstAsync([Sw13, Sw4], _ => false,
            (i, _) => { downloads.Add(i); throw new OperationCanceledException(); }, null, Ct));
        Assert.Equal([Sw13], downloads);
    }

    [Fact]
    public async Task NothingObtainable_IsNull() =>
        Assert.Null(await StemDefaultModel.DownloadFirstAsync([Sw4, Ht], _ => false,
            (_, _) => throw new HttpRequestException("offline"), null, Ct));

    // ------------------------------------------------------------ the catalogue rows

    /// <summary>All three defaults are catalogue rows the ONNX backend can run: six-stem,
    /// pinned, and for SW a BS-RoFormer layout the JuceSharp driver binds.</summary>
    [Fact]
    public void EveryDefault_IsARunnableSixStemCatalogueRow()
    {
        foreach (var id in new[] { StemDefaultModel.LargeGpuDefaultId, StemDefaultModel.GpuDefaultId, StemDefaultModel.CpuDefaultId })
        {
            var d = StemModelCatalog.FindById(id);
            Assert.NotNull(d);
            Assert.Equal(6, d.SourceCount);
            Assert.Matches("^[0-9a-f]{64}$", d.Sha256!);
            Assert.True(d.ExactBytes > 0);
        }

        Assert.Equal(StemModelKind.HtdemucsOnnx, StemModelCatalog.FindById(Ht)!.Kind);
        foreach (var sw in new[] { Sw13, Sw4 })
        {
            var d = StemModelCatalog.FindById(sw)!;
            Assert.Equal(StemModelKind.BsRoformerOnnx, d.Kind);
            Assert.Contains(d.Onnx!.BsRoformerLayout!.Value, BsRoformerIoBinding.SupportedLayouts);
        }

        Assert.Equal("Settings_StemModels_Desc_BsRoformerSw6s", StemModelCatalog.FindById(Sw4)!.DescriptionKey);
    }

    /// <summary>The 13 s row is the file that was uploaded, published where the app's own
    /// releases are, and pinned to the bytes the export tool writes.</summary>
    [Fact]
    public void The13sRow_IsTheHostedExport_PinnedToItsBytes()
    {
        var d = StemModelCatalog.FindById(Sw13)!;

        Assert.Equal("bs_roformer_sw_6stem_13s_fp16.onnx", d.FileName);
        Assert.Equal(357_936_365, d.ExactBytes);
        Assert.Equal("2fd77c88e3fb049eed18d7e3b3c1dbf3c401f61e9c04e58375c557a5a27157fe", d.Sha256);
        Assert.Equal(new GitHubReleaseAssetRef("models-bs-roformer-sw-v1", d.FileName), d.ReleaseAsset);
        Assert.Equal(BsRoformerIoLayout.SplitComplex, d.Onnx!.BsRoformerLayout);
        Assert.Equal(d.Sha256, d.Onnx.Sha256);
        Assert.Equal(d.ExactBytes, d.Onnx.SizeBytes);
        Assert.Equal("Settings_StemModels_Desc_BsRoformerSw6s13s", d.DescriptionKey);
        // Not JuceSharp's: the submodule's downloader cannot sign in, so the row is the app's.
        Assert.Null(OnnxModelCatalog.FindById(Sw13));

        // Listed right after the 4 s row it improves on.
        var ids = StemModelCatalog.BuiltIn.Select(r => r.Id).ToList();
        Assert.Equal(ids.IndexOf(Sw4) + 1, ids.IndexOf(Sw13));
    }

    /// <summary>Every BS-RoFormer row the app offers declares a layout the driver binds —
    /// the shape of the MrCitron rows #254 removed, which downloaded and never ran.</summary>
    [Fact]
    public void EveryBsRoformerRow_DeclaresALayoutTheDriverSupports()
    {
        var rows = StemModelCatalog.BuiltIn.Where(d => d.Kind == StemModelKind.BsRoformerOnnx).ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, d =>
        {
            Assert.NotNull(d.Onnx?.BsRoformerLayout);
            Assert.Contains(d.Onnx!.BsRoformerLayout!.Value, BsRoformerIoBinding.SupportedLayouts);
        });
    }

    // ------------------------------------------------------------ through the real services

    private (StemModelManager Manager, FakeConfigurationService Config, FakeReleaseAssets Assets) Build(
        string active = "", string provider = "Auto", bool signedIn = true)
    {
        var cfg = new FakeConfigurationService();
        cfg.AppConfig.HtDemucsActiveModelId = active;
        cfg.AppConfig.StemSeparationExecutionProvider = provider;
        var assets = new FakeReleaseAssets { IsSignedIn = signedIn };
        return (new StemModelManager(cfg, logger: null, modelsDirectoryOverride: _tmpDir, releaseAssets: assets), cfg, assets);
    }

    private void FakeInstall(StemModelManager manager, string id) =>
        File.WriteAllBytes(manager.GetLocalPath(StemModelCatalog.FindById(id)!), new byte[16]);

    private static FakeGpu Gpu(ulong vram) => new([new StemGpuAdapter(0, "card", vram, IsSoftware: false)]);

    /// <summary>A fresh signed-in user on a large GPU is asked about the 13 s model, with the
    /// 4 s one named as the fallback. Nothing is on disk until they say yes.</summary>
    [Fact]
    public async Task AFreshSignedInUser_OnALargeGpu_IsOffered13s_AndNothingDownloadsUnasked()
    {
        var (manager, _, assets) = Build();
        var onnx = new OnnxStemSeparationService(manager, gpu: Gpu(16UL << 30));

        Assert.False(await onnx.IsAvailableAsync(Ct));
        Assert.Contains($"offers to download '{Sw13}'", await onnx.DescribeBackendAsync(Ct), StringComparison.Ordinal);

        var offer = await new StemDefaultModelProvisioner(manager, Gpu(16UL << 30)).GetOfferAsync(Ct);
        Assert.NotNull(offer);
        Assert.False(offer.IsUpgrade);
        Assert.Equal(Sw13, offer.ModelId);
        Assert.Equal(357_936_365, offer.Bytes);
        Assert.Equal(Sw4, offer.FallbackId);
        Assert.Equal([Sw13, Sw4], offer.Chain);
        Assert.Empty(Directory.EnumerateFiles(_tmpDir));
        Assert.Empty(assets.Calls);
    }

    [Fact]
    public async Task AFreshSignedOutUser_OnALargeGpu_IsOffered4s_WithHtdemucsBehindIt()
    {
        var (manager, _, _) = Build(signedIn: false);
        var offer = await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct);
        Assert.NotNull(offer);
        Assert.Equal(Sw4, offer.ModelId);
        Assert.Equal(352_778_874, offer.Bytes);
        Assert.Equal(Ht, offer.FallbackId);
    }

    [Theory]
    [InlineData(8UL << 30, Sw13)]
    [InlineData(7UL << 30, Sw4)]
    [InlineData(4UL << 30, Ht)]
    public async Task TheOffer_FollowsTheVideoMemory(ulong vram, string expected)
    {
        var (manager, _, _) = Build();
        var offer = await new StemDefaultModelProvisioner(manager, Gpu(vram)).GetOfferAsync(Ct);
        Assert.Equal(expected, offer?.ModelId);
    }

    [Fact]
    public async Task OnTheCpu_TheOfferIsHtdemucs6s_SignedInOrNot()
    {
        var (manager, _, _) = Build(provider: "Cpu");
        var offer = await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct);
        Assert.NotNull(offer);
        Assert.Equal(Ht, offer.ModelId);
        Assert.Equal([Ht, Sw4], offer.Chain);
    }

    /// <summary>The agreed 13 s download goes through the signed-in release download with the
    /// row's own pin, lands in the models folder and is NOT made Active.</summary>
    [Fact]
    public async Task TheAgreed13sDownload_UsesTheSignedInReleaseAsset_AndStaysOnTheDefault()
    {
        var (manager, cfg, assets) = Build();
        var provisioner = new StemDefaultModelProvisioner(manager);
        var offer = (await provisioner.GetOfferAsync(Ct))!;

        Assert.Equal(Sw13, await provisioner.DownloadAsync(offer, null, Ct));

        var call = Assert.Single(assets.Calls);
        Assert.Equal(new GitHubReleaseAssetRef("models-bs-roformer-sw-v1", "bs_roformer_sw_6stem_13s_fp16.onnx"), call.Asset);
        Assert.Equal(Path.Combine(_tmpDir, "bs_roformer_sw_6stem_13s_fp16.onnx"), call.Path);
        Assert.Equal("2fd77c88e3fb049eed18d7e3b3c1dbf3c401f61e9c04e58375c557a5a27157fe", call.Sha256);
        Assert.Equal(357_936_365, call.Bytes);
        Assert.Equal("", cfg.AppConfig.HtDemucsActiveModelId);
        Assert.Contains("model='bs_roformer_sw_6stem_13s_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the migration matrix

    /// <summary>An existing default user with the 4 s SW keeps separating with it, and is asked
    /// ONCE about the 13 s model — an upgrade offer naming what they keep on a no.</summary>
    [Fact]
    public async Task ADefaultUserWith4s_KeepsIt_AndIsOfferedTheUpgrade()
    {
        var (manager, cfg, assets) = Build();
        FakeInstall(manager, Sw4);
        FakeInstall(manager, Ht);

        Assert.Contains("model='bs_roformer_sw_6stem_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
        var offer = await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct);

        Assert.NotNull(offer);
        Assert.True(offer.IsUpgrade);
        Assert.Equal(Sw13, offer.ModelId);
        Assert.Equal(Sw4, offer.FallbackId);
        Assert.Equal([Sw13], offer.Chain);
        Assert.Equal("", cfg.AppConfig.HtDemucsActiveModelId);
        Assert.Empty(assets.Calls);
    }

    [Fact]
    public async Task ADefaultUserWithHtdemucs_KeepsIt_AndIsOfferedTheUpgrade()
    {
        var (manager, _, _) = Build();
        FakeInstall(manager, Ht);

        var offer = await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct);
        Assert.True(offer!.IsUpgrade);
        Assert.Equal(Ht, offer.FallbackId);
    }

    /// <summary>A no is remembered: the 4 s SW keeps separating, nothing becomes Active, and
    /// the next Separate Stems does not ask again.</summary>
    [Fact]
    public async Task ADeclinedUpgrade_IsRemembered_AndThe4sKeepsSeparating()
    {
        var (manager, cfg, _) = Build();
        FakeInstall(manager, Sw4);
        var provisioner = new StemDefaultModelProvisioner(manager);

        provisioner.DeclineUpgrade((await provisioner.GetOfferAsync(Ct))!);

        Assert.Null(await provisioner.GetOfferAsync(Ct));
        Assert.Equal(Sw13, cfg.AppConfig.StemDefaultUpgradeDeclinedId);
        Assert.Equal("", cfg.AppConfig.HtDemucsActiveModelId);
        Assert.Contains("model='bs_roformer_sw_6stem_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>An upgrade whose download fails leaves the user exactly where they were.</summary>
    [Fact]
    public async Task AFailedUpgrade_KeepsTheInstalledDefault()
    {
        var (manager, cfg, assets) = Build();
        FakeInstall(manager, Sw4);
        assets.Fail = new HttpRequestException("404");
        var provisioner = new StemDefaultModelProvisioner(manager);

        Assert.Null(await provisioner.DownloadAsync((await provisioner.GetOfferAsync(Ct))!, null, Ct));
        Assert.Contains("model='bs_roformer_sw_6stem_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("", cfg.AppConfig.HtDemucsActiveModelId);
        Assert.False(manager.IsInstalled(Sw13));
    }

    /// <summary>Accepting the upgrade switches the default user to the 13 s model, still
    /// without an explicit choice.</summary>
    [Fact]
    public async Task AnAcceptedUpgrade_Runs13s_AndStaysOnTheDefault()
    {
        var (manager, cfg, _) = Build();
        FakeInstall(manager, Sw4);
        var provisioner = new StemDefaultModelProvisioner(manager);

        Assert.Equal(Sw13, await provisioner.DownloadAsync((await provisioner.GetOfferAsync(Ct))!, null, Ct));

        Assert.Null(await provisioner.GetOfferAsync(Ct));
        Assert.Contains("model='bs_roformer_sw_6stem_13s_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("", cfg.AppConfig.HtDemucsActiveModelId);
    }

    [Fact]
    public async Task ASignedOutDefaultUserWith4s_IsNotAsked()
    {
        var (manager, _, _) = Build(signedIn: false);
        FakeInstall(manager, Sw4);
        Assert.Null(await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct));
    }

    /// <summary>The 13 s file on disk runs whether or not anybody is signed in.</summary>
    [Fact]
    public async Task AnInstalled13s_RunsSignedOut()
    {
        var (manager, _, _) = Build(signedIn: false);
        FakeInstall(manager, Sw13);
        FakeInstall(manager, Sw4);

        Assert.Contains("model='bs_roformer_sw_6stem_13s_fp16.onnx'",
            await new OnnxStemSeparationService(manager).DescribeBackendAsync(Ct), StringComparison.Ordinal);
        Assert.Null(await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct));
    }

    /// <summary>A 7 GB card with only the 13 s file is not handed a model that does not fit.</summary>
    [Fact]
    public async Task OnASixGbGpu_The13sFileIsNotUsed()
    {
        var (manager, _, _) = Build();
        FakeInstall(manager, Sw13);
        var offer = await new StemDefaultModelProvisioner(manager, Gpu(7UL << 30)).GetOfferAsync(Ct);
        Assert.Equal(Sw4, offer?.ModelId);
    }

    /// <summary>An explicit choice is NEVER changed and never asked about: the 4 s SW chosen
    /// as Active keeps running with the 13 s file on disk and a sign-in in reach.</summary>
    [Theory]
    [InlineData(Sw4, "bs_roformer_sw_6stem_fp16.onnx")]
    [InlineData(Ht, "htdemucs_6s.onnx")]
    public async Task AnExplicitChoice_IsKept_AndNeverAskedAbout(string active, string file)
    {
        var (manager, cfg, _) = Build(active: active);
        FakeInstall(manager, Sw13);
        FakeInstall(manager, Sw4);
        FakeInstall(manager, Ht);
        var onnx = new OnnxStemSeparationService(manager);

        Assert.True(await onnx.IsAvailableAsync(Ct));
        var description = await onnx.DescribeBackendAsync(Ct);
        Assert.Contains($"model='{file}'", description, StringComparison.Ordinal);
        Assert.DoesNotContain("default model", description, StringComparison.Ordinal);
        Assert.Null(await new StemDefaultModelProvisioner(manager).GetOfferAsync(Ct));
        Assert.Equal(active, cfg.AppConfig.HtDemucsActiveModelId);
    }

    // ------------------------------------------------------------ the manager's sign-in gate

    [Fact]
    public async Task Without_A_SignIn_The13sIsBlocked_AndItsDownloadTouchesNothing()
    {
        var (manager, _, assets) = Build(signedIn: false);

        Assert.Equal(StemModelDownloadBlock.NeedsGitHubSignIn, manager.DownloadBlock(Sw13));
        Assert.Equal(StemModelDownloadBlock.None, manager.DownloadBlock(Sw4));
        Assert.Equal(StemModelDownloadBlock.None, manager.DownloadBlock(Ht));
        await Assert.ThrowsAsync<GitHubSignInRequiredException>(() => manager.DownloadAsync(Sw13, ct: Ct));
        Assert.Empty(assets.Calls);
        Assert.Empty(Directory.EnumerateFiles(_tmpDir));

        var noHost = new StemModelManager(new FakeConfigurationService(), modelsDirectoryOverride: _tmpDir);
        Assert.Equal(StemModelDownloadBlock.NeedsGitHubSignIn, noHost.DownloadBlock(Sw13));
    }

    /// <summary>A model on a PUBLIC repository's releases needs no sign-in: the public build
    /// has no sign-in to offer, so nothing may block on one.</summary>
    [Fact]
    public async Task A_PublicReleaseDownload_NeedsNoSignIn()
    {
        var (manager, _, assets) = Build(signedIn: false);
        assets.RequiresSignIn = false;

        Assert.Equal(StemModelDownloadBlock.None, manager.DownloadBlock(Sw13));
        await manager.DownloadAsync(Sw13, ct: Ct);
        Assert.Single(assets.Calls);
    }

    [Fact]
    public void SigningIn_RefreshesEveryListOfModels()
    {
        var (manager, _, assets) = Build(signedIn: false);
        var raised = 0;
        manager.ModelsChanged += (_, _) => raised++;

        assets.IsSignedIn = true;
        assets.RaiseSignInChanged();

        Assert.Equal(1, raised);
        Assert.Equal(StemModelDownloadBlock.None, manager.DownloadBlock(Sw13));
    }

    /// <summary>
    /// A default fetched on demand must not become Active: that would turn "no choice
    /// made" into an explicit choice, and the next change of default would not reach the
    /// user. Driven through the real download path on a file that is already installed
    /// and verifies (so no network is touched).
    /// </summary>
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "self")]
    public async Task ADownloadActivatesOnlyWhenAskedTo(bool activate, string expectedActive)
    {
        var (manager, cfg, _) = Build();
        var id = "test-default-" + Guid.NewGuid().ToString("N");
        var file = id + ".onnx";
        var bytes = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(Path.Combine(_tmpDir, file), bytes);
        StemModelCatalog.RegisterCustom(new StemModelDescriptor
        {
            Id = id,
            DisplayName = "test",
            Description = "test",
            DownloadUrl = "https://example.invalid/" + file,
            FileName = file,
            ExactBytes = bytes.Length,
            ApproxBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            HashSource = "test",
            Kind = StemModelKind.HtdemucsOnnx,
        });
        try
        {
            await manager.DownloadAsync(id, ct: Ct, activateIfNoneActive: activate);
            Assert.Equal(expectedActive == "self" ? id : expectedActive, cfg.AppConfig.HtDemucsActiveModelId);
        }
        finally
        {
            StemModelCatalog.UnregisterCustom(id);
        }
    }

    private sealed class FakeGpu(IReadOnlyList<StemGpuAdapter> adapters) : IStemGpuEnvironment
    {
        public bool IsDirectMlAvailable => true;

        public IReadOnlyList<StemGpuAdapter> Adapters() => adapters;
    }

    /// <summary>The signed-in release download, recorded; "downloads" a stand-in file.</summary>
    private sealed class FakeReleaseAssets : IGitHubReleaseAssetDownloader
    {
        public bool RequiresSignIn { get; set; } = true;

        public bool IsSignedIn { get; set; }

        public Exception? Fail { get; set; }

        public List<(GitHubReleaseAssetRef Asset, string Path, string Sha256, long Bytes)> Calls { get; } = [];

        public event Action? SignInChanged;

        public void RaiseSignInChanged() => SignInChanged?.Invoke();

        public Task<string> DownloadVerifiedAsync(GitHubReleaseAssetRef asset, string destinationPath, string sha256,
            long exactBytes, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            Calls.Add((asset, destinationPath, sha256, exactBytes));
            if (Fail != null) throw Fail;
            File.WriteAllBytes(destinationPath, new byte[16]);
            return Task.FromResult(destinationPath);
        }
    }
}
