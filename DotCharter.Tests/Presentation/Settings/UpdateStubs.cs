using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>The update service, the GitHub auth service, the prompt and the shell seams, as the Updates tests drive them.</summary>
internal sealed class StubUpdateService(UpdateCheckStatus status, UpdateInfo? update = null) : IAppUpdateService
{
    public static readonly UpdateInfo Newer = new(
        new Version(9, 9, 9), "v9.9.9", "notes", "https://example.invalid/release",
        "DotCharter-win-x64.zip", "https://example.invalid/asset", "https://example.invalid/browser", 1234);

    public int Checks { get; private set; }

    public PendingUpdateApply? Pending { get; set; }

    public int Resumed { get; private set; }

    public int Discarded { get; private set; }

    public Version CurrentVersion => new(1, 2, 3);

    public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Task.FromResult(new UpdateCheckResult(status, update, status == UpdateCheckStatus.Failed ? "offline" : null));
    }

    public Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => Task.FromResult("");

    public Task<string?> TryFetchReleaseChecksumsAsync(Version version, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public UpdateApplyBlockReason GetApplyBlockReason() => UpdateApplyBlockReason.None;

    public void ApplyDownloadedUpdate(string zipPath) { }

    public PendingUpdateApply? GetPendingUpdateApply() => Pending;

    public void ResumePendingUpdateApply() => Resumed++;

    public void DiscardPendingUpdateApply()
    {
        Discarded++;
        Pending = null;
    }
}

internal sealed class StubUpdatePrompt : IUpdatePrompt
{
    public UpdateInfo? PendingUpdate { get; private set; }

    public int Opened { get; private set; }

    public void Prime(UpdateInfo update) => PendingUpdate = update;

    public void Open(UpdateInfo update)
    {
        PendingUpdate = update;
        Opened++;
    }

    public void ClearPending() => PendingUpdate = null;
}

internal sealed class StubGitHubAuth : IGitHubAuthService
{
    public bool IsSignedIn { get; private set; }

    public string? Login { get; set; }

    public bool DeviceFlowSupported { get; set; } = true;

    public event Action? AuthStateChanged;

    public string? GetToken() => IsSignedIn ? "token" : null;

    public string? LastClientId { get; private set; }

    public Task<GitHubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken = default) =>
        (LastClientId = clientId) is not null && DeviceFlowSupported
            ? Task.FromResult(new GitHubDeviceCode("device", "WDJB-MJHT", "https://github.com/login/device", 5, DateTimeOffset.MaxValue))
            : throw new InvalidOperationException("device flow disabled");

    public Task<string> WaitForDeviceAuthorizationAsync(string clientId, GitHubDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        IsSignedIn = true;
        AuthStateChanged?.Invoke();
        return Task.FromResult("token");
    }

    public void SignInWithPersonalAccessToken(string token)
    {
        IsSignedIn = true;
        AuthStateChanged?.Invoke();
    }

    public void SignOut()
    {
        IsSignedIn = false;
        AuthStateChanged?.Invoke();
    }

    public Task<string?> GetSignedInLoginAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsSignedIn ? Login : null);
}

internal sealed class StubShellLauncher : IShellLauncher
{
    public List<string> Urls { get; } = [];

    public void OpenUrl(string url) => Urls.Add(url);

    public bool OpenFolder(string path, bool create = false) => true;

    public bool RevealInFileManager(string path) => true;
}

internal sealed class StubClipboard : ISystemClipboard
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);
}

internal sealed class StubApplicationCloser : IApplicationCloser
{
    public int Closed { get; private set; }

    public void Close() => Closed++;
}

/// <summary>An unconfigured TONE3000 account, for tests that only need the tab to resolve.</summary>
internal sealed class StubTone3000Auth : DotCharter.Tone.Online.Tone3000.ITone3000AuthService
{
    public bool IsConfigured => false;

    public bool IsSignedIn => false;

    public string UserName => "";

    public event Action? AuthStateChanged;

    public Task<DotCharter.Tone.Online.OnlineResult<DotCharter.Tone.Online.Tone3000.Tone3000User>> SignInAsync(
        CancellationToken ct = default)
    {
        AuthStateChanged?.Invoke();
        return Task.FromResult(
            DotCharter.Tone.Online.OnlineResult<DotCharter.Tone.Online.Tone3000.Tone3000User>.Fail(
                DotCharter.Tone.Online.OnlineError.NotConfigured, "no client id"));
    }

    public void SignOut() => AuthStateChanged?.Invoke();

    public void InvalidateSession(string reason) => AuthStateChanged?.Invoke();

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
