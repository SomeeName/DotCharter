using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     What the updater believes this machine already has. Everything a partial
///     download SKIPS rests on this answer, and the verify step cannot catch a
///     mistake here — it only ever sees the files that were actually fetched.
/// </summary>
public sealed class InstalledPayloadTests
{
    private static PayloadManifest M(params PayloadFile[] files) =>
        new("0.42.0", InstallLayout.SelfContained, files);

    // ---- the shipped manifest ----------------------------------------------

    [Fact]
    public void TheInstallsOwnManifestIsUsedWhenItIsThere()
    {
        var shipped = M(new PayloadFile("app.dll", "abc", 10));
        var release = M(new PayloadFile("app.dll", "def", 10));

        var installed = InstalledPayloadReader.Resolve(
            release, () => shipped.ToJson(), _ => 10, _ => "abc");

        Assert.Equal(InstalledPayloadSource.ShippedManifest, installed.Source);
        // The install's own recorded hash, not the release's — that difference is
        // exactly what the delta is about to act on.
        Assert.Equal("abc", Assert.Single(installed.Manifest.Files).Sha256);
    }

    /// <summary>
    ///     An install is a directory a user can touch — a hand-repaired DLL, a
    ///     half-finished copy, a file an AV tool quarantined. Trusting the manifest
    ///     for those would SKIP the very file that has to be re-fetched, so one
    ///     stat per file closes the size-changing half of the gap.
    /// </summary>
    [Fact]
    public void AFileThatChangedSizeSinceInstall_IsNoLongerClaimed()
    {
        var shipped = M(
            new PayloadFile("kept.dll", "a", 10),
            new PayloadFile("resized.dll", "b", 10),
            new PayloadFile("deleted.dll", "c", 10));

        var reconciled = InstalledPayloadReader.Reconcile(
            shipped,
            path => path switch
            {
                "kept.dll" => 10,
                "resized.dll" => 99,
                _ => null
            },
            path => path switch { "kept.dll" => "a", _ => "b" });

        Assert.Equal(new[] { "kept.dll" }, reconciled.Files.Select(f => f.Path).ToArray());
    }

    /// <summary>
    ///     The half the size probe could never see, and the reason it mattered:
    ///     a file swapped in place at the SAME length was reported as
    ///     already-present by this update, by every later partial update, and
    ///     forever — nothing else in the app re-fetches a file the manifest claims.
    /// </summary>
    [Fact]
    public void AFileWhoseBytesChangedButWhoseSizeDidNotIsStillOfferedForRefetch()
    {
        var shipped = M(
            new PayloadFile("kept.dll", "a", 10),
            new PayloadFile("swapped.dll", "b", 10));

        var reconciled = InstalledPayloadReader.Reconcile(
            shipped,
            _ => 10,
            path => path == "swapped.dll" ? "somebody-elses-bytes" : "a");

        Assert.Equal(new[] { "kept.dll" }, reconciled.Files.Select(f => f.Path).ToArray());
    }

    /// <summary>An entry we cannot READ is dropped as well — re-fetching costs
    /// bandwidth, trusting it costs the install.</summary>
    [Fact]
    public void AFileWhoseHashCannotBeReadIsStillOfferedForRefetch()
    {
        var shipped = M(new PayloadFile("locked.dll", "a", 10));

        var reconciled = InstalledPayloadReader.Reconcile(shipped, _ => 10, _ => null);

        Assert.Empty(reconciled.Files);
    }

    /// <summary>The stat runs FIRST, so a file of the wrong length is never read.
    /// That is what keeps the content check affordable on a 414 MB install.</summary>
    [Fact]
    public void AFileOfTheWrongSizeIsNeverHashedDuringReconcile()
    {
        var shipped = M(
            new PayloadFile("kept.dll", "a", 10),
            new PayloadFile("resized.dll", "b", 10));
        var hashed = new List<string>();

        InstalledPayloadReader.Reconcile(
            shipped,
            path => path == "kept.dll" ? 10 : 99,
            path =>
            {
                hashed.Add(path);
                return "a";
            });

        Assert.Equal(new[] { "kept.dll" }, hashed.ToArray());
    }

    [Fact]
    public void AnUnchangedInstallReconcilesToTheSameManifestInstance()
    {
        var shipped = M(new PayloadFile("app.dll", "a", 10));

        Assert.Same(shipped, InstalledPayloadReader.Reconcile(shipped, _ => 10, _ => "a"));
    }

    [Fact]
    public void AnUnreadableManifestFallsThroughToHashing()
    {
        var release = M(new PayloadFile("app.dll", "def", 10));

        var installed = InstalledPayloadReader.Resolve(
            release, () => "{ this is not json", _ => 10, _ => "def");

        Assert.Equal(InstalledPayloadSource.Hashed, installed.Source);
        Assert.Equal("def", Assert.Single(installed.Manifest.Files).Sha256);
    }

    // ---- the hashed fallback ------------------------------------------------

    /// <summary>
    ///     <b>The hashed inventory is built from the RELEASE's path list, never by
    ///     walking the install directory.</b> On a portable install
    ///     <c>WritablePaths.ConfigRoot</c> IS the install directory, so a directory
    ///     walk would inventory the user's <c>appconfig.json</c>, profiles,
    ///     keybinds and crash reports — none of which are in the release — and the
    ///     diff would then hand the updater a deletion list that erases them.
    /// </summary>
    [Fact]
    public void TheHashedInventoryOnlyEverLooksAtFilesTheReleaseLists()
    {
        var release = M(new PayloadFile("app.dll", "new", 10));
        var probed = new List<string>();

        var installed = InstalledPayloadReader.Resolve(
            release, () => null,
            path =>
            {
                probed.Add(path);
                return 10;
            },
            _ => "old");

        Assert.Equal(new[] { "app.dll" }, probed.ToArray());
        Assert.Equal(new[] { "app.dll" }, installed.Manifest.Files.Select(f => f.Path).ToArray());
    }

    /// <summary>…and because of that, it is never entitled to say a file was
    /// dropped. The flag is the explicit half of the same guarantee.</summary>
    [Fact]
    public void AHashedInventoryIsNotEntitledToDeriveDeletions()
    {
        var release = M(new PayloadFile("app.dll", "new", 10));

        var hashed = InstalledPayloadReader.Resolve(release, () => null, _ => 10, _ => "old");
        var shipped = InstalledPayloadReader.Resolve(release, () => M().ToJson(), _ => 10, _ => "old");

        Assert.False(hashed.CanDeriveDeletions);
        Assert.True(shipped.CanDeriveDeletions);

        // …and structurally: nothing the hashed inventory holds is absent from the
        // release, so PayloadDelta cannot produce a deletion out of it either.
        Assert.Empty(PayloadDelta.Between(hashed.Manifest, release).Deleted);
    }

    /// <summary>A file whose size already differs cannot be the same content, so
    /// hashing it would be pure cost — on a 414 MB install that is the whole
    /// difference between a usable fallback and an unusable one.</summary>
    [Fact]
    public void AFileOfADifferentSizeIsNotHashedAtAll()
    {
        var release = M(new PayloadFile("app.dll", "new", 10), new PayloadFile("other.dll", "x", 20));
        var hashed = new List<string>();

        InstalledPayloadReader.Resolve(release, () => null,
            path => path == "app.dll" ? 10 : 999,
            path =>
            {
                hashed.Add(path);
                return "old";
            });

        Assert.Equal(new[] { "app.dll" }, hashed.ToArray());
    }

    // ---- against a real directory -------------------------------------------

    [Fact]
    public void FromDirectory_ReadsAndReconcilesARealInstall()
    {
        using var install = new TempDirectory();
        var content = FakeRelease.Content("app", 128);
        File.WriteAllBytes(Path.Combine(install.Path, "app.dll"), content);

        var release = M(
            new PayloadFile("app.dll", FakeRelease.Sha256(content), content.Length),
            new PayloadFile("absent.dll", "nope", 4));

        var installed = InstalledPayloadReader.FromDirectory(release, install.Path);

        Assert.Equal(InstalledPayloadSource.Hashed, installed.Source);
        var only = Assert.Single(installed.Manifest.Files);
        Assert.Equal("app.dll", only.Path);
        Assert.Equal(FakeRelease.Sha256(content), only.Sha256);
    }

    /// <summary>A manifest path is relative and forward-slashed BY CONTRACT. One
    /// that is not must resolve to nothing rather than to a file outside the
    /// install directory.</summary>
    [Fact]
    public void FromDirectory_RefusesAPathThatWouldLeaveTheInstall()
    {
        using var install = new TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "dc-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(outside, [1, 2, 3, 4]);
        try
        {
            var release = M(new PayloadFile("../" + Path.GetFileName(outside), "x", 4));

            var installed = InstalledPayloadReader.FromDirectory(release, install.Path);

            Assert.Empty(installed.Manifest.Files);
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
