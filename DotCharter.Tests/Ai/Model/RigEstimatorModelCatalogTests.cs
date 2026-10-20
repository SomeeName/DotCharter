using DotCharter.Services.Ai.Match.Model;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The catalogue is four facts and every one of them can be wrong in a way
///     that ships silently: an install path nothing reads, an asset name no
///     release carries, a hash that is a placeholder, and a root that roams.
/// </summary>
public sealed class RigEstimatorModelCatalogTests
{
    /// <summary>
    ///     The whole point of the lane. These are two independent constants in
    ///     two files, and the installer writing into a folder the estimator does
    ///     not read would look exactly like a download that never happened.
    /// </summary>
    [Fact]
    public void TheCatalogueDirectoryIsTheDirectoryTheEstimatorLoadsFrom() => Assert.Equal(RigEstimatorOnnx.DefaultModelDirectory, RigEstimatorModelCatalog.ModelDirectory);

    /// <summary>
    ///     <c>LocalApplicationData</c>, NOT <c>ApplicationData</c>. The lyric
    ///     installer's roots are <c>%APPDATA%</c> and a reused installer that
    ///     kept them would have written a 200 MB roaming profile item into a
    ///     folder nothing reads.
    /// </summary>
    [Fact]
    public void TheRootIsLocalApplicationDataNotTheRoamingProfile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(local, RigEstimatorModelCatalog.ModelRoot, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(local, roaming, StringComparison.OrdinalIgnoreCase))
            Assert.DoesNotContain(roaming, RigEstimatorModelCatalog.ModelRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The version lives in the asset NAME, because the fetcher scans
    ///     releases for the name rather than pinning a tag — that is what keeps
    ///     re-uploading unchanged weights optional on every app release.
    /// </summary>
    [Fact]
    public void TheAssetIsVersionedInItsName()
    {
        var download = RigEstimatorModelCatalog.Download;

        Assert.Equal(RigEstimatorModelCatalog.RigEstimatorId, download.Id);
        Assert.Equal("rig-estimator-v1.zip", download.AssetName);
        Assert.StartsWith(download.Id, download.AssetName, StringComparison.Ordinal);
        Assert.EndsWith(".zip", download.AssetName, StringComparison.Ordinal);
        Assert.True(download.ApproximateBytes > 0, "the offer text and the size band both read this");
    }

    /// <summary>
    ///     <b>The hash is not published yet, and that is the state this asserts
    ///     — not an aspiration.</b> Filling it in is a one-line change to
    ///     <c>RigEstimatorModelCatalog.Download</c>; when that happens this case
    ///     fails and is inverted to <c>Assert.True(HasPublishedHash)</c>. Until
    ///     then the installer logs a warning on every install and falls back to
    ///     the size check, which is honest about being a sanity check rather
    ///     than integrity.
    /// </summary>
    [Fact]
    public void TheHashIsPublished_AndIsTheShippedArchivesDigest()
    {
        // rig-estimator-v1.zip = rig-estimator-v2-r8-20260914 (round 8 best.pt,
        // epoch 9), 130,799,294 bytes, digested 2026-09-14. A changed asset
        // changes this line in the same commit, never the installer's check.
        Assert.True(RigEstimatorModelCatalog.Download.HasPublishedHash);
        Assert.Equal("900f9eed9ac2bdbeaee0abbc49556e098fe2ec4c7f92ac51e5756e656792caba",
            RigEstimatorModelCatalog.Download.Sha256);
        Assert.Equal(130_799_294L, RigEstimatorModelCatalog.Download.ApproximateBytes);
    }

    /// <summary>
    ///     A placeholder is REFUSED as a published hash. Without the shape
    ///     check, <c>"TBD"</c> would read as "integrity is enforced" while
    ///     failing every real archive — the same outage as a wrong hash, with no
    ///     way to tell them apart from a log line.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("TBD", false)]
    [InlineData("pending", false)]
    [InlineData("abc123", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void OnlyAFullHexDigestCountsAsPublished(string sha, bool expected)
    {
        var download = RigEstimatorModelCatalog.Download with { Sha256 = sha };

        Assert.Equal(expected, download.HasPublishedHash);
    }
}
