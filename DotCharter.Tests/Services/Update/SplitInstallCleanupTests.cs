using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The migration plan for a v0.62 FLAT install the old updater has just copied
///     a split payload over (#413). Its top folder then holds the launcher,
///     <c>app/</c>, <c>tools/</c>, <c>voices/</c> — and every file the flat
///     release had, plus the shim. On a portable install it is ALSO the config
///     root: settings, profiles, logs, the library index and the credential stores
///     sit among those binaries. The plan must remove the binaries and nothing else.
/// </summary>
public sealed class SplitInstallCleanupTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>What the v0.62 flat release shipped (its own payload-manifest.json lists these).</summary>
    private static readonly string[] FlatRelease =
    [
        "DotCharter.exe", "DotCharter.dll", "DotCharter.runtimeconfig.json", "DotCharter.deps.json",
        "hostfxr.dll", "coreclr.dll", "Avalonia.Base.dll", "NVorbis.dll", "juce_sharp.dll",
        "de-DE/DotCharter.Localization.resources.dll", "runtimes/win-x64/native/onnxruntime.dll",
        "NamCaptures/clean.nam", "voices/guitar.dcvp", "tools/ffmpeg.exe", "tools/THIRD-PARTY-NOTICES.txt",
    ];

    /// <summary>The split release now in app/ (no NVorbis: it was dropped in between).</summary>
    private static readonly string[] AppFiles =
    [
        "DotCharter.exe", "DotCharter.dll", "DotCharter.runtimeconfig.json", "DotCharter.deps.json",
        "hostfxr.dll", "coreclr.dll", "Avalonia.Base.dll", "juce_sharp.dll",
        "de-DE/DotCharter.Localization.resources.dll", "runtimes/win-x64/native/onnxruntime.dll",
        "NamCaptures/clean.nam",
    ];

    /// <summary>A portable install's own state, all of it at the top, beside the binaries.</summary>
    private static readonly string[] UserState =
    [
        "appconfig.json", "keybinds.json", "player-profiles.json", "profiles/p1/tone-rig.json",
        "github-token.dat", "logs/dotcharter.log", "library-index.db", "library-index.db-wal",
        "checkpoints/song/1.rscp", "recovery/snapshot.rscp", "crash-reports/1.txt", "models/lyric-align/model.onnx",
        "dotcharter-update.log", "dotcharter-launcher.log",
    ];

    private static PayloadManifest Manifest(IEnumerable<string> paths, string version = "0.62.0")
        => new(version, InstallLayout.SelfContained, paths.Select(p => new PayloadFile(p, "00", 1)).ToList());

    private static PayloadManifest SplitManifest()
        => Manifest(
            new[] { "DotCharter.exe", "README.txt", "voices/guitar.dcvp", "tools/ffmpeg.exe" }
                .Concat(LegacyUpdaterShim.Files)
                .Concat(AppFiles.Select(f => "app/" + f)), "0.63.0");

    /// <summary>The top folder right after the old updater's robocopy, outside app/.</summary>
    private static List<string> TopAfterOldUpdater(params string[] extra)
        => [.. FlatRelease, .. UserState, "README.txt", "payload-manifest.json", .. extra];

    [Fact]
    public void Plan_AfterAV062Update_RemovesTheFlatBinaries_AndKeepsEveryPieceOfUserState()
    {
        var plan = SplitInstallCleanup.Plan(TopAfterOldUpdater("my-song.psarc"),
            path => AppFiles.Contains(path, StringComparer.OrdinalIgnoreCase),
            Manifest(FlatRelease), SplitManifest());

        // The flat binaries, the shim (they ARE the flat runtime files' names), the
        // file dropped between releases (only the old manifest knows it), and the
        // old manifest itself.
        Assert.Equal(
            new[]
            {
                "Avalonia.Base.dll", "coreclr.dll", "de-DE/DotCharter.Localization.resources.dll",
                "DotCharter.deps.json", "DotCharter.dll", "DotCharter.runtimeconfig.json", "hostfxr.dll",
                "juce_sharp.dll", "NamCaptures/clean.nam", "NVorbis.dll", "payload-manifest.json",
                "runtimes/win-x64/native/onnxruntime.dll",
            }.Order(StringComparer.OrdinalIgnoreCase),
            plan);

        Assert.All(UserState, state => Assert.DoesNotContain(state, plan));
        Assert.DoesNotContain("DotCharter.exe", plan);       // the launcher
        Assert.DoesNotContain("README.txt", plan);
        Assert.DoesNotContain("my-song.psarc", plan);        // nobody shipped it
        Assert.DoesNotContain(plan, p => p.StartsWith("tools/", StringComparison.Ordinal) ||
                                         p.StartsWith("voices/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A manifest or a counterpart in app/ naming a user-state file must not
    ///     make it deletable: the local-state rule runs before both.
    /// </summary>
    [Fact]
    public void Plan_NeverDeletesUserState_EvenWhenAManifestAndAppNameIt()
    {
        var plan = SplitInstallCleanup.Plan(UserState, _ => true, Manifest(UserState), SplitManifest());
        Assert.Empty(plan);
    }

    /// <summary>
    ///     A v0.62 updater that took its PARTIAL path wrote the NEW manifest to the
    ///     top and deleted the flat files itself. That manifest lists this release's
    ///     own top-level files and must not be read as leftovers.
    /// </summary>
    [Fact]
    public void Plan_AfterTheOldPartialPath_TreatsTheTopManifestAsCurrent()
    {
        var top = new List<string> { "DotCharter.exe", "README.txt", "payload-manifest.json", "appconfig.json" };
        top.AddRange(LegacyUpdaterShim.Files);

        var plan = SplitInstallCleanup.Plan(top, path => AppFiles.Contains(path, StringComparer.OrdinalIgnoreCase),
            SplitManifest(), appManifest: null);

        // Only the shim. The readme the release lists stays, and so does the
        // manifest: it is the only copy until Run moves it into app/.
        Assert.Equal(LegacyUpdaterShim.Files.Order(StringComparer.OrdinalIgnoreCase), plan);
    }

    [Fact]
    public void Plan_WithNoManifestAtAll_StillOnlyRemovesWhatAppAlsoHas()
    {
        var plan = SplitInstallCleanup.Plan(["Avalonia.Base.dll", "NVorbis.dll", "notes.txt", "appconfig.json"],
            path => path == "Avalonia.Base.dll", legacyManifest: null, appManifest: null);
        Assert.Equal(["Avalonia.Base.dll"], plan);
    }

    [Fact]
    public void Run_OnARealFolder_LeavesTheSplitLayoutAndTheUsersFiles()
    {
        var root = _temp.Path;
        foreach (var file in AppFiles) Write(Path.Combine("app", file));
        Write(PayloadManifest.SplitInstalledPath, SplitManifest().ToJson());
        foreach (var file in TopAfterOldUpdater("my-song.psarc")) Write(file);
        Write(PayloadManifest.FileName, Manifest(FlatRelease).ToJson());

        Assert.True(SplitInstallCleanup.NeedsCleanup(root));
        var removed = SplitInstallCleanup.Run(root, logger: null);

        Assert.Equal(12, removed);
        Assert.False(SplitInstallCleanup.NeedsCleanup(root));
        Assert.Equal(
            new[] { "DotCharter.exe", "README.txt", "appconfig.json", "dotcharter-launcher.log",
                    "dotcharter-update.log", "github-token.dat", "keybinds.json", "my-song.psarc",
                    "player-profiles.json", "library-index.db", "library-index.db-wal" }
                .Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        // Emptied flat folders are gone; the layout's and the user's stay.
        Assert.False(Directory.Exists(Path.Combine(root, "runtimes")));
        Assert.False(Directory.Exists(Path.Combine(root, "de-DE")));
        Assert.False(Directory.Exists(Path.Combine(root, "NamCaptures")));
        foreach (var dir in new[] { "app", "tools", "voices", "profiles", "logs", "checkpoints", "models" })
            Assert.True(Directory.Exists(Path.Combine(root, dir)), dir);
        Assert.True(File.Exists(Path.Combine(root, "app", "NamCaptures", "clean.nam")));
        Assert.True(File.Exists(Path.Combine(root, PayloadManifest.SplitInstalledPath)));

        void Write(string relative, string content = "x")
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    [Fact]
    public void NeedsCleanup_IsFalseForAFlatFolder()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "DotCharter.dll"), "x");
        File.WriteAllText(Path.Combine(_temp.Path, "hostfxr.dll"), "x");
        Assert.False(SplitInstallCleanup.NeedsCleanup(_temp.Path));
        Assert.Equal(0, SplitInstallCleanup.Run(_temp.Path, logger: null));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "DotCharter.dll")));
    }
}
