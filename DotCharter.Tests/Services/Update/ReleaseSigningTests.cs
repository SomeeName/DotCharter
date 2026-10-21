using System.Text;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The updater trusted a zip because it matched <c>SHA256SUMS.txt</c> from
///     the SAME release, so anyone able to publish a release could publish a matching
///     pair. These pin the publisher signature (<see cref="ReleaseSignature" />), the
///     re-hash of the zip at apply, and the pending-update record's paths.
/// </summary>
public sealed class ReleaseSigningTests : IDisposable
{
    private const string Tag = "v1.1.0";
    private const string AssetName = "DotCharter-v1.1.0-win-x64.zip";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---- the signature itself ---------------------------------------------------

    [Fact]
    public void ThePinnedKey_IsNonEmpty_AndParsesAsAnEd25519PublicKey()
    {
        Assert.False(string.IsNullOrWhiteSpace(ReleaseSignature.PinnedPublicKeyBase64));
        var key = ReleaseSignature.PinnedPublicKey();
        Assert.Equal(ReleaseSignature.KeyBytes, key.Length);
        Assert.True(ReleaseSignature.IsValidPublicKey(key));
        // …and it is not the tests' key, which would mean the app trusts a key
        // whose private half is in this repository.
        Assert.NotEqual(TestReleaseSigning.PublicKey, key);
    }

    [Fact]
    public void ASignedManifestForItsOwnTag_Verifies()
    {
        var (manifest, signature) = Signed();
        Assert.Null(ReleaseSignature.Check(manifest, signature, Tag, TestReleaseSigning.PublicKey));
    }

    [Fact]
    public void AMissingSignature_IsRefused()
    {
        var (manifest, _) = Signed();
        Assert.Contains("publishes no", ReleaseSignature.Check(manifest, null, Tag, TestReleaseSigning.PublicKey));
        Assert.NotNull(ReleaseSignature.Check(manifest, [], Tag, TestReleaseSigning.PublicKey));
    }

    [Fact]
    public void ASignatureByAnotherKey_IsRefused()
    {
        var (manifest, _) = Signed();
        var forged = Encoding.ASCII.GetBytes(
            TestReleaseSigning.Sign(Encoding.ASCII.GetString(manifest), TestReleaseSigning.OtherPrivateKey));

        Assert.Contains("does not match its signature",
            ReleaseSignature.Check(manifest, forged, Tag, TestReleaseSigning.PublicKey));
    }

    /// <summary>A valid signature, over a DIFFERENT hash: the release page swapped
    /// the digest and kept the old signature.</summary>
    [Fact]
    public void AValidSignatureOverADifferentHash_IsRefused()
    {
        var (manifest, signature) = Signed();
        var tampered = Encoding.ASCII.GetBytes(
            Encoding.ASCII.GetString(manifest).Replace(new string('1', 64), new string('2', 64), StringComparison.Ordinal));
        Assert.NotEqual(manifest, tampered);

        Assert.NotNull(ReleaseSignature.Check(tampered, signature, Tag, TestReleaseSigning.PublicKey));
    }

    /// <summary>An older release's signed manifest replayed under a newer tag.</summary>
    [Fact]
    public void ASignedManifestOfAnotherRelease_IsRefused()
    {
        var (manifest, signature) = Signed("v1.0.0");
        Assert.Contains("belongs to v1.0.0",
            ReleaseSignature.Check(manifest, signature, Tag, TestReleaseSigning.PublicKey));
    }

    [Fact]
    public void ASignedManifestThatNamesNoRelease_IsRefused()
    {
        var body = $"{new string('1', 64)}  {AssetName}\n";
        var signature = Encoding.ASCII.GetBytes(TestReleaseSigning.Sign(body));
        Assert.Contains("does not say which release",
            ReleaseSignature.Check(Encoding.ASCII.GetBytes(body), signature, Tag, TestReleaseSigning.PublicKey));
    }

    [Fact]
    public void AGarbageSignature_IsRefused()
    {
        var (manifest, _) = Signed();
        Assert.Contains("not a base64",
            ReleaseSignature.Check(manifest, "not base64!"u8.ToArray(), Tag, TestReleaseSigning.PublicKey));
    }

    // ---- the download refuses an unsigned or mis-signed release -------------------

    [Fact]
    public async Task ASignedRelease_Downloads()
    {
        var zip = PayloadZip();
        using var service = NewService(FakeReleaseHttp.Serve(Tag, AssetName, zip,
            checksums: FakeReleaseHttp.Digests((AssetName, zip))));

        var path = await service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(zip, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AReleaseWithNoSignature_IsRefusedAndLeavesNothing()
    {
        var zip = PayloadZip();
        using var service = NewService(FakeReleaseHttp.Serve(Tag, AssetName, zip,
            checksums: FakeReleaseHttp.Digests((AssetName, zip)), signature: FakeReleaseHttp.Unsigned));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("publishes no", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DownloadDir, AssetName)));
    }

    [Fact]
    public async Task AReleaseSignedWithTheWrongKey_IsRefused()
    {
        var zip = PayloadZip();
        var digests = TestReleaseSigning.Bind(FakeReleaseHttp.Digests((AssetName, zip)), Tag);
        using var service = NewService(FakeReleaseHttp.Serve(Tag, AssetName, zip, checksums: digests,
            signature: TestReleaseSigning.Sign(digests, TestReleaseSigning.OtherPrivateKey)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not match its signature", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DownloadDir, AssetName)));
    }

    /// <summary>The PRODUCT default is the pinned key: a release signed with any
    /// other key (here, the tests') is refused by a service built without one.</summary>
    [Fact]
    public async Task TheServiceDefaultsToThePinnedKey()
    {
        var zip = PayloadZip();
        using var service = new GitHubUpdateService(new Version(1, 0, 0),
            handler: FakeReleaseHttp.Serve(Tag, AssetName, zip, checksums: FakeReleaseHttp.Digests((AssetName, zip))),
            installDir: InstallDir(), downloadDir: DownloadDir);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>A signed manifest whose hash is not the zip's: the digest check
    /// still refuses — a signature vouches for the hash, not for whatever arrives.</summary>
    [Fact]
    public async Task ASignedManifestOverADifferentZip_IsRefused()
    {
        var zip = PayloadZip();
        var other = PayloadZip("other");
        using var service = NewService(FakeReleaseHttp.Serve(Tag, AssetName, zip,
            checksums: FakeReleaseHttp.Digests((AssetName, other))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not the build it claims to be", error.Message, StringComparison.Ordinal);
    }

    // ---- the zip is re-hashed at apply -------------------------------------------

    [Fact]
    public async Task AZipAlteredAfterVerification_IsRefusedAtApply_BeforeAnythingIsStaged()
    {
        var zip = PayloadZip();
        using var service = NewService(FakeReleaseHttp.Serve(Tag, AssetName, zip,
            checksums: FakeReleaseHttp.Digests((AssetName, zip))));
        var path = await service.DownloadUpdateAsync(Update(), cancellationToken: TestContext.Current.CancellationToken);

        File.WriteAllBytes(path, PayloadZip("swapped after the check"));

        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(path));
        Assert.Contains("changed after it was verified", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UpdaterScriptBuilder.StagingRootFor(InstallDir())));
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(InstallDir())));
    }

    [Fact]
    public void AZipThisSessionNeverVerified_IsRefusedAtApply()
    {
        var path = Path.Combine(DownloadDir, AssetName);
        Directory.CreateDirectory(DownloadDir);
        File.WriteAllBytes(path, PayloadZip());
        using var service = NewService(new FakeHttpMessageHandler());

        var error = Assert.Throws<InvalidDataException>(() => service.ApplyDownloadedUpdate(path));
        Assert.Contains("not an update this session downloaded", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UpdaterScriptBuilder.StagingRootFor(InstallDir())));
    }

    // ---- the pending-update record's paths ---------------------------------------

    [Fact]
    public void ARecordWhosePayloadIsOutsideTheStagingRoot_IsNotResumable_AndDiscardLeavesItAlone()
    {
        var install = InstallDir();
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        WriteFiles(elsewhere, FakeRelease.HostFiles("evil"));
        WriteRecord(install, new UpdateApplyState
        {
            PayloadDir = elsewhere,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install),
        });
        var installer = NewInstaller(install);

        var pending = installer.GetPendingUpdateApply();

        Assert.NotNull(pending);
        Assert.False(pending.CanResume);
        Assert.Contains("outside this install's staging folder", pending.Problem, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(installer.ResumePendingUpdateApply);

        installer.DiscardPendingUpdateApply();
        Assert.True(File.Exists(Path.Combine(elsewhere, AppPayload.ExeName)), "discard deleted a path the record named");
    }

    [Fact]
    public void ARecordNamingAForeignStagingRootOrZip_DeletesNeitherOnDiscard()
    {
        var install = InstallDir();
        var victimDir = Path.Combine(_temp.Path, "victim");
        WriteFiles(victimDir, FakeRelease.HostFiles("victim"));
        var victimZip = Path.Combine(_temp.Path, "victim.zip");
        File.WriteAllText(victimZip, "keep me");

        WriteRecord(install, new UpdateApplyState
        {
            PayloadDir = Path.Combine(victimDir),
            StagingRoot = victimDir,
            ZipPath = victimZip,
        });
        var installer = NewInstaller(install);
        Assert.False(installer.GetPendingUpdateApply()!.CanResume);

        installer.DiscardPendingUpdateApply();

        Assert.True(Directory.Exists(victimDir));
        Assert.True(File.Exists(victimZip));
        Assert.False(File.Exists(UpdaterScriptBuilder.PendingStatePathFor(install)));
    }

    [Fact]
    public void ARecordWhoseZipIsOutsideTheDownloadFolder_IsNotResumable_AndTheZipSurvivesDiscard()
    {
        var install = InstallDir();
        var payload = Path.Combine(UpdaterScriptBuilder.StagingRootFor(install), "DotCharter-v1.1.0-win-x64");
        WriteFiles(payload, FakeRelease.HostFiles("new"));
        var victimZip = Path.Combine(_temp.Path, "victim.zip");
        File.WriteAllText(victimZip, "keep me");
        WriteRecord(install, new UpdateApplyState
        {
            PayloadDir = payload,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install),
            ZipPath = victimZip,
        });
        var installer = NewInstaller(install);

        var pending = installer.GetPendingUpdateApply()!;
        Assert.False(pending.CanResume);
        Assert.Contains("outside the update download folder", pending.Problem, StringComparison.Ordinal);

        installer.DiscardPendingUpdateApply();
        Assert.True(File.Exists(victimZip));
        Assert.False(Directory.Exists(UpdaterScriptBuilder.StagingRootFor(install)));
    }

    /// <summary>The receipt filter runs on RESUME too: the staged tree sat on disk
    /// since the apply checked it.</summary>
    [Fact]
    public void AStagedPayloadCarryingUserData_IsNotResumable()
    {
        var install = InstallDir();
        var payload = Path.Combine(UpdaterScriptBuilder.StagingRootFor(install), "DotCharter-v1.1.0-win-x64");
        WriteFiles(payload, FakeRelease.HostFiles("new"));
        File.WriteAllText(Path.Combine(payload, "appconfig.json"), "{}");
        WriteRecord(install, new UpdateApplyState
        {
            PayloadDir = payload,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install),
        });
        var installer = NewInstaller(install);

        var pending = installer.GetPendingUpdateApply()!;

        Assert.False(pending.CanResume);
        Assert.Contains("appconfig.json", pending.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(installer.ResumePendingUpdateApply);
    }

    [Fact]
    public void AWellFormedRecord_IsStillResumable()
    {
        var install = InstallDir();
        var payload = Path.Combine(UpdaterScriptBuilder.StagingRootFor(install), "DotCharter-v1.1.0-win-x64");
        WriteFiles(payload, FakeRelease.HostFiles("new"));
        Directory.CreateDirectory(DownloadDir);
        var zip = Path.Combine(DownloadDir, AssetName);
        File.WriteAllText(zip, "x");
        WriteRecord(install, new UpdateApplyState
        {
            PayloadDir = payload,
            StagingRoot = UpdaterScriptBuilder.StagingRootFor(install) + "\\",
            ZipPath = zip,
        });

        var pending = NewInstaller(install).GetPendingUpdateApply()!;

        Assert.True(pending.CanResume, pending.Problem);
    }

    // ---- the updater script ------------------------------------------------------

    [Fact]
    public void TheScript_GetsAnUnpredictableNewName_InThePerUserFolder()
    {
        var dir = Path.Combine(_temp.Path, "scripts");

        var first = UpdaterScriptBuilder.CreateScriptFile(dir, "@echo off\r\n");
        var second = UpdaterScriptBuilder.CreateScriptFile(dir, "@echo off\r\n");

        Assert.NotEqual(first, second);
        Assert.Equal(dir, Path.GetDirectoryName(first));
        Assert.StartsWith(UpdaterScriptBuilder.ScriptFilePrefix, Path.GetFileName(first), StringComparison.Ordinal);
        Assert.True(Path.GetFileNameWithoutExtension(first).Length >= UpdaterScriptBuilder.ScriptFilePrefix.Length + 32);
        Assert.NotEqual(UpdaterScriptBuilder.ScriptFileName, Path.GetFileName(first));

        // The default lives under the user's own temp folder, never beside the install.
        Assert.StartsWith(Path.GetTempPath(), UpdaterScriptBuilder.DefaultScriptDirectory(), StringComparison.OrdinalIgnoreCase);

        UpdaterScriptBuilder.DeleteStaleScripts(dir);
        Assert.Empty(Directory.GetFiles(dir));
    }

    // ---- helpers -----------------------------------------------------------------

    private string DownloadDir => Path.Combine(_temp.Path, "downloads");

    private string InstallDir()
    {
        var install = Path.Combine(_temp.Path, "install");
        if (!Directory.Exists(install)) WriteFiles(install, FakeRelease.HostFiles("old"));
        return install;
    }

    private GitHubUpdateService NewService(FakeHttpMessageHandler handler)
        => new(new Version(1, 0, 0), handler: handler, installDir: InstallDir(), downloadDir: DownloadDir,
            releaseSigningKey: TestReleaseSigning.PublicKey);

    private UpdateInstaller NewInstaller(string install)
        => new(install, downloadDir: DownloadDir, scriptDir: Path.Combine(_temp.Path, "scripts"));

    private static UpdateInfo Update()
        => new(new Version(1, 1, 0), Tag, "notes", "https://example/r",
            AssetName, FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, 0);

    private static byte[] PayloadZip(string seed = "new")
        => FakeRelease.Zip(FakeRelease.HostFiles(seed), "DotCharter-v1.1.0-win-x64");

    private static (byte[] Manifest, byte[] Signature) Signed(string tag = Tag)
    {
        var body = TestReleaseSigning.Bind($"{new string('1', 64)}  {AssetName}\n", tag);
        return (Encoding.ASCII.GetBytes(body), Encoding.ASCII.GetBytes(TestReleaseSigning.Sign(body)));
    }

    private static void WriteFiles(string dir, IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }

    private static void WriteRecord(string install, UpdateApplyState state)
        => File.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(install), state.ToJson());
}
