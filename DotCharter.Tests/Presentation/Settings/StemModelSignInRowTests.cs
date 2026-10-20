using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
/// #267: Settings ▸ Stem models lists the 13 s BS-RoFormer SW, hosted on the app's private
/// releases. Signed out, its download cannot work, so the row must say so — a disabled verb
/// with the reason printed — and never offer a live Download that fails. Signing in (Settings
/// ▸ Updates) turns it live without reopening the screen.
/// </summary>
public sealed class StemModelSignInRowTests : IDisposable
{
    private const string Sw13 = "bs-roformer-sw-6s-13s";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharter_StemSignIn_" + Guid.NewGuid().ToString("N"));
    private readonly Assets _assets = new();

    public StemModelSignInRowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private StemModelManager Manager() =>
        new(new FakeConfigurationService(), logger: null, modelsDirectoryOverride: _dir, releaseAssets: _assets);

    [Fact]
    public void SignedOut_The13sRow_IsVisiblyUnavailable_WithItsReason()
    {
        using var vm = StemModelsRig.Create(Manager());
        var row = vm.Models.Single(r => r.Descriptor.Id == Sw13);

        Assert.False(row.CanDownload);
        Assert.True(row.IsDownloadUnavailable);
        Assert.Equal("Sign in to GitHub in Settings ▸ Updates to download this model.", row.DownloadUnavailableReason);

        // Every public row stays live.
        Assert.All(vm.Models.Where(r => r.Descriptor.ReleaseAsset is null), r =>
        {
            Assert.True(r.CanDownload, r.Descriptor.Id);
            Assert.False(r.IsDownloadUnavailable);
            Assert.Equal("", r.DownloadUnavailableReason);
        });
    }

    /// <summary>The command is not a back door: pressed anyway, it starts nothing.</summary>
    [Fact]
    public async Task SignedOut_TheDownloadCommand_DoesNothing()
    {
        using var vm = StemModelsRig.Create(Manager());
        var row = vm.Models.Single(r => r.Descriptor.Id == Sw13);

        await row.DownloadCommand.ExecuteAsync(null);

        Assert.Empty(_assets.Calls);
        Assert.False(row.IsDownloading);
        Assert.Equal("", row.ProgressText);
    }

    [Fact]
    public async Task SigningIn_TurnsTheRowLive_WithoutReopeningTheScreen()
    {
        using var vm = StemModelsRig.Create(Manager());

        _assets.IsSignedIn = true;
        _assets.Raise();
        var row = vm.Models.Single(r => r.Descriptor.Id == Sw13);

        Assert.True(row.CanDownload);
        Assert.False(row.IsDownloadUnavailable);
        Assert.Equal("", row.DownloadUnavailableReason);

        await row.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(Sw13, Assert.Single(_assets.Calls));
        Assert.True(vm.Models.Single(r => r.Descriptor.Id == Sw13).IsInstalled);
    }

    /// <summary>An installed 13 s file is just installed: no reason, Active and Delete as usual.</summary>
    [Fact]
    public void AnInstalled13s_ShowsNoReason_SignedOutOrNot()
    {
        var manager = Manager();
        File.WriteAllBytes(manager.GetLocalPath(StemModelCatalog.FindById(Sw13)!), new byte[16]);
        using var vm = StemModelsRig.Create(manager);
        var row = vm.Models.Single(r => r.Descriptor.Id == Sw13);

        Assert.False(row.IsDownloadUnavailable);
        Assert.True(row.CanSetActive);
        Assert.True(row.CanDelete);
    }

    private sealed class Assets : IGitHubReleaseAssetDownloader
    {
        public bool RequiresSignIn => true;

        public bool IsSignedIn { get; set; }

        public List<string> Calls { get; } = [];

        public event Action? SignInChanged;

        public void Raise() => SignInChanged?.Invoke();

        public Task<string> DownloadVerifiedAsync(GitHubReleaseAssetRef asset, string destinationPath, string sha256,
            long exactBytes, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            Calls.Add(Path.GetFileNameWithoutExtension(destinationPath) == "bs_roformer_sw_6stem_13s_fp16" ? Sw13 : asset.AssetName);
            File.WriteAllBytes(destinationPath, new byte[16]);
            return Task.FromResult(destinationPath);
        }
    }
}
