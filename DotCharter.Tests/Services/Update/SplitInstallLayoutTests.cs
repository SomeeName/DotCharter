using System.IO.Compression;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The split install (#413): the launcher, <c>voices/</c>, <c>tools/</c> and a
///     readme at the top, the app in <c>app/</c>. These pin what the updater
///     validates, fetches and copies for it — and that a split release is still a
///     payload the updater inside every v0.62-and-older install accepts.
/// </summary>
public sealed class SplitInstallLayoutTests : IDisposable
{
    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A split release payload as the release script lays it out, shim included.</summary>
    private static HashSet<string> SplitRelease(bool withShim = true)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DotCharter.exe", "README.txt", "voices/guitar.dcvp", "tools/ffmpeg.exe",
            "app/DotCharter.exe", "app/DotCharter.dll", "app/DotCharter.runtimeconfig.json",
            "app/DotCharter.deps.json", "app/hostfxr.dll", "app/coreclr.dll", "app/payload-manifest.json",
        };
        if (withShim) files.UnionWith(LegacyUpdaterShim.Files);
        return files;
    }

    // ---- what the CURRENT updater validates ------------------------------------

    [Fact]
    public void Validate_SplitPayload_ReadsTheRuntimeFromApp()
    {
        var files = SplitRelease(withShim: false);
        Assert.Equal("app/", AppPayload.AppPrefix(files.Contains));
        Assert.Null(AppPayload.Validate(files.Contains, SelfContainedConfig, InstallLayout.SelfContained));
    }

    [Theory]
    [InlineData("DotCharter.exe")]                    // the launcher
    [InlineData("app/hostfxr.dll")]                   // the app's own host, not the shim's copy
    [InlineData("app/DotCharter.deps.json")]
    public void Validate_SplitPayloadMissingAPiece_IsRefusedByName(string missing)
    {
        // The shim copies at the top must NOT stand in for the app's own files:
        // the app in app/ could not start without them.
        var files = SplitRelease();
        files.Remove(missing);

        var problem = AppPayload.Validate(files.Contains, SelfContainedConfig, InstallLayout.SelfContained);

        Assert.NotNull(problem);
        Assert.Contains(missing.Replace("app/", string.Empty, StringComparison.Ordinal), problem);
    }

    [Fact]
    public void CheckShape_RefusesAFlatPayloadOverASplitInstall_AndAllowsTheMigration()
    {
        Assert.NotNull(AppPayload.CheckShape(installIsSplit: true, payloadIsSplit: false));
        Assert.Null(AppPayload.CheckShape(installIsSplit: false, payloadIsSplit: true));   // v0.62 → split
        Assert.Null(AppPayload.CheckShape(installIsSplit: true, payloadIsSplit: true));
        Assert.Null(AppPayload.CheckShape(installIsSplit: false, payloadIsSplit: false));
    }

    [Fact]
    public void ClassifyDirectory_OfASplitInstall_ReadsApp()
    {
        WriteSplitInstall(_temp.Path);
        Assert.True(AppPayload.IsSplitDirectory(_temp.Path));
        Assert.Equal(InstallLayout.SelfContained, AppPayload.ClassifyDirectory(_temp.Path));
        Assert.Null(AppPayload.ValidateDirectory(_temp.Path, InstallLayout.SelfContained));
    }

    [Fact]
    public void ValidateArchive_OfAFolderWrappedSplitZip_Passes_AndThePlannerFindsTheSameRoot()
    {
        var zipPath = Path.Combine(_temp.Path, "split.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in SplitRelease())
            {
                using var writer = new StreamWriter(zip.CreateEntry("DotCharter-v9.9.9-win-x64/" + name).Open());
                writer.Write(name.EndsWith("runtimeconfig.json", StringComparison.Ordinal) ? SelfContainedConfig : "MZ");
            }
        }

        using var archive = ZipFile.OpenRead(zipPath);
        // The launcher at the top is the root marker, never app/DotCharter.exe.
        Assert.Equal("DotCharter-v9.9.9-win-x64/", AppPayload.ResolveArchiveRoot(archive));
        Assert.Null(AppPayload.ValidateArchive(archive, InstallLayout.SelfContained));
    }

    // ---- the OLD updater ----------------------------------------------------------

    /// <summary>
    ///     The reason the shim exists: a v0.62 updater validates the payload ROOT
    ///     with the flat rule. Without the four copies it refuses the release that
    ///     moves it into app/, and every old install is stranded on its version.
    /// </summary>
    [Fact]
    public void ASplitRelease_PassesTheV062UpdatersFlatRule_OnlyWithTheShim()
    {
        var withShim = SplitRelease();
        Assert.Null(LegacyUpdaterShim.CheckLegacyUpdaterAccepts(withShim.Contains, SelfContainedConfig));
        // …and that IS the old validator: the same flat Validate, blind to app/.
        Assert.Null(AppPayload.Validate(
            name => withShim.Contains(name) && !name.Contains('/'), SelfContainedConfig, InstallLayout.SelfContained));

        foreach (var name in LegacyUpdaterShim.Files)
        {
            var without = SplitRelease();
            without.Remove(name);
            Assert.NotNull(LegacyUpdaterShim.CheckLegacyUpdaterAccepts(without.Contains, SelfContainedConfig));
        }
    }

    [Fact]
    public void TheShimIsOnlyEverTheTopLevelNames()
    {
        Assert.True(LegacyUpdaterShim.IsShimPath("hostfxr.dll"));
        Assert.True(LegacyUpdaterShim.IsShimPath("DotCharter.runtimeconfig.json"));
        Assert.False(LegacyUpdaterShim.IsShimPath("app/hostfxr.dll"));
        Assert.False(LegacyUpdaterShim.IsShimPath("DotCharter.exe"));
        Assert.False(LegacyUpdaterShim.IsShimPath("DotCharter.dll"));
    }

    // ---- what the current updater FETCHES and COPIES -------------------------------

    [Fact]
    public void AlwaysIncludeFor_ASplitManifest_IsTheAppsFiveAndTheLauncher()
    {
        var release = Manifest(SplitRelease());
        Assert.True(release.IsSplit);
        Assert.Equal(PayloadManifest.SplitInstalledPath, release.InstalledPath);
        Assert.Equal(
            ["DotCharter.exe", "app/DotCharter.exe", "app/DotCharter.runtimeconfig.json", "app/DotCharter.deps.json",
                "app/hostfxr.dll", "app/coreclr.dll"],
            PayloadManifest.AlwaysIncludeFor(release));

        // Unchanged files still ship when they are what the validator demands.
        var delta = PayloadDelta.Between(release, release);
        Assert.Equal(6, delta.Changed.Count);
        Assert.DoesNotContain(delta.Changed, f => LegacyUpdaterShim.IsShimPath(f.Path));
    }

    [Fact]
    public void StripFrom_DropsOnlyTheShim_AndLeavesAFlatManifestAlone()
    {
        var stripped = LegacyUpdaterShim.StripFrom(Manifest(SplitRelease()));
        Assert.DoesNotContain(stripped.Files, f => LegacyUpdaterShim.IsShimPath(f.Path));
        Assert.Equal(SplitRelease(withShim: false).Count, stripped.Files.Count);

        var flat = Manifest(["DotCharter.exe", "DotCharter.runtimeconfig.json", "hostfxr.dll"]);
        Assert.Same(flat, LegacyUpdaterShim.StripFrom(flat));
    }

    [Fact]
    public void RemoveFrom_DeletesTheShimFromAStagedSplitPayload_NeverFromAFlatOne()
    {
        var split = Path.Combine(_temp.Path, "split");
        WriteSplitInstall(split);
        foreach (var name in LegacyUpdaterShim.Files) File.WriteAllText(Path.Combine(split, name), "copy");

        Assert.Equal(4, LegacyUpdaterShim.RemoveFrom(split));
        Assert.All(LegacyUpdaterShim.Files, name => Assert.False(File.Exists(Path.Combine(split, name))));
        Assert.True(File.Exists(Path.Combine(split, "app", AppPayload.HostFxrName)));

        // In a flat payload the same names are the app's own runtime.
        var flat = Path.Combine(_temp.Path, "flat");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, AppPayload.ExeName), "MZ");
        File.WriteAllText(Path.Combine(flat, AppPayload.HostFxrName), "MZ");
        Assert.Equal(0, LegacyUpdaterShim.RemoveFrom(flat));
        Assert.True(File.Exists(Path.Combine(flat, AppPayload.HostFxrName)));
    }

    [Fact]
    public void InstalledPayloadReader_ReadsTheManifestInApp_BeforeAnOldOneAtTheTop()
    {
        WriteSplitInstall(_temp.Path);
        var current = Manifest(["app/DotCharter.exe"]) with { Version = "0.63.0" };
        File.WriteAllText(Path.Combine(_temp.Path, PayloadManifest.SplitInstalledPath), current.ToJson());
        File.WriteAllText(Path.Combine(_temp.Path, PayloadManifest.FileName),
            (Manifest(["DotCharter.exe"]) with { Version = "0.62.0" }).ToJson());

        var installed = InstalledPayloadReader.FromDirectory(current, _temp.Path);

        Assert.Equal(InstalledPayloadSource.ShippedManifest, installed.Source);
        Assert.Equal("0.63.0", installed.Manifest.Version);
    }

    [Fact]
    public void ApplyDownloadedUpdate_AFlatPayloadOverASplitInstall_IsRefusedAndStagesNothing()
    {
        WriteSplitInstall(_temp.Path);
        var zipPath = Path.Combine(_temp.Path, "flat.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[]
                     {
                         AppPayload.ExeName, AppPayload.DepsName, AppPayload.RuntimeConfigName, AppPayload.HostFxrName,
                         AppPayload.CoreClrName,
                     })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name == AppPayload.RuntimeConfigName ? SelfContainedConfig : "MZ");
            }
        }

        using var service = new GitHubUpdateService(new Version(0, 63, 0), installDir: _temp.Path,
            downloadDir: _temp.Path);

        service.TrustDownloadForTests(zipPath);
        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("flat payload", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, UpdaterScriptBuilder.StagingDirName)));
        Assert.False(File.Exists(Path.Combine(_temp.Path, UpdaterScriptBuilder.ScriptFileName)));
    }

    private static PayloadManifest Manifest(IEnumerable<string> paths)
        => new("9.9.9", InstallLayout.SelfContained,
            paths.Order(StringComparer.OrdinalIgnoreCase).Select(p => new PayloadFile(p, "00", 2)).ToList());

    private static void WriteSplitInstall(string dir)
    {
        var app = Path.Combine(dir, AppPayload.AppFolderName);
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.ExeName), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(app, AppPayload.ExeName), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(app, AppPayload.RuntimeConfigName), SelfContainedConfig);
        File.WriteAllText(Path.Combine(app, AppPayload.DepsName), "{}");
        File.WriteAllBytes(Path.Combine(app, AppPayload.HostFxrName), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(app, AppPayload.CoreClrName), [0x4D, 0x5A]);
    }
}
