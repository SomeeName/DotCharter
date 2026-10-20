using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Infrastructure;

// Do-nothing implementations of services a fixture must hand a constructor that
// takes them as REQUIRED (#661) but whose test never exercises them. Each answers
// "nothing here": no session to load, no takes, no TONE3000 account, an empty
// capture library, a shell that opens nothing.

/// <summary>A session with no project behind it: loads change nothing, saves fail.</summary>
internal sealed class InertProjectSession : IProjectSessionService
{
    public AppConfig AppConfig { get; } = new();
    public bool IsProjectDirty => false;
    public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

    public event Action? ProjectDirtyChanged { add { } remove { } }
    public event Action? RecentProjectsChanged { add { } remove { } }

    public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
    public void MarkProjectClean() { }
    public void MarkProjectDirty() { }
    public void Initialize() { }
    public void LoadAppConfig() { }
    public void SaveAppConfig() { }
    public void FreeAudio() { }
    public Task LoadFile(string filePath) => Task.CompletedTask;
    public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false) => Task.CompletedTask;
    public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(false);
    public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
    public Task OpenProject() => Task.CompletedTask;
    public Task<bool> SaveProjectAsync() => Task.FromResult(false);
    public Task<bool> SaveProjectAsAsync() => Task.FromResult(false);
    public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
    public void Dispose() { }
}

/// <summary>An empty library: scans find nothing, a cover is no file.</summary>
internal sealed class InertPsarcLibrary : ILibraryService
{
    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => Task.FromResult<IReadOnlyList<LibrarySongEntry>>([]);

    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath, IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken) => ScanAsync(rootPath);

    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath, IProgress<LibraryScanProgress>? progress,
        IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken) => ScanAsync(rootPath);

    public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) =>
        throw new NotSupportedException("The inert library holds no songs.");

    public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult(string.Empty);
}

/// <summary>Take recording switched off: nothing is recorded, nothing resolves.</summary>
internal sealed class InertLiveTakeStore : ILiveTakeStore
{
    public bool IsEnabled => false;
    public string TakesDirectory => Path.Combine(Path.GetTempPath(), "dotcharter-inert-takes");
    public LiveTakeRecorder? TryStartTake(int sampleRate) => null;
    public Task<string?> FinalizeTakeAsync(LiveTakeRecorder recorder) => Task.FromResult<string?>(null);
    public Task CleanupAsync() => Task.CompletedTask;
}

/// <summary>TONE3000 offline: every call fails as "not configured".</summary>
internal sealed class InertTone3000Service : ITone3000Service
{
    public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(Tone3000SearchQuery query, CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<Tone3000SearchPage>.Fail(OnlineError.NotConfigured));

    public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.NotConfigured));

    public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(long toneId, CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<IReadOnlyList<Tone3000Model>>.Fail(OnlineError.NotConfigured));

    public Task<OnlineResult<long>> DownloadModelAsync(Tone3000Model model, Stream destination, long maxBytes,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<long>.Fail(OnlineError.NotConfigured));
}

/// <summary>No TONE3000 account: not configured, never signed in.</summary>
internal sealed class InertTone3000Auth : ITone3000AuthService
{
    public bool IsConfigured => false;
    public bool IsSignedIn => false;
    public string UserName => "";

    public event Action? AuthStateChanged { add { } remove { } }

    public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<Tone3000User>.Fail(OnlineError.NotConfigured));

    public void SignOut() { }
    public void InvalidateSession(string reason) { }
    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>An empty capture library that cannot download or import.</summary>
internal sealed class InertNamCaptureLibrary : INamCaptureLibrary
{
    public string RootDirectory => Path.Combine(Path.GetTempPath(), "dotcharter-inert-nam");

    public event EventHandler? Changed { add { } remove { } }

    public IReadOnlyList<NamCaptureEntry> GetAll() => [];
    public bool IsModelDownloaded(long modelId) => false;
    public bool IsToneDownloaded(long toneId) => false;

    public Task<OnlineResult<NamCaptureEntry>> DownloadAsync(Tone3000Tone tone, Tone3000Model model,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(OnlineResult<NamCaptureEntry>.Fail(OnlineError.NotConfigured));

    public bool Delete(long modelId) => false;
    public int MigrateFileExtensions() => 0;
    public bool ImportBundled(string sourceFilePath, BundledNamCapture meta) => false;
}

/// <summary>A shell that opens nothing and records what it was asked to open.</summary>
internal sealed class InertShellLauncher : IShellLauncher
{
    public List<string> OpenedUrls { get; } = [];

    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public bool OpenFolder(string path, bool create = false) => false;
    public bool RevealInFileManager(string path) => false;
}
