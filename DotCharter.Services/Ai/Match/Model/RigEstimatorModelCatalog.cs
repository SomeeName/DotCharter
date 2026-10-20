namespace DotCharter.Services.Ai.Match.Model;

/// <summary>A downloadable rig-estimator model, as a published release asset.</summary>
/// <param name="Id">Folder name under <see cref="RigEstimatorModelCatalog.ModelRoot" />.</param>
/// <param name="AssetName">Release asset to fetch.</param>
/// <param name="Sha256">
///     Expected hash of the archive, lower-case hex.
///     <para>
///         <b>Empty means "not published yet", and that is a real state rather
///         than a sloppy one.</b> The asset name is versioned
///         (<c>rig-estimator-v1.zip</c>) and the hash can only be computed once
///         that exact archive exists — so the shipping order is: build the
///         installer, train the model, upload the asset, then fill this in. An
///         empty hash therefore degrades to the SIZE check
///         (<see cref="RigEstimatorModelInstaller" />) and logs a WARNING on
///         every install, so "we never got round to it" cannot pass silently.
///         A filled hash is enforced and a mismatch discards the download.
///     </para>
/// </param>
/// <param name="ApproximateBytes">
///     For the progress message and the plausibility band. Zero means unknown,
///     which widens the band to "not obviously an error page".
/// </param>
/// <param name="RequiredFiles">
///     The files that have to be present for this download to count as
///     installed. Null (the default) means the MODEL's two files
///     (<see cref="RigEstimatorModelInstaller.ModelFiles" />); the tone
///     library names its one <c>.bin</c>. ADDITIVE so every
///     existing four-argument construction keeps compiling.
/// </param>
/// <param name="Description">What it is, for a progress line ("Downloading the
/// tone library (9 MB)…"). Defaults to the model's wording.</param>
public sealed record RigEstimatorModelDownload(
    string Id, string AssetName, string Sha256, long ApproximateBytes,
    IReadOnlyList<string>? RequiredFiles = null,
    string Description = "rig estimator model")
{
    /// <summary>The files an install needs — the entry's own list, else the model's.</summary>
    public IReadOnlyList<string> Files => RequiredFiles ?? RigEstimatorModelInstaller.ModelFiles;

    /// <summary>True when <see cref="Sha256" /> is a usable 64-char hex digest.</summary>
    /// <remarks>
    ///     Shape-checked rather than merely non-empty: a placeholder like
    ///     <c>"TBD"</c> would otherwise read as "integrity is enforced" while
    ///     failing every real archive, which is the same outage as a wrong hash
    ///     but with no way to tell them apart in a log.
    /// </remarks>
    public bool HasPublishedHash =>
        Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}

/// <summary>
///     The one model the "match a tone from audio" estimator lane can download,
///     and where it goes.
///     <para>
///         <b>Why a download and not a bundled file.</b> The export is ~200 MB
///         of weights that change far more rarely than the app; bundling it
///         would make every update re-download bytes the machine already has.
///         So it is its own release asset, fetched on demand into
///         <see cref="ModelRoot" /> and then left alone — the same arrangement
///         the lyric acoustic models use
///         (<c>DotCharter.Services/Audio/Lyrics/LyricModelCatalog.cs</c>).
///     </para>
///     <para>
///         <b>The root is <c>LocalApplicationData</c>, NOT <c>ApplicationData</c>.</b>
///         The lyric roots are <c>%APPDATA%</c>; this lane's path was fixed by
///         <see cref="RigEstimatorOnnx.DefaultModelDirectory" /> before any
///         downloader existed, and models are machine-local caches that have no
///         business roaming. <see cref="ModelDirectory" /> is asserted equal to
///         the estimator's own constant by
///         <c>RigEstimatorModelCatalogTests</c>, so a re-point of either side
///         cannot silently install into a folder nothing reads.
///     </para>
/// </summary>
public static class RigEstimatorModelCatalog
{
    /// <summary>The model id, which is also its folder name and its asset stem.</summary>
    public const string RigEstimatorId = "rig-estimator";

    /// <summary>
    ///     Where models for this lane are installed. ONE root, deliberately:
    ///     the lyric catalogue searches next-to-the-exe first so a portable copy
    ///     can carry its own weights, but the estimator reads exactly one
    ///     hard-coded directory, so a second root here would be a folder the
    ///     installer writes and the consumer never looks in.
    /// </summary>
    public static string ModelRoot => Path.Combine(
        UserDataFolders.LocalAppData,
        "DotCharter", "ai-match");

    /// <summary>
    ///     The directory the estimator loads from. Equal by construction to
    ///     <see cref="RigEstimatorOnnx.DefaultModelDirectory" />.
    /// </summary>
    public static string ModelDirectory => Path.Combine(ModelRoot, RigEstimatorId);

    /// <summary>
    ///     The download.
    ///     <para>
    ///         <b>Versioned in the asset NAME, not by which release carries it</b>
    ///         — the fetcher scans releases for the name rather than pinning a
    ///         tag, so re-uploading unchanged weights on every app release stays
    ///         optional. Bump to <c>-v2.zip</c> when the label space or the
    ///         export changes; do not overwrite a published <c>-v1</c>.
    ///     </para>
    ///     <para>
    ///         <b>The hash is not filled in yet</b> — the v1 export does not
    ///         exist at the time of writing. Filling it is a ONE-LINE change
    ///         here and nothing else moves: the installer already enforces a
    ///         published hash and already warns while there is none.
    ///         <see cref="RigEstimatorModelDownload.ApproximateBytes" /> is a
    ///         planning figure for the same reason and only ever affects the
    ///         progress text and the plausibility band.
    ///     </para>
    /// </summary>
    public static RigEstimatorModelDownload Download { get; } = new(
        RigEstimatorId,
        "rig-estimator-v1.zip",
        Sha256: "900f9eed9ac2bdbeaee0abbc49556e098fe2ec4c7f92ac51e5756e656792caba",
        ApproximateBytes: 130_799_294L);

    // ---- the tone library ------------------------------------------------------

    /// <summary>The library's id, which is also its folder name and its asset stem.</summary>
    public const string LibraryId = "rig-library";

    /// <summary>
    ///     <c>RigLibrary.Fingerprint(ReferenceDi.Medley())</c>, as the app computes
    ///     it — a CONSTANT here so the catalogue can name the file without
    ///     generating twenty seconds of audio to ask, and pinned to the live
    ///     computation by <c>RigLibraryCatalogTests</c>. If the medley or the
    ///     fingerprint changes, that test fails, this moves, and the asset is
    ///     re-rendered and re-published as <c>-v2</c>.
    /// </summary>
    public const string LibraryDiFingerprint = "5e4b70d7";

    /// <summary>Where the shipped library is installed: <c>ai-match\rig-library\</c>.</summary>
    public static string LibraryDirectory => Path.Combine(ModelRoot, LibraryId);

    /// <summary>
    ///     The library file's name for the shipping key — the same name the
    ///     matcher's own cache would use for that key, which is what makes "is the
    ///     installed library the one this DI needs" a header check and not a
    ///     convention.
    /// </summary>
    public static string LibraryFileName { get; } = Search.RigLibrary.CacheFileName(
        Similarity.ToneFeatureExtractor.ExtractorId,
        new Search.RigLibraryOptions().Count,
        new Search.RigLibraryOptions().Seed,
        LibraryDiFingerprint);

    /// <summary>The installed library's full path (present or not).</summary>
    public static string LibraryPath => Path.Combine(LibraryDirectory, LibraryFileName);

    /// <summary>
    ///     The pre-rendered retrieval library for the BUNDLED reference DI.
    ///     <para>
    ///         <b>Why it is shippable at all.</b> The bundled DI is generated, the
    ///         sampler is seeded and the extractor is deterministic, so the
    ///         4,000-row library for that DI is the same file on every machine —
    ///         and rendering it is ~2.8 hours of CPU (owner: watched
    ///         "1/4000" and gave up). Rendered once with
    ///         <c>tools/rig-dataset build-library</c>, published once, installed
    ///         next to the estimator; the matcher loads it whenever its header's
    ///         DI fingerprint matches the DI in hand, and otherwise grows a local
    ///         library small-first (see <c>RigLibraryProvider</c>).
    ///     </para>
    ///     <para>
    ///         Optional in the same sense the model is: without it the match
    ///         still runs, on a library that starts at 96 rows.
    ///     </para>
    /// </summary>
    public static RigEstimatorModelDownload Library { get; } = new(
        LibraryId,
        "rig-library-v1.zip",
        // rig-library-v1.zip = library-tonefx-v2-4000-20260912-5e4b70d7.bin rendered
        // by `tools/rig-dataset build-library`, digested after zipping.
        Sha256: "f5663a676598c73ab14110318584230a04892aad34b16b8932650528fbdc8910",
        ApproximateBytes: 6_026_235L,
        RequiredFiles: [LibraryFileName],
        Description: "tone library");

    /// <summary>Every entry, by id.</summary>
    public static IReadOnlyList<RigEstimatorModelDownload> All { get; } = [Download, Library];

    /// <summary>The entry with this id, or null.</summary>
    /// <param name="id">A download id.</param>
    public static RigEstimatorModelDownload? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
}
