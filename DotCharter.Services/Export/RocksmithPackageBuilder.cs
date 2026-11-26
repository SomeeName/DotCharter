using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Images;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Infrastructure.Psarc;
using DotCharter.Services.Infrastructure.Psarc.Write;
using DotCharter.Services.Parsing.Xml.Showlights;
using SkiaSharp;

namespace DotCharter.Services.Export;

/// <summary>Options for building a Rocksmith 2014 package: PC, Mac or both.</summary>
public sealed record RocksmithBuildOptions
{
    /// <summary>Full path of the .psarc to create (conventionally ending in "_p.psarc", or
    /// "_m.psarc" for a Mac-only build). For <see cref="ExportPlatform.PcAndMac" /> it names the
    /// pair: both packages take its folder and base name with their own suffix.</summary>
    public required string OutputPath { get; init; }

    /// <summary>The project folder that owns generated audio caches, independently of the package destination.
    /// When omitted, raw builder callers derive the folder from the original main audio.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// The platform(s) to write. A stock Mac package is always encrypted and needs the Mac SNG key:
    ///     a Mac-only build that cannot have it fails, PC + Mac writes the PC package alone
    ///     and says why (<see cref="RocksmithPlatformTargets" />). An RS Extended Range package
    ///     is plain on both platforms (<c>_rsxr_p.psarc</c>, <c>_rsxr_m.psarc</c>).
    /// </summary>
    public ExportPlatform Platform { get; init; } = ExportPlatform.Pc;

    /// <summary>The DLC key (alphanumeric). Derived from artist + title when empty.</summary>
    public string DlcKey { get; init; } = "";

    /// <summary>Steam App ID; default is Cherub Rock (248750), required for CDLC.</summary>
    public string AppId { get; init; } = "248750";

    /// <summary>Main audio. A <c>.wem</c> is used as-is; anything decodable
    /// (wav/ogg/mp3/flac/…) is encoded to one — see <see cref="AudioCodec"/>.</summary>
    public required string AudioPath { get; init; }

    /// <summary>Preview audio. When empty, a 28 s faded clip is cut from the
    /// main audio automatically (see <see cref="PreviewStartMs"/>).</summary>
    public string PreviewAudioPath { get; init; } = "";

    /// <summary>Where the auto-cut preview starts in the main audio. Null uses the
    /// project's <c>ChartData.PreviewStartMs</c>, else 25% into the song; always
    /// clamped so a whole clip fits. The clip is re-cut on every build.</summary>
    public double? PreviewStartMs { get; init; }

    /// <summary>
    ///     Codec used when the source audio must be encoded to a <c>.wem</c>.
    ///     Ignored when the source already is one.
    ///     <para>
    ///         <see cref="WemCodec.Vorbis"/> (the default) is the codec Rocksmith
    ///         itself ships and by far the smallest. JuceSharp's own Wwise-Vorbis
    /// encoder writes it (ABI v44). <see cref="WemCodec.Pcm"/>
    ///         is ~11x the size. <see cref="WemCodec.ImaAdpcm"/> is never written:
    /// the game does not play it, the song never starts, so a request
    ///         for it is treated as Vorbis.
    ///     </para>
    /// </summary>
    public WemCodec AudioCodec { get; init; } = WemCodec.Vorbis;

    /// <summary>
    /// Write a PLAIN package: the PSARC table of contents unencrypted
    ///     (archive flags without 0x4) and every SNG as flags 1, compressed but not
    ///     encrypted. It loads only with the CDLC enabler's plain-PSARC patch
    ///     (RSCDLCEnabler-TooManyCoresFix PR #1). Null and false both write the stock
    /// ENCRYPTED form for every arrangement, 7-/8-string guitars included:
    ///     plain is an explicit choice (the export dialog's Package option).
    ///     Without the user's key file (<see cref="RocksmithKeys" />)
    ///     the package is plain whatever this says, with a warning. A Mac package
    ///     is never plain.
    /// </summary>
    public bool? PlainPackage { get; init; }

    /// <summary>
    ///     With both RS Extended Range and stock arrangements, also write a standard package
    ///     holding only the stock ones, beside the RS Extended Range package. Both share the
    ///     song's identity: they are two versions of one song.
    /// </summary>
    public bool WriteStandardPackage { get; init; }

    /// <summary>The standard package's path; defaults to <see cref="OutputPath" /> without the
    /// RS Extended Range tag. The save companion builds it to a staging name.</summary>
    public string? StandardOutputPath { get; init; }

    /// <summary>The <see cref="PlainPackage" /> of the standard package: the stock choice the
    /// RS Extended Range package overrides.</summary>
    public bool? StandardPlainPackage { get; init; }

    /// <summary>The persistent and master ids and the song partition drawn per arrangement, shared
    /// by the two packages of one export. Filled by the first build and read by the second.</summary>
    internal Dictionary<string, RsSharedArrangement>? SharedArrangementIds { get; init; }

    /// <summary>This build is the standard package of a mixed export: a path whose Main was
    /// extended and left out ships its first stock arrangement as Main.</summary>
    internal bool StandardCompanion { get; init; }


    /// <summary>
    /// The package path whose stale <c>chords-ext.json</c> sidecars (the 7-string
    /// chord format before the RS7X block) are removed after the build.
    ///     Defaults to <see cref="OutputPath" />; the save companion builds to a staging
    ///     name and passes the final one here.
    /// </summary>
    public string? ChordSidecarPackagePath { get; init; }

    /// <summary>Whether to remove legacy chord sidecars after publication. Test exports preserve them.</summary>
    public bool CleanUpChordSidecars { get; init; } = true;

    /// <summary>
    /// DLC Builder's build steps (its fixes, then dynamic difficulty for a single-level
    ///     arrangement), run on a copy of each arrangement (<see cref="DlcBuilderBuildSteps" />).
    ///     The app passes <see cref="DlcBuilderSteps.FromConfig" />; null here, so a caller that
    ///     does not ask builds the chart as it is.
    /// </summary>
    public DlcBuilderSteps? DlcBuilderSteps { get; init; }

    /// <summary>
    ///     Write each stock bass whose lowest string is below C1 an octave up at A220
    ///     (<see cref="LowBassTuningFix" />), on the package's copy only. Off unless asked: the app
    ///     passes the export dialog's checkbox or <c>AppConfig.ExportFixLowBassTuning</c>.
    /// </summary>
    public bool FixLowBassTuning { get; init; }

    /// <summary>The double-stop naming policy for both the build steps and SNG.</summary>
    public DoubleStopNameOptions ChordNameOptions { get; init; } = DoubleStopNameOptions.Default;

    /// <summary>
    /// Test seam: pins every id and time stamp the build would otherwise
    ///     draw at random, so a rebuild of a reference package can be compared byte
    ///     for byte. Null (always, outside tests) draws fresh ones.
    /// </summary>
    internal RocksmithBuildIdentity? Identity { get; init; }

    /// <summary>Album art image (png/jpg). A placeholder is generated when empty.</summary>
    public string AlbumArtPath { get; init; } = "";

    /// <summary>The song's package version, written as <c>Package Version</c> in the package's
    /// <c>toolkit.version</c> (DLC Builder's <c>project.Version</c>). A test build says "test".</summary>
    public string Version { get; init; } = "1";

    /// <summary>Main audio volume in dB (DLC Builder convention).</summary>
    public double AudioVolume { get; init; } = -8.0;

    /// <summary>Preview audio volume in dB.</summary>
    public double PreviewVolume { get; init; } = -7.0;

    /// <summary>Include the project's first vocal track as a Vocals arrangement.</summary>
    public bool IncludeVocals { get; init; } = true;

    /// <summary>Song length in ms; derived from the chart when 0.</summary>
    public double SongLengthMs { get; init; }

    /// <summary>Per-arrangement tone slot overrides keyed by arrangement name;
    /// value = (base tone, up to four slot tones A-D). When absent the
    /// arrangement's own ToneBase/ToneA-D and tone changes are used.</summary>
    public Dictionary<string, (string ToneBase, string[] Slots)> ToneMap { get; init; } = [];

    /// <summary>Full tone definitions (raw manifest "Tones" entry JSON bodies)
    /// keyed by tone key. A definition whose key matches an arrangement's base
    /// tone or one of its slot tones is embedded in that arrangement's manifest
    /// so the game loads the real tone instead of falling back to a default.
    /// Typically sourced from an existing package via
    /// the package reader's tone definitions.</summary>
    public IReadOnlyDictionary<string, string> ToneDefinitions { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Names of the instrumental arrangements to include. Null exports
    /// all instrumentals; vocals are governed by <see cref="IncludeVocals"/>.</summary>
    public IReadOnlyCollection<string>? ArrangementFilter { get; init; }

    /// <summary>Package author used when the chart itself has no Charter set
    /// (typically the app-level "default charter name" setting).</summary>
    public string FallbackCharter { get; init; } = "";
}

/// <summary>
///     The named stages of a Rocksmith package build, in the order they run.
///     <para>
///         An ENUM and not a string, because the string was English:
///         <c>Report("Resolving audio")</c> went straight into
///         <c>Dialog_Export_ProgressStep</c>'s <c>{2}</c>, so the export dialog
///         read "Schritt 1 von 6: Resolving audio" for a German user and the
///         same English fragment for a Japanese one. A service has no business
///         naming a stage in a language; the dialog resolves
///         <see cref="RocksmithBuildProgress.ResourceKey" /> instead.
///     </para>
/// </summary>
public enum RocksmithBuildStage
{
    ResolvingAudio,
    ConvertingArrangements,
    CreatingManifests,
    EncodingAlbumArt,
    GeneratingSoundBanks,
    WritingArchive
}

public sealed record RocksmithBuildProgress(int Step, int TotalSteps, RocksmithBuildStage Stage)
{
    public double Fraction => TotalSteps == 0 ? 0 : (double)Step / TotalSteps;

    /// <summary>
    ///     The resx key naming <see cref="Stage" />. A COMPUTED key family —
    ///     the six names are spelled nowhere in the repo, so both localization
    ///     lints are blind to them and an "unused key" sweep calls all six
    ///     dead. It is registered in
    ///     <c>LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage</c>,
    ///     which enumerates the enum and asserts every member exists and is
    ///     non-empty in all three tables.
    /// </summary>
    public string ResourceKey => $"Dialog_Export_Stage_{Stage}";
}

public sealed record RocksmithBuildResult(
    string PsarcPath,
    string DlcKey,
    IReadOnlyList<string> ArrangementNames,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Every package written, PC first; <see cref="PsarcPath" /> is the first.</summary>
    public IReadOnlyList<string> PackagePaths { get; init; } = [PsarcPath];

    /// <summary>The archive carries the PSXR magic and extended arrangements.</summary>
    public bool IsPsxr { get; init; }

    /// <summary>The standard packages written beside the RS Extended Range package, PC first
    /// (<see cref="RocksmithBuildOptions.WriteStandardPackage" />); also in <see cref="PackagePaths" />.</summary>
    public IReadOnlyList<string> StandardPackagePaths { get; init; } = [];

    /// <summary>The chart names of the stock arrangements this build wrote.</summary>
    internal IReadOnlyList<string> StockArrangements { get; init; } = [];
}

/// <summary>
/// Builds a playable Rocksmith 2014 .psarc — PC, Mac or both — from a <see cref="ChartProject"/>.
/// Flat-file layout, entry set and asset naming ported from
/// Rocksmith2014.NET PackageBuilder.fs.
/// </summary>
public sealed partial class RocksmithPackageBuilder
{
    private readonly IAppLogger _logger;

    public RocksmithPackageBuilder(IAppLogger logger) => _logger = logger;

    /// <summary>Whether this is a seven-/eight-string guitar or five-/six-string bass,
    /// written as an RS Extended Range package on Windows.</summary>
    public static bool IsExtendedStringArrangement(Arrangement arrangement) => Rs7StringLayout.IsExtended(arrangement);

    public Task<RocksmithBuildResult> BuildPsarcAsync(
        ChartProject project,
        RocksmithBuildOptions options,
        IProgress<RocksmithBuildProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task>? beforePublish = null)
    {
        // Play-only content is never exported; refused before any work starts.
        PlayOnlyExportGuard.ThrowIfPlayOnly(project, "Rocksmith package");
        return Task.Run(() => BuildPackagesAsync(project, options, progress, cancellationToken, beforePublish), cancellationToken);
    }

    private async Task<RocksmithBuildResult> BuildAsync(
        ChartProject project,
        RocksmithBuildOptions options,
        IProgress<RocksmithBuildProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? beforePublish)
    {
        var warnings = new List<string>();
        var step = 0;
        const int totalSteps = 6;

        void Report(RocksmithBuildStage stage) => progress?.Report(new RocksmithBuildProgress(++step, totalSteps, stage));

        if (project.Beats.Count == 0)
            throw new InvalidDataException("The project has no beat map — cannot export.");
        if (project.Arrangements.Count == 0)
            throw new InvalidDataException("The project has no arrangements — cannot export.");

        var meta = RsPackageMetadataFactory.Create(project, options);
        var dlcKey = meta.DlcKey;
        var key = dlcKey.ToLowerInvariant();

        Report(RocksmithBuildStage.ResolvingAudio);
        cancellationToken.ThrowIfCancellationRequested();

        var exportAudio = ProjectExportAudio.ForSnapshot(project, options.AudioPath);
        var mainWem = ResolveWem(exportAudio, options, warnings);
        var previewWem = ResolvePreviewWem(mainWem, options, exportAudio, project.Chart.PreviewStartMs, warnings);

        Report(RocksmithBuildStage.ConvertingArrangements);
        cancellationToken.ThrowIfCancellationRequested();

        var songLengthMs = options.SongLengthMs;
        var arrangements = ResolvePathMains(ConvertArrangements(project, options, songLengthMs, warnings), options.StandardCompanion);
        if (arrangements.Count == 0)
        {
            // Say WHY (an 8-string guitar is refused).
            var skipped = warnings.Where(w => w.StartsWith("Arrangement '", StringComparison.Ordinal));
            throw new InvalidDataException(string.Join(" ", ["No arrangement could be converted for export.", .. skipped]));
        }

        Report(RocksmithBuildStage.CreatingManifests);
        cancellationToken.ThrowIfCancellationRequested();

        // PC, Mac or both; a Mac package the content or the keys rule out is
        // refused when it was the only target and dropped with a warning otherwise.
        var platforms = RocksmithPlatformTargets.Resolve(options, arrangements, warnings);
        var psxr = arrangements.Any(a => a.Sng.Extension != null);
        var plain = (psxr || platforms.Contains(RsPlatformLayout.Pc)) && ResolvePcPlain(options, arrangements, warnings);

        // Everything but the SNGs, the graph and the entry folders is the same bytes on
        // both platforms, so it is built once.
        var showlights = CreateShowlights(project, songLengthMs, warnings);
        var header = Encoding.UTF8.GetBytes(RsManifestGenerator.CreateHeader(meta, arrangements));
        var manifests = arrangements
            .Select(a => Encoding.UTF8.GetBytes(RsManifestGenerator.CreateArrangementManifest(meta, a)))
            .ToList();

        Report(RocksmithBuildStage.EncodingAlbumArt);
        cancellationToken.ThrowIfCancellationRequested();

        var albumArt = CreateAlbumArt(options, warnings).ToList();
        var xblock = XBlockGenerator.Create(meta, arrangements);

        Report(RocksmithBuildStage.GeneratingSoundBanks);
        cancellationToken.ThrowIfCancellationRequested();

        BuiltBank[] banks =
        [
            CreateBank(dlcKey, mainWem, (float)options.AudioVolume, isPreview: false, options.Identity?.MainBank),
            CreateBank($"{dlcKey}_Preview", previewWem, (float)options.PreviewVolume, isPreview: true,
                options.Identity?.PreviewBank)
        ];

        Report(RocksmithBuildStage.WritingArchive);
        cancellationToken.ThrowIfCancellationRequested();

        var shared = new SharedEntries(key, showlights, header, manifests, albumArt, xblock, banks,
            Encoding.UTF8.GetBytes(ToolkitVersionText(meta.Charter, options)),
            Encoding.ASCII.GetBytes(options.AppId));
        var paths = new List<string>(platforms.Count);
        using var publication = new PackagePublication(logger: _logger);
        foreach (var platform in platforms)
        {
            var path = RocksmithPlatformTargets.OutputPathFor(options, platform);
            path = platform.IsMac ? ExtendedRangePackageName.ApplyMac(path, psxr) : ExtendedRangePackageName.Apply(path, psxr);
            cancellationToken.ThrowIfCancellationRequested();
            WritePackage(publication.Stage(path), platform, plain && (psxr || !platform.IsMac), meta, arrangements, shared, options, psxr);
            paths.Add(path);
        }
        if (beforePublish != null)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await beforePublish(path, cancellationToken).ConfigureAwait(false);
            }
        }
        publication.Commit(cancellationToken);

        if (options.CleanUpChordSidecars)
            CleanUpChordSidecars(options.ChordSidecarPackagePath ?? options.OutputPath, arrangements, psxr);

        _logger.Info(AppLogCategory.Project,
            $"Rocksmith package built. Path='{string.Join("', '", paths)}', key='{dlcKey}', " +
            $"arrangements=[{string.Join(", ", arrangements.Select(a => a.PartitionName))}], " +
            $"warnings={warnings.Count}.");

        return new RocksmithBuildResult(
            paths[0],
            dlcKey,
            arrangements.Select(a => a.ArrangementName).ToList(),
            warnings)
        {
            PackagePaths = paths, IsPsxr = psxr,
            StockArrangements = [.. arrangements.Where(a => a.Domain != null && a.Sng.Extension == null).Select(a => a.ChartName)]
        };
    }

    private static void CleanUpChordSidecars(string path, IReadOnlyList<RsPackageArrangement> arrangements, bool extended)
    {
        var partitions = arrangements.Select(a => a.PartitionName);
        Rs7ChordSidecar.RemoveStale(path, partitions);
        Rs7ChordSidecar.RemoveStale(ExtendedRangePackageName.Apply(path, extended), partitions);
    }

    /// <summary>Whether the PC package is written PLAIN: only when asked for, and
    /// plain whatever the option says without the keys.</summary>
    private static bool ResolvePcPlain(RocksmithBuildOptions options, IReadOnlyList<RsPackageArrangement> arrangements,
        List<string> warnings)
    {
        var plain = options.PlainPackage ?? false;
        if (arrangements.Any(a => a.Sng.Extension != null))
        {
            warnings.Add("Written as a PLAIN package (unencrypted) for the RS Extended Range format.");
            return true;
        }
        // No user key file: an encrypted package cannot be written, so the
        // package is plain — the export dialog says so before the export starts.
        if (!plain && !RocksmithKeys.CanWriteEncrypted)
        {
            warnings.Add("Written as a PLAIN package (unencrypted): it loads only with the RS7String " +
                         "7-string mod.");
            return true;
        }

        if (plain)
        {
            warnings.Add("Written as a PLAIN package (unencrypted, as asked): it loads only with " +
                         "the RS7String 7-string mod.");
        }

        return plain;
    }

    /// <summary>The entries both platforms share, built once per export.</summary>
    private sealed record SharedEntries(
        string LowerKey,
        byte[] Showlights,
        byte[] Header,
        IReadOnlyList<byte[]> Manifests,
        IReadOnlyList<(int Size, byte[] Data)> AlbumArt,
        byte[] XBlock,
        IReadOnlyList<BuiltBank> Banks,
        byte[] ToolkitVersion,
        byte[] AppId);

    /// <summary>A generated Wwise bank and the wem it plays: the same bytes on both platforms
    /// (Rocksmith2014.NET SoundBank.fs has no platform branch; official PC/Mac pairs agree).</summary>
    private sealed record BuiltBank(string BankName, byte[] Bank, string WemName, string WemPath);

    /// <summary>
    ///     Writes one platform's package. The entry ORDER is the PC package's
    ///     (pinned byte for byte by <c>RocksmithPlatformPackageTests</c>).
    ///     <para>
    ///         The guard covers the BUILDING of the entry list, not just the write. Two of
    ///         these entries are open FileStreams over the source .wem files, and a failure
    ///         between them and the write left both wem handles open until the finalizer ran,
    ///         so the very next export could not overwrite its own cached wem.
    ///     </para>
    /// </summary>
    private static void WritePackage(string path, RsPlatformLayout platform, bool plain, RsPackageMetadata meta,
        IReadOnlyList<RsPackageArrangement> arrangements, SharedEntries shared, RocksmithBuildOptions options, bool psxr)
    {
        var key = shared.LowerKey;
        var entries = new List<PsarcWriteEntry>();
        try
        {
            foreach (var arrangement in arrangements)
            {
                var sngData = new MemoryStream();
                // The RS Extended Range package carries the same plain charts on both platforms.
                if (platform.IsMac && !psxr)
                    RocksmithSngWriter.WritePackedMac(arrangement.Sng, sngData, options.Identity?.SngIv);
                else if (arrangement.Sng.Extension != null)
                    RsxrSngWriter.WritePlain(arrangement.Sng, sngData);
                else if (plain)
                    RocksmithSngWriter.WritePlainPc(arrangement.Sng, sngData);
                else
                    RocksmithSngWriter.WritePackedPc(arrangement.Sng, sngData, options.Identity?.SngIv);
                sngData.Position = 0;
                entries.Add(new PsarcWriteEntry(platform.SngEntry(key, arrangement.PartitionName), sngData));
            }

            entries.Add(new PsarcWriteEntry($"songs/arr/{key}_showlights.xml", shared.Showlights));
            entries.Add(new PsarcWriteEntry($"manifests/songs_dlc_{key}/songs_dlc_{key}.hsan", shared.Header));
            for (var i = 0; i < arrangements.Count; i++)
            {
                entries.Add(new PsarcWriteEntry(
                    $"manifests/songs_dlc_{key}/{key}_{arrangements[i].PartitionName}.json", shared.Manifests[i]));
            }

            foreach (var (size, dds) in shared.AlbumArt)
                entries.Add(new PsarcWriteEntry($"gfxassets/album_art/album_{key}_{size}.dds", dds));

            // XBlock, flat models, aggregate graph.
            entries.Add(new PsarcWriteEntry($"gamexblocks/nsongs/{key}.xblock", shared.XBlock));
            entries.Add(new PsarcWriteEntry("flatmodels/rs/rsenumerable_song.flat",
                RsEnumerableModels.Song()));
            entries.Add(new PsarcWriteEntry("flatmodels/rs/rsenumerable_root.flat",
                RsEnumerableModels.Root()));
            entries.Add(new PsarcWriteEntry($"{key}_aggregategraph.nt",
                AggregateGraphGenerator.Create(meta, arrangements, options.Identity?.GraphItems, platform)));

            foreach (var bank in shared.Banks)
            {
                entries.Add(new PsarcWriteEntry(platform.AudioEntry($"song_{bank.BankName.ToLowerInvariant()}.bnk"),
                    bank.Bank));
                entries.Add(new PsarcWriteEntry(platform.AudioEntry($"{bank.WemName}.wem"),
                    new FileStream(bank.WemPath, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }

            // Toolkit version + app id.
            entries.Add(new PsarcWriteEntry("toolkit.version", shared.ToolkitVersion));
            entries.Add(new PsarcWriteEntry("appid.appid", shared.AppId));

            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            EnsureEntryNamesFromKey(entries.Select(e => e.Name), key);
            PsarcWriter.WriteFile(path, entries, encryptToc: !plain,
                extendedRange: psxr);
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    // ---- metadata / arrangements --------------------------------------------

    /// <summary>
    ///     The last line of defence for ASCII-only entry names: every entry name in the archive is ASCII, and every
    ///     one that is not a fixed resource or a numbered <c>.wem</c> carries the key. A Unicode
    ///     name that slipped past the key rules builds a package the game silently does not list.
    /// </summary>
    internal static void EnsureEntryNamesFromKey(IEnumerable<string> entryNames, string lowerKey)
    {
        foreach (var name in entryNames)
        {
            if (RocksmithDlcKey.HasNonAscii(name))
                throw new InvalidOperationException($"Package entry '{name}' is not ASCII; the game would not list the package.");
            if (FixedEntryNames.Contains(name) || IsNumberedWem(name)) continue;
            if (!name.Contains(lowerKey, StringComparison.Ordinal))
                throw new InvalidOperationException($"Package entry '{name}' is not derived from the DLC key '{lowerKey}'.");
        }

        static bool IsNumberedWem(string name) =>
            IsNumberedWemIn(name, RsPlatformLayout.Pc.AudioEntry("")) ||
            IsNumberedWemIn(name, RsPlatformLayout.Mac.AudioEntry(""));

        static bool IsNumberedWemIn(string name, string folder) =>
            name.StartsWith(folder, StringComparison.Ordinal) &&
            name.EndsWith(".wem", StringComparison.Ordinal) &&
            name[folder.Length..^".wem".Length].All(char.IsAsciiDigit);
    }

    private static readonly HashSet<string> FixedEntryNames = new(StringComparer.Ordinal)
    {
        "flatmodels/rs/rsenumerable_song.flat", "flatmodels/rs/rsenumerable_root.flat", "toolkit.version", "appid.appid"
    };

    private List<RsPackageArrangement> ConvertArrangements(
        ChartProject project,
        RocksmithBuildOptions options,
        double songLengthMs,
        List<string> warnings)
    {
        var result = new List<RsPackageArrangement>();

        var instrumentals = project.Arrangements
            .Where(a => a.ArrangementType != ArrangementType.Vocal)
            .Where(a => options.ArrangementFilter == null || options.ArrangementFilter.Contains(a.Name))
            .ToList();

        var toneDefinitions = MergeToneDefinitions(project, options);
        var toneRepair = new Lazy<ToneKeyRepairProposal?>(() => ToneKeyRepair.Propose(project));
        var partitionCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var chartArrangement in instrumentals)
        {
            var arrangement = chartArrangement;
            var typeName = arrangement.ArrangementType switch
            {
                ArrangementType.Rhythm => "Rhythm",
                ArrangementType.Bass => "Bass",
                ArrangementType.Combo => "Combo",
                _ => "Lead"
            };

            var partition = partitionCounters.TryGetValue(typeName, out var count) ? count + 1 : 1;
            partitionCounters[typeName] = partition;
            partition = SharedPartition(options, chartArrangement.Name, partition);
            var partitionName = partition == 1
                ? typeName.ToLowerInvariant()
                : $"{typeName.ToLowerInvariant()}{partition}";

            try
            {
                if (RsxrStringLayout.ExportRefusal(arrangement) is { } refusal)
                    throw new InvalidDataException(refusal);
                // Charts with no authored phrase/section structure would otherwise
                // export the SNG mapper's degenerate one-riff stub; generate a
                // proper COUNT/p0…pN/END structure first (in place — this
                // arrangement belongs to an already-cloned snapshot).
                PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, songLengthMs);
                if (options.DlcBuilderSteps is { } steps)
                {
                    arrangement = DlcBuilderBuildSteps.ApplyOrKeep(project, arrangement, songLengthMs > 0 ? songLengthMs : null,
                        steps with { ChordNameOptions = options.ChordNameOptions }, _logger, warnings);
                }

                arrangement = LowBassTuningFix.ForPackage(arrangement, options.FixLowBassTuning);

                // Resolve the slot table BEFORE the SNG: a tone-change event in
                // the SNG is an INDEX into the very list the manifest declares as
                // Tone_A..Tone_D, so the mapper has to be handed the same list.
                // Letting it resolve its own from the arrangement made every tone
                // change in an override build (the tone designer's, and the one
                // this builder's ToneMap option exists for) select whatever tone
                // sat at that index in the other list.
                var (toneBase, slots) = options.ToneMap.TryGetValue(arrangement.Name, out var overrideTones)
                    ? overrideTones
                    : RsToneSlots.Resolve(arrangement);

                var build = SngFromDomainMapper.BuildInstrumental(
                    arrangement, project.Beats, songLengthMs, options.Identity?.ConversionTime,
                    chordNameOptions: options.ChordNameOptions, toneSlots: slots);
                // The game needs a non-empty Tone_Base to resolve the arrangement's
                // tone at song load, even when no tone definitions are shipped (it
                // then falls back to its default tone BY NAME). DLC Builder always
                // emits one ("lead_base"); an empty string is a load crash.
                // Nothing declared a tone (New project and every importer without
                // tone names leave all of them empty), so the invented name is not a
                // declaration: a body the project happens to define under it is still
                // shipped, but its absence is not reported.
                var declaresTone = !string.IsNullOrWhiteSpace(toneBase) || slots.Any(s => !string.IsNullOrWhiteSpace(s));
                if (string.IsNullOrWhiteSpace(toneBase)) toneBase = $"{partitionName}_base";

                var ids = ArrangementIds(options, result.Count, chartArrangement.Name, partition);
                result.Add(new RsPackageArrangement
                {
                    ArrangementName = typeName,
                    PartitionName = partitionName,
                    ChartName = chartArrangement.Name,
                    SongPartition = partition,
                    PersistentId = ids.PersistentId,
                    MasterId = ids.MasterId,
                    Sng = build.Sng,
                    NoteCounts = build.NoteCounts,
                    Domain = arrangement,
                    ToneBase = toneBase,
                    ToneSlots = slots,
                    ToneDefinitions = ResolveToneDefinitions(
                        toneDefinitions, toneBase, slots, arrangement.Name, declaresTone ? warnings : [], toneRepair)
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                warnings.Add($"Arrangement '{arrangement.Name}' was skipped: {ex.Message}");
                _logger.Warning(AppLogCategory.Project,
                    $"Export skipped arrangement. Name='{arrangement.Name}'.", ex);
            }
        }

        if (VocalsExportPolicy.PackageTrack(project, options.IncludeVocals) is { Track: var vocalTrack })
        {
            var ids = ArrangementIds(options, result.Count, VocalsIdentityKey, 1);
            result.Add(new RsPackageArrangement
            {
                ArrangementName = "Vocals",
                PartitionName = "vocals",
                SongPartition = 1,
                PersistentId = ids.PersistentId,
                MasterId = ids.MasterId,
                Sng = SngFromDomainMapper.BuildVocals(vocalTrack, project.Beats, options.Identity?.ConversionTime),
                NoteCounts = null,
                Domain = null
            });
        }

        return result;
    }

    /// <summary>The whole <c>toolkit.version</c> entry: the tool and its version, the author, the song's version and the comment.</summary>
    internal static string ToolkitVersionText(string author, RocksmithBuildOptions options) =>
        $"Toolkit version: {ToolkitName(options)}\nPackage Author: {author}\nPackage Version: {ToolkitVersion(options.Version)}\nPackage Comment: Remastered";

    /// <summary>The tool line of <c>toolkit.version</c>, <c>DotCharter v1.3.0</c>: DLC Builder writes its own name and version there.</summary>
    internal static string ToolkitName(RocksmithBuildOptions options) =>
        $"DotCharter v{options.Identity?.ToolVersion ?? DotCharter.Services.Infrastructure.AppVersionInfo.Current}";

    /// <summary>The version as <c>toolkit.version</c> carries it: one line, "1" when blank.</summary>
    private static string ToolkitVersion(string version) =>
        string.IsNullOrWhiteSpace(version) ? "1" : string.Join(' ', version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static RsArrangementIdentity ArrangementIds(RocksmithBuildOptions options, int index, string name, int partition)
    {
        if (options.SharedArrangementIds?.TryGetValue(name, out var shared) == true) return shared.Ids;
        var ids = options.Identity is { } identity
            ? identity.Arrangements[index]
            : new RsArrangementIdentity(Guid.NewGuid(), RandomNumberGenerator.GetInt32(RsMasterIds.CustomMasterIdFloor, int.MaxValue));
        options.SharedArrangementIds?.TryAdd(name, new RsSharedArrangement(ids, partition));
        return ids;
    }

    /// <summary>
    ///     The effective tone-definition set for a build: the imported
    ///     passthrough bodies from <see cref="RocksmithBuildOptions.ToneDefinitions"/>
    ///     overlaid with the project's own authored <see cref="ChartProject.ToneDefinitions"/>
    ///     — an authored tone always wins by key.
    ///     <para>
    ///         Lookup is by KEY <b>or</b> NAME, trimmed and case-insensitive,
    ///         which is exactly how <c>Core/Utilities/ChartToneNames</c> and
    ///         <c>RsTonePlaybackCoordinator</c> resolve a <c>ToneChange</c> at
    ///         playback. It is not an ordinal key-only dictionary, i.e. the
    ///         STRICTEST resolver in the app — and the rule is that anything
    ///         stricter reports a tone as missing that the rig then plays. The
    ///         Tone Designer's Generate is what walks into it: it declares the
    ///         tone's NAME in the slot (the name is what the tone change
    ///         references) while storing the SANITIZED key, so "Clean Verse"
    ///         and <c>Clean_Verse</c> would fail to meet here and the body would be left
    ///         out of the package — the D01 fault that dialog exists to prevent,
    ///         created by the dialog, silently.
    ///     </para>
    ///     <para>A key wins over a name alias, so an authored tone actually
    ///     called by another's name cannot displace it.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> MergeToneDefinitions(
        ChartProject project, RocksmithBuildOptions options)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in options.ToneDefinitions)
            if (!string.IsNullOrWhiteSpace(key)) merged[key.Trim()] = json;
        foreach (var tone in project.ToneDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(tone.Key) && !string.IsNullOrWhiteSpace(tone.Json))
                merged[tone.Key.Trim()] = tone.Json;
        }

        // …then the NAME aliases, which never displace a real key.
        foreach (var tone in project.ToneDefinitions)
        {
            if (string.IsNullOrWhiteSpace(tone.Name) || string.IsNullOrWhiteSpace(tone.Json)) continue;
            merged.TryAdd(tone.Name.Trim(), tone.Json);
        }

        return merged;
    }

    /// <summary>Selects the tone definition bodies used by this arrangement:
    /// every distinct non-empty tone key (base first, then slots A-D) that has
    /// a definition in the merged set (see <see cref="MergeToneDefinitions"/>).
    /// A slot the set cannot answer is a tone the game will switch to and find
    /// nothing behind, so it is REPORTED rather than quietly dropped — with the rename that
    /// repairs it when <see cref="ToneKeyRepair" /> finds one without doubt.</summary>
    private static string[] ResolveToneDefinitions(
        IReadOnlyDictionary<string, string> toneDefinitions, string toneBase, string[] slots,
        string arrangementName, List<string> warnings, Lazy<ToneKeyRepairProposal?> repair)
    {
        var wanted = slots.Prepend(toneBase)
            .Select(tone => (tone ?? "").Trim())
            .Where(tone => tone.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolved = new List<string>(wanted.Length);
        foreach (var tone in wanted)
        {
            if (toneDefinitions.TryGetValue(tone, out var json))
            {
                resolved.Add(json);
            }
            else
            {
                var hint = repair.Value is { } fix && ToneKeyEdits.Same(fix.To, tone)
                    ? $" The project defines '{fix.From}', which nothing plays: the export check's Fix renames it to '{tone}'."
                    : "";
                warnings.Add($"Arrangement '{arrangementName}' declares tone '{tone}' but the project " +
                             "has no definition for it; the game will have nothing to switch to." + hint);
            }
        }

        return resolved.ToArray();
    }

    // ---- assets ---------------------------------------------------------------

    internal static byte[] CreateShowlights(ChartProject project, double songLengthMs, List<string> warnings)
    {
        if (project.Showlights.Count == 0)
        {
            // Generated from the sections and notes (a minimal fog + beam + lasers set when there is
            // nothing to read), so the game has something to render. The project is not touched.
            warnings.Add("The project has no show lights; a set was generated from the sections and notes.");
            project = CloneWithDefaultShowlights(project, songLengthMs);
        }

        var writer = new RsXmlShowlightsWriter();
        var document = writer.Write(project);
        return Encoding.UTF8.GetBytes(document.Declaration + Environment.NewLine + document);
    }

    private static ChartProject CloneWithDefaultShowlights(ChartProject project, double songLengthMs)
    {
        // Only Beats + Showlights are read by the showlights writer.
        var stub = new ChartProject();
        foreach (var beat in project.Beats) stub.Beats.Add(beat);
        foreach (var light in RsDefaultShowlights.For(project, songLengthMs)) stub.Showlights.Add(light);
        return stub;
    }

    private IEnumerable<(int Size, byte[] Data)> CreateAlbumArt(RocksmithBuildOptions options, List<string> warnings)
    {
        int[] sizes = [64, 128, 256];

        if (!string.IsNullOrWhiteSpace(options.AlbumArtPath) && File.Exists(options.AlbumArtPath))
        {
            // ONE decode for all three sizes — see DdsEncoder.EncodeCoverArtSizes.
            foreach (var encoded in DdsEncoder.EncodeCoverArtSizes(options.AlbumArtPath, sizes))
                yield return encoded;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(options.AlbumArtPath))
            warnings.Add($"Album art file '{options.AlbumArtPath}' was not found; a placeholder was generated.");

        foreach (var size in sizes)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            {
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(size, size),
                    [new SKColor(30, 34, 52), new SKColor(96, 60, 128)],
                    SKShaderTileMode.Clamp);
                canvas.DrawRect(new SKRect(0, 0, size, size), paint);
            }

            yield return (size, DdsEncoder.EncodeDxt1(bitmap));
        }
    }

    /// <summary>A .wem on disk together with the codec actually inside it — the
    /// sound bank has to declare the same one or the game picks the wrong decoder.</summary>
    internal readonly record struct ResolvedWem(string Path, WemCodec Codec);

    /// <summary>
    ///     Reuses a cached <c>.wem</c> only when the codec can still be READ OUT of
    ///     it. Returns <c>null</c> when the cache is stale, missing or not a
    ///     recognisable wem, so the caller re-encodes instead.
    /// </summary>
    /// <remarks>
    ///     A filename is not evidence of a codec. <c>&lt;stem&gt;.wem</c> is an
    ///     ordinary name any tool may have produced, and the freshness test is only
    ///     an mtime comparison — so trusting the name let a hand-placed PCM
    ///     <c>song.wem</c> be declared Vorbis in the bank, which is a song that
    ///     loads, scrolls and plays SILENCE with no error anywhere. Detecting keeps
    ///     the bank/wem codec pairing structural rather than conventional.
    /// </remarks>
    internal static ResolvedWem? TryReuseCachedWem(string cachePath, string sourcePath)
    {
        if (!IsFresh(cachePath, sourcePath)) return null;

        try
        {
            using var cached = File.OpenRead(cachePath);
            var detected = WemCodecInfo.Detect(cached);
            return detected is null ? null : new ResolvedWem(cachePath, detected.Value);
        }
        catch (IOException)
        {
            // An unreadable cache is a cache miss, never a build failure.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Produces a <c>.wem</c> for the given audio. A <c>.wem</c> input is used
    ///     as-is (its codec is read from the file). Otherwise we encode
    ///     <see cref="RocksmithBuildOptions.AudioCodec"/> ourselves.
    ///     <para>
    ///         The encoded wem is cached in the project metadata folder and reused while it stays
    ///         newer than the source, so building the same song again — e.g. on every
    ///         save — does not re-encode the audio. A cache hit still has its codec
    ///         DETECTED (<see cref="TryReuseCachedWem" />): no path may declare a
    ///         codec it has not read out of the file.
    ///     </para>
    /// </summary>
    private ResolvedWem ResolveWem(string audioPath, RocksmithBuildOptions options, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new FileNotFoundException(
                $"Audio file not found: '{audioPath}'.", audioPath);
        }

        if (audioPath.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
        {
            using var existing = File.OpenRead(audioPath);
            var detected = WemCodecInfo.Detect(existing);
            if (detected is null)
            {
                warnings.Add(
                    $"'{Path.GetFileName(audioPath)}' is not a recognisable wem; assuming Vorbis. " +
                    "If the song is silent in-game, this is why.");
                return new ResolvedWem(audioPath, WemCodec.Vorbis);
            }

            return new ResolvedWem(audioPath, detected.Value);
        }

        var codec = ManagedCodec(options.AudioCodec);
        var sourceSignature = ProjectVolumes.Signature(audioPath);

        var cache = ManagedWemCachePath(audioPath, codec, CacheRoot(options));
        if (ManagedAudioCache.IsCurrent(cache, audioPath) && CompanionOwnership.IsUnmodified(cache) &&
            TryReuseCachedWem(cache, audioPath) is { } reusable && reusable.Codec == codec)
        {
            _logger.Info(AppLogCategory.Project, $"Reusing cached wem '{cache}'.");
            return reusable;
        }

        _logger.Info(AppLogCategory.Project,
            $"Encoding wem ({codec}). Source='{audioPath}'.");

        var pcm = PcmAudioLoader.Load(audioPath, warnings);
        if (!CompanionOwnership.WriteIfUnmodified(cache, target =>
            {
                var staging = target + ".writing";
                try
                {
                    WemWriter.WriteFile(staging, codec, pcm.Samples, pcm.Channels, pcm.SampleRate);
                    AtomicFile.Publish(staging, target);
                }
                finally
                {
                    if (File.Exists(staging)) RecycleBin.Remove(staging);
                }
            }, out var reason))
        {
            throw new IOException($"Managed audio cache was left alone: {reason}.");
        }
        ManagedAudioCache.Record(cache, sourceSignature);

        if (codec == WemCodec.Pcm)
        {
            warnings.Add(
                $"Audio encoded as PCM ({new FileInfo(cache).Length / (1024 * 1024)} MB), about 11x the size " +
                "of Vorbis. PCM has not been verified in-game.");
        }

        return new ResolvedWem(cache, codec);
    }

    /// <summary>
    ///     The preview wem. An explicit <see cref="RocksmithBuildOptions.PreviewAudioPath"/>
    ///     wins; otherwise a 28 s faded clip is cut from the main audio (cached as
    ///     <c>.dotcharter/cache/</c>) and encoded like any source.
    ///     A <c>.wem</c> main input cannot be decoded for clipping, and any clip
    ///     failure degrades to the old ship-the-full-song behavior.
    /// </summary>
    private ResolvedWem ResolvePreviewWem(
        ResolvedWem mainWem, RocksmithBuildOptions options, string audioPath, double? projectPreviewStartMs, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(options.PreviewAudioPath))
            return ResolveWem(options.PreviewAudioPath, options, warnings);

        if (audioPath.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            return mainWem;

        try
        {
            var previewWav = ManagedAudioCache.PreviewPath(audioPath, CacheRoot(options));

            // Cut on every build: a cached clip may have been cut at another start (or at the automatic one).
            var pcm = PcmAudioLoader.Load(audioPath, warnings);
            var totalMs = pcm.Duration.TotalMilliseconds;
            var startMs = PreviewClip.ClampStartMs(
                options.PreviewStartMs ?? projectPreviewStartMs ?? PreviewClip.DefaultStartMs(totalMs), totalMs);
            if (!CompanionOwnership.WriteIfUnmodified(previewWav, target =>
                {
                    var staging = target + ".writing";
                    try
                    {
                        PcmAudioLoader.WriteWav(staging, PreviewClip.Create(pcm, startMs));
                        AtomicFile.Publish(staging, target);
                    }
                    finally
                    {
                        if (File.Exists(staging)) RecycleBin.Remove(staging);
                    }
                }, out var reason))
            {
                throw new IOException($"Preview cache was left alone: {reason}.");
            }

            return ResolveWem(previewWav, options, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Preview clip generation failed ({ex.Message}); the full song is used as the preview.");
            return mainWem;
        }
    }

    /// <summary>The codec the managed path writes for a request. IMA-ADPCM is never
    /// written: Rocksmith does not play it (the song never starts).</summary>
    internal static WemCodec ManagedCodec(WemCodec requested) =>
        requested == WemCodec.Pcm ? WemCodec.Pcm : WemCodec.Vorbis;

    /// <summary>Where a managed-encoded wem is cached. The complete source filename
    /// and codec plus decode policy keep recordings, encodings and decode levels in separate cache slots.</summary>
    private static string ManagedWemCachePath(string audioPath, WemCodec codec, string? projectDirectory = null) =>
        ManagedAudioCache.WemPath(audioPath, projectDirectory);

    private static string CacheRoot(RocksmithBuildOptions options) =>
        string.IsNullOrWhiteSpace(options.ProjectDirectory)
            ? ProjectFolderLayout.RootForArtifact(options.AudioPath) : Path.GetFullPath(options.ProjectDirectory);

    private static bool IsFresh(string candidate, string source) =>
        File.Exists(candidate) &&
        File.GetLastWriteTimeUtc(candidate) >= File.GetLastWriteTimeUtc(source);

    private static BuiltBank CreateBank(string bankName, ResolvedWem wem, float volume, bool isPreview, SoundBankIds? ids)
    {
        using var bankData = new MemoryStream();
        string wemName;
        using (var audio = File.OpenRead(wem.Path))
        {
            // The bank's codec plugin id must match the wem's actual codec.
            wemName = SoundBankWriter.Generate(bankName, audio, bankData, volume, isPreview, wem.Codec, ids);
        }

        return new BuiltBank(bankName, bankData.ToArray(), wemName, wem.Path);
    }
}
