using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The tone library's catalogue entry: the second download in
///     <see cref="RigEstimatorModelCatalog" />, and the four facts about it that
///     can rot silently — the DI fingerprint it is named for, the file name the
///     matcher will look for, where it is installed, and whether its hash is
///     published.
/// </summary>
public sealed class RigLibraryCatalogTests
{
    /// <summary>
    ///     <b>The catalogue's fingerprint IS the app's.</b> The constant exists so
    ///     the catalogue can name the file without generating twenty seconds of
    ///     audio; this is what keeps it honest. A changed medley, a changed
    ///     synthesis or a changed fingerprint function fails here, and the asset
    ///     has to be re-rendered and re-published as <c>-v2</c>.
    /// </summary>
    [Fact]
    public void TheFingerprintIsTheOneTheAppComputesForTheBundledDi() => Assert.Equal(RigEstimatorModelCatalog.LibraryDiFingerprint, RigLibrary.Fingerprint(ReferenceDi.Medley()));

    /// <summary>
    ///     The installed file's name is the matcher's own cache name for the
    ///     shipping key, so "is the installed library the one this DI needs" is a
    ///     header check and not a convention two files have to agree on.
    /// </summary>
    [Fact]
    public void TheFileNameIsTheMatchersOwnNameForTheShippingKey()
    {
        var defaults = new RigLibraryOptions();

        Assert.Equal("library-tonefx-v2-4000-20260912-5e4b70d7.bin", RigEstimatorModelCatalog.LibraryFileName);
        Assert.Equal(
            Path.GetFileName(RigLibrary.DefaultCachePath(
                ToneFeatureExtractor.ExtractorId, defaults.Count, defaults.Seed,
                RigEstimatorModelCatalog.LibraryDiFingerprint)),
            RigEstimatorModelCatalog.LibraryFileName);
        Assert.Equal(new[] { RigEstimatorModelCatalog.LibraryFileName }, RigEstimatorModelCatalog.Library.Files);
    }

    [Fact]
    public void TheLibraryInstallsNextToTheModel()
    {
        Assert.Equal(Path.Combine(RigEstimatorModelCatalog.ModelRoot, "rig-library"),
            RigEstimatorModelCatalog.LibraryDirectory);
        Assert.Equal(Path.Combine(RigEstimatorModelCatalog.LibraryDirectory, RigEstimatorModelCatalog.LibraryFileName),
            RigEstimatorModelCatalog.LibraryPath);
        Assert.Equal(Path.GetDirectoryName(RigEstimatorModelCatalog.ModelDirectory),
            Path.GetDirectoryName(RigEstimatorModelCatalog.LibraryDirectory));
    }

    [Fact]
    public void TheAssetIsVersionedInItsName_AndTheEntryIsInTheCatalogue()
    {
        var library = RigEstimatorModelCatalog.Library;

        Assert.Equal(RigEstimatorModelCatalog.LibraryId, library.Id);
        Assert.Equal("rig-library-v1.zip", library.AssetName);
        Assert.StartsWith(library.Id, library.AssetName, StringComparison.Ordinal);
        Assert.True(library.ApproximateBytes > 0);
        Assert.Equal("tone library", library.Description);
        Assert.Equal(2, RigEstimatorModelCatalog.All.Count);
        Assert.Equal(2, RigEstimatorModelCatalog.All.Select(d => d.Id).Distinct().Count());
        Assert.Same(library, RigEstimatorModelCatalog.Find(RigEstimatorModelCatalog.LibraryId));
        Assert.Same(RigEstimatorModelCatalog.Download, RigEstimatorModelCatalog.Find(RigEstimatorModelCatalog.RigEstimatorId));
        Assert.Null(RigEstimatorModelCatalog.Find("nothing"));
    }

    /// <summary>The model's entry is unchanged by the library's arrival: still
    /// the two model files, still the model wording.</summary>
    [Fact]
    public void TheModelEntryStillNamesTheModelFiles()
    {
        Assert.Equal(new[] { "model.onnx", "model.sidecar.json" }, RigEstimatorModelCatalog.Download.Files);
        Assert.Equal("rig estimator model", RigEstimatorModelCatalog.Download.Description);
        Assert.Null(RigEstimatorModelCatalog.Download.RequiredFiles);
    }

    /// <summary>
    ///     <b>The hash is published and it is the shipped archive's digest.</b>
    ///     rig-library-v1.zip = library-tonefx-v2-4000-20260912-5e4b70d7.bin,
    ///     rendered 2026-09-14 by <c>tools/rig-dataset build-library</c> and
    ///     digested after zipping. A re-cut asset changes these lines in the same
    ///     commit, never the installer's check.
    /// </summary>
    [Fact]
    public void TheLibraryHashIsPublished_AndIsTheShippedArchivesDigest()
    {
        Assert.True(RigEstimatorModelCatalog.Library.HasPublishedHash);
        Assert.Equal("f5663a676598c73ab14110318584230a04892aad34b16b8932650528fbdc8910", RigEstimatorModelCatalog.Library.Sha256);
        Assert.Equal(6_026_235L, RigEstimatorModelCatalog.Library.ApproximateBytes);
    }
}
