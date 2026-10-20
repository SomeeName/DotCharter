using System.IO.Compression;
using System.Text;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     An install that carries a plugin (a declared plugin's file beside the app) never takes a
///     payload without it. The public release is built with <c>-p:DotCharterPlugins=false</c>
///     and refuses to carry a plugin, so a plugin build offered a payload without it is looking
///     at the public feed: applying it would stop the plugin loading, and a partial update
///     would delete it. Offline: every release here is served by <see cref="FakeReleaseHttp" />.
/// </summary>
public sealed class PluginUpdateGuardTests : IDisposable
{
    private const string AssetName = "DotCharter-v0.43.0-win-x64.zip";
    private const string Plugin = "Example.Plugin.dll";

    /// <summary>What the running app would declare; the guard knows plugins only through it.</summary>
    private static readonly string[] Declared = [Plugin];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---- the rule ------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void OnlyAnInstallWithAPlugin_OfferedAPayloadWithoutIt_IsRefused(
        bool installHasPlugin, bool payloadHasPlugin, bool refused)
    {
        var install = Path.Combine(_temp.Path, "install");
        var payload = Path.Combine(_temp.Path, "payload");
        WriteSplit(install, plugin: installHasPlugin);
        WriteSplit(payload, plugin: payloadHasPlugin);

        var problem = PluginUpdateGuard.Check(PluginUpdateGuard.MissingFromPayload(
            payload, PluginUpdateGuard.InstalledPlugins(install, Declared)), "v0.71.0");

        Assert.Equal(refused, problem != null);
        if (refused)
        {
            Assert.Contains(Plugin, problem, StringComparison.Ordinal);
            Assert.Contains("v0.71.0 is a build without them", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APluginTheAppDoesNotDeclare_IsNotGuarded()
    {
        var install = Path.Combine(_temp.Path, "install");
        WriteSplit(install, plugin: true);

        Assert.Empty(PluginUpdateGuard.InstalledPlugins(install, []));
        Assert.Equal(Declared, PluginUpdateGuard.InstalledPlugins(install, Declared));
        Assert.Null(PluginUpdateGuard.Check([]));
    }

    [Theory]
    [InlineData("app/Example.Plugin.dll", true)]
    [InlineData("Example.Plugin.dll", true)]
    [InlineData("app/example.plugin.DLL", true)]
    [InlineData("app/Example.Plugin.pdb", false)]
    [InlineData("app/Example.Plugin.Tests.dll", false)]
    [InlineData("app/sub/Example.Plugin.dll", false)]
    public void TheManifestCarriesThePluginOnlyAtItsOwnPath(string path, bool carries)
    {
        var manifest = new PayloadManifest("0.71.0", InstallLayout.SelfContained,
            [new PayloadFile("app/DotCharter.exe", "00", 2), new PayloadFile(path, "00", 2)]);

        Assert.Equal(carries, PluginUpdateGuard.MissingFromManifest(manifest, Declared).Count == 0);
    }

    /// <summary>
    ///     A PARTIAL payload ships only the files that changed, so an unchanged plugin is not
    ///     in it — its manifest, the whole release, still lists it, and that must count.
    /// </summary>
    [Fact]
    public void APartialPayloadCarriesThePluginThroughItsManifest()
    {
        var payload = Path.Combine(_temp.Path, "partial");
        WriteSplit(payload, plugin: false);
        Assert.Equal(Declared, PluginUpdateGuard.MissingFromPayload(payload, Declared));

        var manifest = new PayloadManifest("0.71.0", InstallLayout.SelfContained,
            [new PayloadFile("app/DotCharter.exe", "00", 2), new PayloadFile("app/" + Plugin, "00", 2)]);
        File.WriteAllText(Path.Combine(payload, PayloadManifest.SplitInstalledPath), manifest.ToJson());

        Assert.Empty(PluginUpdateGuard.MissingFromPayload(payload, Declared));
    }

    [Fact]
    public void AnInstallCarriesThePluginWhenItsAppFolderHoldsIt()
    {
        var install = Path.Combine(_temp.Path, "install");
        WriteSplit(install, plugin: false);
        Assert.Empty(PluginUpdateGuard.InstalledPlugins(install, Declared));

        File.WriteAllBytes(Path.Combine(install, AppPayload.AppFolderName, Plugin), [0x4D, 0x5A]);
        Assert.Equal(Declared, PluginUpdateGuard.InstalledPlugins(install, Declared));
    }

    // ---- the apply path ------------------------------------------------------

    /// <summary>
    ///     A structurally perfect public payload over a private install is refused, nothing is
    ///     staged, and the message says why. The payload also carries an <c>appconfig.json</c>,
    ///     which <c>PayloadReceiptFilter</c> refuses one step LATER: without the plugin guard
    ///     this test still gets a refusal, but the wrong one, and never launches the updater.
    /// </summary>
    [Fact]
    public void APublicPayloadOverAPluginInstall_IsRefusedBeforeAnythingIsStaged()
    {
        var install = Path.Combine(_temp.Path, "install");
        WriteSplit(install, plugin: true);
        var zipPath = WriteSplitZip("public.zip", plugin: false);

        using var service = new GitHubUpdateService(new Version(0, 70, 0), installDir: install,
            downloadDir: _temp.Path, pluginFiles: Declared);
        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("carries plugins (" + Plugin + ")", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(install, UpdaterScriptBuilder.StagingDirName)));
        Assert.False(File.Exists(Path.Combine(install, UpdaterScriptBuilder.ScriptFileName)));
        Assert.True(File.Exists(Path.Combine(install, AppPayload.AppFolderName, Plugin)), "the plugin was touched");
    }

    /// <summary>
    ///     The same install, offered a payload that carries the plugin: the guard lets it
    ///     through, and the refusal that follows is the receipt filter's.
    /// </summary>
    [Fact]
    public void APluginPayloadOverAPluginInstall_PassesThePluginGuard()
    {
        var install = Path.Combine(_temp.Path, "install");
        WriteSplit(install, plugin: true);
        var zipPath = WriteSplitZip("plugin.zip", plugin: true);

        using var service = new GitHubUpdateService(new Version(0, 70, 0), installDir: install,
            downloadDir: _temp.Path, pluginFiles: Declared);
        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.DoesNotContain("carries plugins", error.Message, StringComparison.Ordinal);
        Assert.Contains("appconfig.json", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the download path ---------------------------------------------------

    /// <summary>
    ///     The published manifest is the whole release, so a plugin install learns from it
    ///     that the release lacks the plugin BEFORE any asset byte is fetched — and the refusal does
    ///     not fall back to the full download, which would only be refused on apply.
    /// </summary>
    [Fact]
    public async Task APluginInstall_RefusesAPublicReleaseBeforeFetchingTheAsset()
    {
        var release = BuildRelease(installHasPlugin: true, releaseHasPlugin: false);
        var handler = Serve(release);
        using var service = NewService(handler);

        var error = await Assert.ThrowsAsync<PluginUpdateRefusedException>(() => service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("v0.43.0 is a build without them", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.ToString() == FakeReleaseHttp.AssetUrl);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "downloads", AssetName)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task EveryOtherPairing_StillTakesThePartialDownload(bool installHasPlugin, bool releaseHasPlugin)
    {
        var release = BuildRelease(installHasPlugin, releaseHasPlugin);
        using var service = NewService(Serve(release));

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHubReleaseRules.PartialAssetName(new Version(0, 43, 0)), Path.GetFileName(path));
    }

    // ---- helpers -------------------------------------------------------------

    private static readonly string[] AppFiles =
    [
        AppPayload.ExeName, AppPayload.RuntimeConfigName, AppPayload.DepsName, AppPayload.HostFxrName,
        AppPayload.CoreClrName,
    ];

    private static byte[] Body(string name)
        => name == AppPayload.RuntimeConfigName ? Encoding.UTF8.GetBytes(FakeRelease.SelfContainedConfig) : [0x4D, 0x5A];

    /// <summary>A split install or payload (#413): the launcher at the top, the app in app/.</summary>
    private static void WriteSplit(string dir, bool plugin)
    {
        var app = Path.Combine(dir, AppPayload.AppFolderName);
        Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.ExeName), [0x4D, 0x5A]);
        foreach (var name in AppFiles)
            File.WriteAllBytes(Path.Combine(app, name), Body(name));
        if (plugin) File.WriteAllBytes(Path.Combine(app, Plugin), [0x4D, 0x5A]);
    }

    /// <summary>A folder-wrapped split release zip, plus an <c>appconfig.json</c> the receipt
    /// filter refuses after the plugin guard has had its say.</summary>
    private string WriteSplitZip(string name, bool plugin)
    {
        const string root = "DotCharter-v0.71.0-win-x64/";
        var zipPath = Path.Combine(_temp.Path, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string entry, byte[] bytes)
        {
            using var stream = zip.CreateEntry(root + entry).Open();
            stream.Write(bytes);
        }

        Add(AppPayload.ExeName, [0x4D, 0x5A]);
        foreach (var file in AppFiles)
            Add(AppPayload.SplitPrefix + file, Body(file));
        if (plugin) Add(AppPayload.SplitPrefix + Plugin, [0x4D, 0x5A]);
        Add("appconfig.json", Encoding.UTF8.GetBytes("{}"));
        return zipPath;
    }

    private GitHubUpdateService NewService(FakeHttpMessageHandler handler)
        => new(new Version(0, 42, 0), handler: handler,
            installDir: Path.Combine(_temp.Path, "install"),
            downloadDir: Path.Combine(_temp.Path, "downloads"), pluginFiles: Declared);

    private static FakeHttpMessageHandler Serve(ReleaseFixture release)
        => FakeReleaseHttp.Serve("v0.43.0", AssetName, release.Zip, release.ManifestBytes,
            FakeReleaseHttp.Digests((AssetName, release.Zip), (PayloadManifest.FileName, release.ManifestBytes)));

    /// <summary>
    ///     <c>PartialManifestChecksumTests</c>' flat release, one version ahead of the install,
    ///     with the plugin in the install and/or the release as asked. The plugin never changes
    ///     between the two, so a partial download of a plugin release does not carry it.
    /// </summary>
    private ReleaseFixture BuildRelease(bool installHasPlugin, bool releaseHasPlugin)
    {
        var installDir = Path.Combine(_temp.Path, "install");
        Directory.CreateDirectory(installDir);

        var files = FakeRelease.HostFiles("0.43.0");
        files["app.dll"] = FakeRelease.Content("app-new", 3000);
        files["runtime/big.dll"] = FakeRelease.Content("runtime", 4_000_000);
        var pluginBytes = FakeRelease.Content("plugin", 2000);
        if (releaseHasPlugin) files[Plugin] = pluginBytes;

        var manifest = FakeRelease.Manifest("0.43.0", files);
        var zip = FakeRelease.Zip(files, "DotCharter-v0.43.0-win-x64");

        var installedFiles = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase)
        {
            ["app.dll"] = FakeRelease.Content("app-old", 2500),
        };
        if (installHasPlugin) installedFiles[Plugin] = pluginBytes;
        else installedFiles.Remove(Plugin);

        foreach (var (name, content) in installedFiles)
        {
            var target = Path.Combine(installDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        File.WriteAllText(Path.Combine(installDir, PayloadManifest.FileName),
            FakeRelease.Manifest("0.42.0", installedFiles).ToJson());

        var update = new UpdateInfo(
            new Version(0, 43, 0), "v0.43.0", "notes", "https://example/r",
            AssetName, FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, zip.Length,
            FakeReleaseHttp.ManifestUrl);

        return new ReleaseFixture(zip, Encoding.UTF8.GetBytes(manifest.ToJson()), update);
    }

    private sealed record ReleaseFixture(byte[] Zip, byte[] ManifestBytes, UpdateInfo Update);
}
