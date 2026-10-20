using System.IO.Compression;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

public sealed class GitHubUpdateServiceTests
{
    private const string LatestReleaseJson = """
        {
          "tag_name": "v0.13.0",
          "name": "DotCharter 0.13.0",
          "html_url": "https://github.com/SomeeName/DotCharter/releases/tag/v0.13.0",
          "prerelease": false,
          "body": "## Highlights\n- Auto-update\n- GitHub sign-in",
          "assets": [
            {
              "name": "DotCharter-v0.13.0-linux-x64.tar.gz",
              "url": "https://api.github.com/repos/SomeeName/DotCharter/releases/assets/11",
              "browser_download_url": "https://github.com/SomeeName/DotCharter/releases/download/v0.13.0/DotCharter-v0.13.0-linux-x64.tar.gz",
              "size": 1000
            },
            {
              "name": "DotCharter-v0.13.0-win-x64.zip",
              "url": "https://api.github.com/repos/SomeeName/DotCharter/releases/assets/12",
              "browser_download_url": "https://github.com/SomeeName/DotCharter/releases/download/v0.13.0/DotCharter-v0.13.0-win-x64.zip",
              "size": 123456789
            },
            {
              "name": "checksums.txt",
              "url": "https://api.github.com/repos/SomeeName/DotCharter/releases/assets/13",
              "browser_download_url": "https://github.com/SomeeName/DotCharter/releases/download/v0.13.0/checksums.txt",
              "size": 128
            }
          ]
        }
        """;

    // ---- version tag parsing --------------------------------------------

    [Theory]
    [InlineData("v0.12.5", "0.12.5")]
    [InlineData("V0.12.5", "0.12.5")]
    [InlineData("0.12.5", "0.12.5")]
    [InlineData("v1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("v1.2.3+build.7", "1.2.3")]
    [InlineData("v1.2.3-rc.1+42", "1.2.3-rc.1")]
    public void TryParseVersionFromTag_ParsesReleaseTags(string tag, string expected) => Assert.Equal(expected, GitHubReleaseRules.TryParseVersionFromTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("release-candidate")]
    [InlineData("v")]
    public void TryParseVersionFromTag_RejectsNonVersions(string? tag) => Assert.Null(GitHubReleaseRules.TryParseVersionFromTag(tag));

    [Fact]
    public void CompareVersions_TreatsMissingComponentsAsZero()
    {
        // Tag "v0.13.0" parses as 0.13.0 (build -1 unset) and must equal the
        // 4-part assembly version 0.13.0.0 — plain Version.CompareTo does not.
        Assert.Equal(0, GitHubReleaseRules.CompareVersions(new Version(0, 13, 0), new Version(0, 13, 0, 0)));
        Assert.True(GitHubReleaseRules.CompareVersions(new Version(0, 13, 1), new Version(0, 13, 0, 0)) > 0);
        Assert.True(GitHubReleaseRules.CompareVersions(new Version(0, 12, 5), new Version(0, 13, 0)) < 0);
        Assert.True(GitHubReleaseRules.CompareVersions(new Version(1, 0), new Version(0, 99, 99)) > 0);
    }

    // ---- release JSON parsing -------------------------------------------

    [Fact]
    public void ParseLatestRelease_ReadsTagNotesUrlAndAssets()
    {
        var release = GitHubReleaseRules.ParseLatestRelease(LatestReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("v0.13.0", release.TagName);
        Assert.Contains("Auto-update", release.Body);
        Assert.Equal("https://github.com/SomeeName/DotCharter/releases/tag/v0.13.0", release.HtmlUrl);
        Assert.False(release.Prerelease);
        Assert.Equal(3, release.Assets.Count);
        Assert.Equal(123456789, release.Assets[1].Size);
        Assert.Equal("https://api.github.com/repos/SomeeName/DotCharter/releases/assets/12",
            release.Assets[1].ApiUrl);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public void ParseLatestRelease_ReturnsNullForUnexpectedJson(string json) => Assert.Null(GitHubReleaseRules.ParseLatestRelease(json));

    // ---- asset selection --------------------------------------------------

    [Fact]
    public void SelectWinX64Asset_PrefersWinX64Zip()
    {
        var release = GitHubReleaseRules.ParseLatestRelease(LatestReleaseJson)!;
        var asset = GitHubReleaseRules.SelectWinX64Asset(release.Assets);

        Assert.NotNull(asset);
        Assert.Equal("DotCharter-v0.13.0-win-x64.zip", asset.Name);
    }

    [Fact]
    public void SelectWinX64Asset_FallsBackToOnlyZip()
    {
        var assets = new[]
        {
            new GitHubReleaseAsset("DotCharter.zip", "api", "browser", 10),
            new GitHubReleaseAsset("notes.txt", "api2", "browser2", 5)
        };

        Assert.Equal("DotCharter.zip", GitHubReleaseRules.SelectWinX64Asset(assets)!.Name);
    }

    [Fact]
    public void SelectWinX64Asset_ReturnsNullWhenAmbiguousOrMissing()
    {
        Assert.Null(GitHubReleaseRules.SelectWinX64Asset([]));
        Assert.Null(GitHubReleaseRules.SelectWinX64Asset(
        [
            new GitHubReleaseAsset("a-macos.zip", "u", "b", 1),
            new GitHubReleaseAsset("b-linux.zip", "u", "b", 1)
        ]));
    }

    /// <summary>
    ///     The standalone rig ships on the same release page: its zip must never be
    ///     the update, whatever order the page lists assets in and even if it were
    ///     named "win-x64" - and a page carrying ONLY a rig zip is no update at all,
    ///     not "the only zip".
    /// </summary>
    [Fact]
    public void SelectWinX64Asset_NeverPicksTheStandaloneRig()
    {
        Assert.Equal("DotCharter-v0.62.0-win-x64.zip", GitHubReleaseRules.SelectWinX64Asset(
        [
            new GitHubReleaseAsset("DotCharter-RigLab-v0.1.0-win-x64.zip", "u", "b", 1),
            new GitHubReleaseAsset("DotCharter-RigLab-v0.1.0.zip", "u", "b", 1),
            new GitHubReleaseAsset("DotCharter-v0.62.0-win-x64.zip", "u", "b", 1)
        ])!.Name);
        Assert.Null(GitHubReleaseRules.SelectWinX64Asset(
            [new GitHubReleaseAsset("DotCharter-RigLab-v0.1.0.zip", "u", "b", 1)]));
    }

    // ---- partial-payload asset rules --------------------------------------

    private static GitHubReleaseAsset Asset(string name, long size = 100) =>
        new(name, "api/" + name, "browser/" + name, size);

    /// <summary>
    ///     The published delta patches are gone (0.43), but the release pages that
    ///     carry them are permanent — v0.41.0 through v0.42.4 each published a
    ///     "…-from-v….zip", and that is named "…-win-x64.zip" too. If the FULL
    ///     selector could return one, an install it was never diffed against would
    ///     be handed a partial payload: the broken-install case.
    /// </summary>
    [Fact]
    public void SelectWinX64Asset_NeverReturnsAPublishedPatch()
    {
        var assets = new[]
        {
            Asset("DotCharter-v0.42.0-from-v0.41.0-win-x64.zip", 9_000_000),
            Asset("DotCharter-v0.42.0-win-x64.zip", 414_000_000),
        };

        Assert.Equal("DotCharter-v0.42.0-win-x64.zip", GitHubReleaseRules.SelectWinX64Asset(assets)!.Name);

        // …and a release with ONLY a patch offers no full asset at all.
        Assert.Null(GitHubReleaseRules.SelectWinX64Asset([assets[0]]));
    }

    /// <summary>
    ///     The locally ASSEMBLED payload lands in the same downloads folder and is
    ///     named for the same version, so it must be equally unselectable — a stray
    ///     one from an interrupted update must never be mistaken for the release.
    /// </summary>
    [Fact]
    public void SelectWinX64Asset_NeverReturnsAnAssembledPartialPayload()
    {
        var partial = GitHubReleaseRules.PartialAssetName(new Version(0, 43, 0));

        Assert.Equal("DotCharter-v0.43.0-partial-win-x64.zip", partial);
        Assert.Null(GitHubReleaseRules.SelectWinX64Asset([Asset(partial, 9_000_000)]));
    }

    /// <summary>The manifest asset is the entire condition for a partial download.</summary>
    [Fact]
    public void SelectManifestAsset_FindsThePayloadManifest()
    {
        Assert.Equal(PayloadManifest.FileName, GitHubReleaseRules.SelectManifestAsset(
        [
            Asset("DotCharter-v0.43.0-win-x64.zip"),
            Asset(PayloadManifest.FileName),
        ])!.Name);

        // A release from before v0.41.0 published none: full download, no partial.
        Assert.Null(GitHubReleaseRules.SelectManifestAsset([Asset("DotCharter-v0.40.0-win-x64.zip")]));
    }

    /// <summary>
    ///     <c>AppPayload.Validate</c> refuses a self-contained payload without the
    ///     host pair, and it runs against the STAGED partial payload. A release
    ///     that changes only managed assemblies leaves those two byte-identical, so
    ///     a purely content-diffed set would be rejected on every user's machine.
    /// </summary>
    [Fact]
    public void TheAlwaysIncludeSetCoversEverythingTheValidatorDemands()
    {
        var problem = AppPayload.Validate(
            name => PayloadManifest.AlwaysInclude.Contains(name, StringComparer.OrdinalIgnoreCase),
            SelfContainedRuntimeConfig,
            InstallLayout.SelfContained);

        Assert.Null(problem);
    }

    private const string SelfContainedRuntimeConfig =
        """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}""";

    // ---- check flow over a fake handler -----------------------------------

    [Fact]
    public async Task CheckForUpdate_NewerRelease_ReportsUpdateAvailable()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LatestReleaseJson);
        using var service = new GitHubUpdateService(new Version(0, 12, 5, 0), handler: handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Update);
        Assert.Equal(new Version(0, 13, 0), result.Update.Version);
        Assert.Equal("DotCharter-v0.13.0-win-x64.zip", result.Update.AssetName);
        Assert.Equal(123456789, result.Update.AssetSizeBytes);

        // Required GitHub API headers.
        Assert.Contains("api.github.com/repos/SomeeName/DotCharter/releases/latest",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", handler.HeaderOf(0, "Accept"));
        Assert.NotNull(handler.HeaderOf(0, "User-Agent"));
        Assert.Null(handler.HeaderOf(0, "Authorization")); // anonymous without a token provider
    }

    [Fact]
    public async Task CheckForUpdate_SameOrOlderRelease_ReportsUpToDate()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LatestReleaseJson);
        using var service = new GitHubUpdateService(new Version(0, 13, 0, 0), handler: handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task CheckForUpdate_WithTokenProvider_SendsBearerAuthorization()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LatestReleaseJson);
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), tokenProvider: () => "ghp_test_token", handler: handler);

        await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer ghp_test_token", handler.HeaderOf(0, "Authorization"));
    }

    [Fact]
    public async Task CheckForUpdate_HttpFailure_ReportsFailedInsteadOfThrowing()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.InternalServerError);
        using var service = new GitHubUpdateService(new Version(0, 12, 5), handler: handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // ---- download + zip verification ---------------------------------------

    [Fact]
    public async Task DownloadUpdate_StreamsZipVerifiesAndReportsProgress()
    {
        using var temp = new TempDir();
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("DotCharter.exe");
                await using var s = entry.Open();
                s.Write(new byte[1024]);
            }

            zipBytes = ms.ToArray();
        }

        var handler = new FakeHttpMessageHandler();
        // The digest resolve comes FIRST now; v0.13.0 predates published digests,
        // so a 404 on the release document is the legal "there are none" answer.
        handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);
        handler.EnqueueBytes(zipBytes);
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), handler: handler, downloadDir: temp.Path);
        var update = new UpdateInfo(new Version(0, 13, 0), "v0.13.0", "notes", "url",
            "DotCharter-v0.13.0-win-x64.zip", "https://api.github.com/assets/12",
            "https://api.github.com/assets/12", zipBytes.Length);

        var progressValues = new List<double>();
        var path = await service.DownloadUpdateAsync(
            update, new SynchronousProgress(progressValues), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(zipBytes.Length, new FileInfo(path).Length);
        Assert.Equal(1.0, progressValues[^1]);
        // Asset downloads must request the raw bytes from the API asset url.
        // Request 0 is the release-document lookup the digest resolve makes.
        Assert.Equal("application/octet-stream", handler.HeaderOf(1, "Accept"));
        Assert.Contains("api.github.com/assets/12", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadUpdate_CorruptZip_ThrowsAndDeletesFile()
    {
        using var temp = new TempDir();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);
        handler.EnqueueBytes([1, 2, 3, 4, 5, 6, 7, 8]);
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), handler: handler, downloadDir: temp.Path);
        var update = new UpdateInfo(new Version(0, 13, 0), "v0.13.0", "notes", "url",
            "broken.zip", "https://api.github.com/assets/13", "https://api.github.com/assets/13", 8);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadUpdateAsync(update, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.zip"));
    }

    // ---- partial (range-fetched) download ------------------------------------

    /// <summary>
    ///     The point of the whole scheme: with the release's
    ///     <c>payload-manifest.json</c> in hand, the updater assembles the payload
    ///     out of range requests against the ONE full zip and never downloads the
    ///     bulk it already has. The result is still a zip, so every step after this
    ///     — bounds check, bounded extraction, layout validation, robocopy — is the
    ///     untouched full-zip path.
    /// </summary>
    [Fact]
    public async Task DownloadUpdate_WithAPayloadManifest_AssemblesTheUpdateFromRanges()
    {
        using var temp = new TempDir();
        var install = Path.Combine(temp.Path, "install");
        var downloads = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(install);

        var release = BuildReleasePayload(install);

        var handler = ServeRelease(release);

        using var service = new GitHubUpdateService(
            new Version(0, 42, 0), handler: handler, installDir: install, downloadDir: downloads);

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHubReleaseRules.PartialAssetName(new Version(0, 43, 0)), Path.GetFileName(path));
        Assert.True(new FileInfo(path).Length < release.Zip.Length / 4,
            $"assembled payload is {new FileInfo(path).Length} of {release.Zip.Length}");

        using var assembled = ZipFile.OpenRead(path);
        var names = assembled.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("app.dll", names);
        // The 4 MB member did not change, so it is exactly what was NOT fetched.
        Assert.DoesNotContain("runtime/big.dll", names);
        // …and the payload still satisfies the validator that runs at apply time.
        Assert.Null(AppPayload.ValidateArchive(assembled, InstallLayout.SelfContained));
        // The manifest rides along so this install can be the FROM side next time.
        Assert.Contains(PayloadManifest.FileName, names);
    }

    /// <summary>
    ///     The fallback that makes attempting any of this safe. A server (or a
    ///     corporate proxy) that ignores <c>Range</c> answers 200 with the whole
    ///     asset; the updater must notice and take the ordinary full download
    ///     rather than parse 414 MB as if it were the slice it asked for.
    /// </summary>
    [Fact]
    public async Task DownloadUpdate_WhenTheServerIgnoresRanges_FallsBackToTheFullAsset()
    {
        using var temp = new TempDir();
        var install = Path.Combine(temp.Path, "install");
        var downloads = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(install);

        var release = BuildReleasePayload(install);

        var handler = ServeRelease(release, honourRanges: false);

        using var service = new GitHubUpdateService(
            new Version(0, 42, 0), handler: handler, installDir: install, downloadDir: downloads);

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(release.Update.AssetName, Path.GetFileName(path));
        Assert.Equal(release.Zip.Length, new FileInfo(path).Length);
        // Nothing half-assembled is left behind for the apply path to trip over.
        Assert.Empty(Directory.GetDirectories(downloads));
    }

    /// <summary>A release from before v0.41.0 publishes no manifest at all: there is
    /// nothing to diff against, so the full asset is the only answer.</summary>
    [Fact]
    public async Task DownloadUpdate_WithoutAPayloadManifest_TakesTheFullAsset()
    {
        using var temp = new TempDir();
        var install = Path.Combine(temp.Path, "install");
        var downloads = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(install);

        var release = BuildReleasePayload(install);

        var handler = ServeRelease(release, withManifest: false);

        using var service = new GitHubUpdateService(
            new Version(0, 42, 0), handler: handler, installDir: install, downloadDir: downloads);

        var path = await service.DownloadUpdateAsync(
            release.Update with { ManifestAssetApiUrl = null },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(release.Update.AssetName, Path.GetFileName(path));
    }

    /// <summary>
    ///     Verification runs against the release manifest BEFORE the payload is
    ///     handed on, so a manifest that does not describe the asset it names must
    ///     produce a full download rather than a payload of wrong bytes.
    /// </summary>
    [Fact]
    public async Task DownloadUpdate_WhenTheManifestDoesNotMatchTheAsset_FallsBackToTheFullAsset()
    {
        using var temp = new TempDir();
        var install = Path.Combine(temp.Path, "install");
        var downloads = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(install);

        var release = BuildReleasePayload(install);
        var lying = release.Manifest with
        {
            Files = release.Manifest.Files
                .Select(f => f.Path == "app.dll" ? f with { Sha256 = new string('f', 64) } : f)
                .ToList()
        };

        var handler = ServeRelease(release with { Manifest = lying });

        using var service = new GitHubUpdateService(
            new Version(0, 42, 0), handler: handler, installDir: install, downloadDir: downloads);

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(release.Update.AssetName, Path.GetFileName(path));
        Assert.Equal(release.Zip.Length, new FileInfo(path).Length);
    }

    private sealed record ReleaseFixture(byte[] Zip, PayloadManifest Manifest, UpdateInfo Update);

    /// <summary>
    ///     The whole release over HTTP: document, digests, manifest, asset. The
    ///     digests are REAL, because the updater refuses a release at or above
    ///     <c>FirstDigestPublishingVersion</c> that cannot produce them — a
    ///     partial-update test that scripted only the manifest used to work and
    ///     now would have the release lookup eat its one scripted response.
    /// </summary>
    private static FakeHttpMessageHandler ServeRelease(
        ReleaseFixture release, bool honourRanges = true, bool withManifest = true)
    {
        var manifestBytes = withManifest
            ? System.Text.Encoding.UTF8.GetBytes(release.Manifest.ToJson())
            : null;

        var digested = new List<(string, byte[])> { (release.Update.AssetName, release.Zip) };
        if (manifestBytes != null) digested.Add((PayloadManifest.FileName, manifestBytes));

        return FakeReleaseHttp.Serve(
            release.Update.TagName, release.Update.AssetName, release.Zip, manifestBytes,
            FakeReleaseHttp.Digests(digested.ToArray()), honourRanges);
    }

    /// <summary>
    ///     A v0.43.0 release (five host files, a changed <c>app.dll</c>, an
    ///     unchanged 4 MB member) plus a v0.42.0 install of the same payload,
    ///     written into <paramref name="installDir" /> with its own
    ///     <c>payload-manifest.json</c> — exactly what a real install carries.
    /// </summary>
    private static ReleaseFixture BuildReleasePayload(string installDir)
    {
        var files = FakeRelease.HostFiles("0.43.0");
        files["app.dll"] = FakeRelease.Content("app-new", 3000);
        files["runtime/big.dll"] = FakeRelease.Content("runtime", 4_000_000);

        var manifest = FakeRelease.Manifest("0.43.0", files);
        var zip = FakeRelease.Zip(files, "DotCharter-v0.43.0-win-x64");

        var installedFiles = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase)
        {
            ["app.dll"] = FakeRelease.Content("app-old", 2500),
        };
        foreach (var (name, content) in installedFiles)
        {
            var target = Path.Combine(installDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        File.WriteAllText(Path.Combine(installDir, PayloadManifest.FileName),
            FakeRelease.Manifest("0.42.0", installedFiles).ToJson());

        var update = new UpdateInfo(
            new Version(0, 43, 0), "v0.43.0", "notes", "url",
            "DotCharter-v0.43.0-win-x64.zip", FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, zip.Length,
            FakeReleaseHttp.ManifestUrl);

        return new ReleaseFixture(zip, manifest, update);
    }

    // ---- apply gating -------------------------------------------------------

    [Fact]
    public void GetApplyBlockReason_NoExeInInstallDir_ReportsNotAReleaseInstall()
    {
        using var temp = new TempDir();
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), installDir: temp.Path, downloadDir: temp.Path);

        Assert.Equal(UpdateApplyBlockReason.NotAReleaseInstall, service.GetApplyBlockReason());
    }

    [Fact]
    public void GetApplyBlockReason_ReleaseLayoutInWritableDir_ReportsNone()
    {
        using var temp = new TempDir();
        WriteSelfContainedInstall(temp.Path);
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), installDir: temp.Path, downloadDir: temp.Path);

        Assert.Equal(UpdateApplyBlockReason.None, service.GetApplyBlockReason());
    }

    /// <summary>An exe with no readable runtimeconfig tells us nothing about which
    /// payloads are safe to copy over it, so in-place apply must stay disabled.</summary>
    [Fact]
    public void GetApplyBlockReason_ExeWithoutRuntimeConfig_ReportsUnknownInstallLayout()
    {
        using var temp = new TempDir();
        File.WriteAllBytes(Path.Combine(temp.Path, AppPayload.ExeName), [0x4D, 0x5A]);
        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), installDir: temp.Path, downloadDir: temp.Path);

        Assert.Equal(UpdateApplyBlockReason.UnknownInstallLayout, service.GetApplyBlockReason());
    }

    /// <summary>The failure that bricked a LAN-updated install: a framework-dependent
    /// payload copied over a self-contained one. Nothing may be staged or launched.</summary>
    [Fact]
    public void ApplyDownloadedUpdate_CrossLayoutPayload_ThrowsAndLeavesNoStaging()
    {
        using var temp = new TempDir();
        WriteSelfContainedInstall(temp.Path);
        var zipPath = Path.Combine(temp.Path, "update.zip");
        BuildPayloadZip(zipPath, selfContained: false);

        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), installDir: temp.Path, downloadDir: temp.Path);

        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));

        Assert.Contains("framework-dependent", error.Message);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, UpdaterScriptBuilder.StagingDirName)));
        Assert.False(File.Exists(Path.Combine(temp.Path, UpdaterScriptBuilder.ScriptFileName)));
    }

    [Fact]
    public void ApplyDownloadedUpdate_PayloadMissingRuntimeConfig_ThrowsAndLeavesNoStaging()
    {
        using var temp = new TempDir();
        WriteSelfContainedInstall(temp.Path);
        var zipPath = Path.Combine(temp.Path, "update.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry(AppPayload.ExeName);

        using var service = new GitHubUpdateService(
            new Version(0, 12, 5), installDir: temp.Path, downloadDir: temp.Path);

        Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(zipPath));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, UpdaterScriptBuilder.StagingDirName)));
    }

    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private const string FrameworkDependentConfig =
        """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""";

    private static void WriteSelfContainedInstall(string dir)
    {
        File.WriteAllBytes(Path.Combine(dir, AppPayload.ExeName), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(dir, AppPayload.RuntimeConfigName), SelfContainedConfig);
        File.WriteAllText(Path.Combine(dir, AppPayload.DepsName), "{}");
        File.WriteAllBytes(Path.Combine(dir, AppPayload.HostFxrName), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, AppPayload.CoreClrName), [0x4D, 0x5A]);
    }

    private static void BuildPayloadZip(string zipPath, bool selfContained)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Add(AppPayload.ExeName, "MZ");
        Add(AppPayload.DepsName, "{}");
        Add(AppPayload.RuntimeConfigName, selfContained ? SelfContainedConfig : FrameworkDependentConfig);
        if (selfContained)
        {
            Add(AppPayload.HostFxrName, "MZ");
            Add(AppPayload.CoreClrName, "MZ");
        }

        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    [Fact]
    public void ResolvePayloadRoot_UnwrapsSingleTopLevelFolder()
    {
        using var temp = new TempDir();
        var nested = Path.Combine(temp.Path, "DotCharter-v0.13.0-win-x64");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "DotCharter.exe"), [0x4D, 0x5A]);

        Assert.Equal(nested, UpdateInstaller.ResolvePayloadRoot(temp.Path));
    }

    [Fact]
    public void ResolvePayloadRoot_FlatLayout_ReturnsStagingRoot()
    {
        using var temp = new TempDir();
        File.WriteAllBytes(Path.Combine(temp.Path, "DotCharter.exe"), [0x4D, 0x5A]);

        Assert.Equal(temp.Path, UpdateInstaller.ResolvePayloadRoot(temp.Path));
    }

    // ---- optional live check (not part of the gate) -------------------------

    [Fact]
    public async Task LiveCheckAgainstRealRepo_EnvGated()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("DOTCHARTER_LIVE_UPDATE_TEST") == "1",
            "Live network test — set DOTCHARTER_LIVE_UPDATE_TEST=1 to run manually. " +
            "For a private repo also set DOTCHARTER_LIVE_UPDATE_TOKEN.");

        // Optional token so the live check also works against a private repo
        // (the DotCharter repo went private in 07/2026).
        var token = Environment.GetEnvironmentVariable("DOTCHARTER_LIVE_UPDATE_TOKEN");
        using var service = new GitHubUpdateService(
            new Version(0, 0, 1),
            tokenProvider: string.IsNullOrWhiteSpace(token) ? null : () => token);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(token) && result.Status == UpdateCheckStatus.Failed
                                             && result.Error?.Contains("404") == true,
            "Repo is private and no DOTCHARTER_LIVE_UPDATE_TOKEN was provided.");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Update);
        Assert.EndsWith(".zip", result.Update.AssetName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("win-x64", result.Update.AssetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Range-fetches a REAL release asset off GitHub and reports what a
    ///     partial update actually costs. Not part of the gate (it needs the
    ///     network and a token for the private repo) — this is the measurement
    ///     harness the design rests on, kept runnable so the claim can be
    ///     re-checked rather than believed.
    /// </summary>
    [Fact]
    public async Task LivePartialFetchAgainstRealRelease_EnvGated()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("DOTCHARTER_LIVE_PARTIAL_TEST") == "1",
            "Live network test — set DOTCHARTER_LIVE_PARTIAL_TEST=1 (plus " +
            "DOTCHARTER_LIVE_UPDATE_TOKEN for the private repo, and optionally " +
            "DOTCHARTER_LIVE_PARTIAL_FROM=<x.y.z>) to run manually.");

        var token = Environment.GetEnvironmentVariable("DOTCHARTER_LIVE_UPDATE_TOKEN");
        var fromTag = "v" + (Environment.GetEnvironmentVariable("DOTCHARTER_LIVE_PARTIAL_FROM") ?? "0.42.3");
        var cancellationToken = TestContext.Current.CancellationToken;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var http = new HttpClient(handler);

        HttpRequestMessage Request(string url, string accept)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("User-Agent", "DotCharter-live-partial-test");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return request;
        }

        async Task<string> GetAsync(string url, string accept)
        {
            using var request = Request(url, accept);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        const string repo = "https://api.github.com/repos/" +
                            GitHubUpdateService.DefaultOwner + "/" + GitHubUpdateService.DefaultRepo;

        var latest = GitHubReleaseRules.ParseLatestRelease(
            await GetAsync(repo + "/releases/latest", "application/vnd.github+json"))!;
        var previous = GitHubReleaseRules.ParseLatestRelease(
            await GetAsync(repo + "/releases/tags/" + fromTag, "application/vnd.github+json"))!;

        var asset = GitHubReleaseRules.SelectWinX64Asset(latest.Assets)!;
        var releaseManifest = PayloadManifest.FromJson(
            await GetAsync(GitHubReleaseRules.SelectManifestAsset(latest.Assets)!.ApiUrl,
                "application/octet-stream"))!;
        var installedManifest = PayloadManifest.FromJson(
            await GetAsync(GitHubReleaseRules.SelectManifestAsset(previous.Assets)!.ApiUrl,
                "application/octet-stream"))!;

        using var staging = new TempDir();
        var downloader = new PartialUpdateDownloader(new HttpAssetRangeFetcher(http, Request));
        var result = await downloader.FetchAsync(
            asset.ApiUrl, asset.Size, releaseManifest,
            new InstalledPayload(installedManifest, InstalledPayloadSource.ShippedManifest),
            staging.Path, cancellationToken: cancellationToken);

        Assert.Equal(PartialUpdateOutcome.Fetched, result.Outcome);
        Assert.Null(AppPayload.ValidateDirectory(staging.Path, InstallLayout.SelfContained));
        // Under HALF the asset, not a quarter: v0.59.0 -> v0.60.0 really changed two
        // 160 MB members (the rig assembly and ffmpeg), 240.8 of 935.2 MB (#220). The
        // product's own NotWorthIt rule decides when a partial fetch is not worth it.
        Assert.True(result.BytesFetched < asset.Size / 2,
            $"fetched {result.BytesFetched:N0} of {asset.Size:N0}");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{fromTag} -> {latest.TagName}: {result.FilesWritten} file(s), " +
            $"{result.BytesFetched / (1024.0 * 1024.0):N1} MB fetched of " +
            $"{asset.Size / (1024.0 * 1024.0):N0} MB, {result.Deletions.Count} deletion(s).");
    }

    private sealed class SynchronousProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dc-update-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => BestEffortDelete.Directory(Path);
    }
}
