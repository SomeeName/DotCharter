using System.Text.RegularExpressions;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Services.Import.Youtube;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Cache;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     #439: with <c>DOTCHARTER_DATA_ROOT</c> set, every per-user data path the app
///     resolves lies under it — so a test or measurement launch never reads,
///     renames or deletes the user's real data. The override is applied through
///     <see cref="UserDataFolders.UseDataRootForTesting" /> (an <c>AsyncLocal</c>,
///     so no parallel test sees it); the product reads it exactly where it reads
///     the variable.
/// </summary>
public sealed class UserDataFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-dataroot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder this test made; the OS cleans it up.
        }
    }

    /// <summary>
    ///     Every product path that starts from a user folder, keyed by the file that
    ///     builds it. <see cref="EveryFileThatUsesTheResolver_IsCoveredHere" /> keeps the
    ///     keys in step with the source, so a new call site cannot skip this test.
    /// </summary>
    private static IEnumerable<(string File, string Path)> ResolvedDataPaths() =>
    [
        ("DotCharter.Tone/Infrastructure/Storage/WritablePaths.cs", WritablePaths.GetUserAppDir()),
        ("DotCharter.Tone/Infrastructure/Storage/WritablePaths.cs", WritablePaths.RedirectedConfigRoot()!),
        ("DotCharter.Tone/Infrastructure/Storage/WritablePaths.cs", WritablePaths.RedirectedLogRoot()!),
        ("DotCharter.Tone/Audio/Tonelab/TonelabToneFiles.cs", new DotCharter.Tone.Audio.Tonelab.TonelabToneFiles().Directory),
        ("DotCharter.Tone/Audio/Tonelab/TonelabGameMirror.cs", DotCharter.Tone.Audio.Tonelab.TonelabGameMirror.DefaultRoot),
        ("DotCharter.Tone/Online/Tone3000/NamCaptureLibrary.cs", NamCaptureLibrary.DefaultRootDirectory),
        ("DotCharter.Services/Ai/Match/Model/RigEstimatorModelCatalog.cs", RigEstimatorModelCatalog.ModelRoot),
        ("DotCharter.Services/Ai/Match/Model/RigEstimatorModelCatalog.cs", RigEstimatorModelCatalog.ModelDirectory),
        ("DotCharter.Services/Ai/Match/Model/RigEstimatorOnnx.cs", RigEstimatorOnnx.DefaultModelDirectory),
        ("DotCharter.Services/Ai/Match/Search/MatchResultStore.cs", MatchResultStore.DefaultRoot()),
        ("DotCharter.Services/Ai/Match/Search/RigLibrary.cs", RigLibrary.DefaultCachePath("x", 1, 2, "fp")),
        ("DotCharter.Services/Audio/Lyrics/LyricModelCatalog.cs", LyricModelCatalog.ModelRoots[^1]),
        ("DotCharter.Services/Audio/SilentTransport/SilentAudioTrack.cs", new SilentAudioTrack().CacheDirectory),
        ("DotCharter.Services/Audio/StemSeparation/Cache/StemCacheStore.cs", StemCacheStore.DefaultRoot()),
        ("DotCharter.Services/Audio/StemSeparation/JuceStemSeparationService.cs",
            JuceStemSeparationService.RoamingModelsDirectory),
        ("DotCharter.Services/Audio/StemSeparation/StemModelManager.cs", StemModelManager.DefaultModelsDirectory),
        ("DotCharter.Services/Audio/Waveform/WaveformDiskCache.cs", WaveformDiskCache.DefaultRoot),
        ("DotCharter.Services/Import/Youtube/YoutubeAudioDownload.cs",
            new YoutubeAudioDownload(new NoYtDlp(), new ProjectState()).OutputDirectory()),
        ("DotCharter.Services/Library/Cache/LibraryAudioCacheStore.cs", LibraryAudioCacheStore.DefaultRoot()),
        ("DotCharter.Services/Library/LibraryCoverCache.cs", LibraryCoverCache.AlbumArtCacheDirectory()),
        ("DotCharter.UI/Program.cs", global::DotCharter.UI.Program.PipelineCacheDirectory()),
    ];

    /// <summary>Files that use the resolver only to FIND another program's install
    /// (<see cref="UserDataFolders.OtherProgramsLocalAppData" />), read-only.</summary>
    private static readonly string[] ReadOnlyProbes =
    [
        "DotCharter.Tone/Ai/CodexCliToneModel.cs",
        "DotCharter.Tone/Audio/JuceAudioPlaybackService.VstChain.cs",
    ];

    [Fact]
    public void WithTheOverride_EveryResolvedDataPath_LiesUnderIt()
    {
        using (UserDataFolders.UseDataRootForTesting(_root))
        {
            Assert.Equal(_root, UserDataFolders.DataRoot);
            Assert.Equal(Path.Combine(_root, "Roaming"), UserDataFolders.RoamingAppData);
            Assert.Equal(Path.Combine(_root, "Local"), UserDataFolders.LocalAppData);
            Assert.True(Directory.Exists(UserDataFolders.RoamingAppData), "the roaming stand-in is created on demand");
            Assert.True(Directory.Exists(UserDataFolders.LocalAppData), "the local stand-in is created on demand");

            var prefix = _root + Path.DirectorySeparatorChar;
            var outside = ResolvedDataPaths()
                .Where(p => !Path.GetFullPath(p.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.File}: {p.Path}")
                .ToList();
            Assert.True(outside.Count == 0,
                "these paths ignore DOTCHARTER_DATA_ROOT, so a test launch would use the user's real data:\n  " +
                string.Join("\n  ", outside));
        }

        Assert.Null(TestScopedRootOrNull());
    }

    [Fact]
    public void WithoutTheOverride_ThePathsAreExactlyTheUserFolders()
    {
        if (Environment.GetEnvironmentVariable(UserDataFolders.DataRootVariable) is { Length: > 0 }) return;

        Assert.False(UserDataFolders.IsRedirected);
        Assert.Null(UserDataFolders.DescribeOverride());
        Assert.Null(WritablePaths.RedirectedConfigRoot());
        Assert.Null(WritablePaths.RedirectedLogRoot());
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), UserDataFolders.RoamingAppData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), UserDataFolders.LocalAppData);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotCharter", "StemCache"),
            StemCacheStore.DefaultRoot());
    }

    [Fact]
    public void TheReadOnlyProbe_IsNeverRedirected()
    {
        using var scope = UserDataFolders.UseDataRootForTesting(_root);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UserDataFolders.OtherProgramsLocalAppData);
    }

    [Fact]
    public void TheStartupLine_NamesTheOverride()
    {
        using var scope = UserDataFolders.UseDataRootForTesting(_root);
        var line = UserDataFolders.DescribeOverride();
        Assert.NotNull(line);
        Assert.Contains(UserDataFolders.DataRootVariable + "=" + _root, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\"\"", false)]
    [InlineData("relative-root", true)]
    public void Normalize_TreatsBlankAsUnset_AndMakesARelativeRootAbsolute(string? value, bool expected)
    {
        var normalized = UserDataFolders.Normalize(value);
        Assert.Equal(expected, normalized != null);
        if (normalized != null) Assert.True(Path.IsPathFullyQualified(normalized));
    }

    [Fact]
    public async Task TheTestScope_DoesNotLeakIntoAnotherFlow()
    {
        var seenElsewhere = "unset";
        var gate = new TaskCompletionSource();
        var other = Task.Run(async () =>
        {
            await gate.Task;
            seenElsewhere = TestScopedRootOrNull() ?? "none";
        }, TestContext.Current.CancellationToken);

        using (UserDataFolders.UseDataRootForTesting(_root))
        {
            gate.SetResult();
            await other;
        }

        Assert.Equal("none", seenElsewhere);
    }

    /// <summary>
    ///     Keeps <see cref="ResolvedDataPaths" /> honest: every product file that
    ///     builds a path from <see cref="UserDataFolders.RoamingAppData" /> or
    ///     <see cref="UserDataFolders.LocalAppData" /> must be listed there (or, for
    ///     <see cref="UserDataFolders.OtherProgramsLocalAppData" />, in
    ///     <see cref="ReadOnlyProbes" />), and every listed file must still use it.
    /// </summary>
    [Fact]
    public void EveryFileThatUsesTheResolver_IsCoveredHere()
    {
        var dataUse = new Regex(@"UserDataFolders\s*\.\s*(?:RoamingAppData|LocalAppData)\b");
        var probeUse = new Regex(@"UserDataFolders\s*\.\s*OtherProgramsLocalAppData\b");
        var covered = ResolvedDataPaths().Select(p => p.File).ToHashSet(StringComparer.Ordinal);
        // WritablePaths reaches the resolver through GetUserAppDir; NamCaptureLibrary
        // and the others name it directly. The resolver file itself is the definition.
        var usingData = new SortedSet<string>(StringComparer.Ordinal);
        var usingProbe = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in UserDataFolderLintTests.ProductSources())
        {
            var relative = UserDataFolderLintTests.Relative(file);
            if (relative.EndsWith("/UserDataFolders.cs", StringComparison.Ordinal)) continue;
            var code = SourceText.StripComments(File.ReadAllText(file));
            if (dataUse.IsMatch(code)) usingData.Add(relative);
            if (probeUse.IsMatch(code)) usingProbe.Add(relative);
        }

        Assert.Equal(covered.Order(StringComparer.Ordinal), usingData);
        Assert.Equal(ReadOnlyProbes.Order(StringComparer.Ordinal), usingProbe);
    }

    private static string? TestScopedRootOrNull() =>
        Environment.GetEnvironmentVariable(UserDataFolders.DataRootVariable) is { Length: > 0 }
            ? null
            : UserDataFolders.DataRoot;

    private sealed class NoYtDlp : IYtDlpAudioDownloader
    {
        public bool IsAvailable => false;
        public string? YtDlpPath => null;
        public string? FfmpegPath => null;

        public Task<YtDlpDownloadResult> DownloadMp3Async(string videoUrl, string outputBasePathNoExtension,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
            throw new NotSupportedException();
    }
}
