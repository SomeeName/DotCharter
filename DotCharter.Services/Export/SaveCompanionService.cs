using System.Collections.Concurrent;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.Charter;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Export;

/// <summary>
///     Writes the derived artifacts that sit in the saved project's export folder: a Charter
///     <c>.rscp</c>, an Editor on Fire <c>.eof</c>, a DLC Builder <c>.rs2dlc</c> and a
///     Rocksmith <c>.psarc</c>.
/// </summary>
/// <remarks>
///     All are DERIVED data. None may ever fail a project save — the <c>.dcproj</c>
///     is the source of truth and it is already on disk (atomically) by the time
///     these run. Callers catch; these methods log and degrade.
///     <para>
///         Derived does NOT mean disposable. The <c>.rscp</c>, the <c>.eof</c> and the
///         <c>.rs2dlc</c> are three other editors' own project formats, so all three go through
///         <see cref="CompanionOwnership" />: a save overwrites only the file it
///         wrote itself. The <c>.psarc</c> primary write and XML bundle are unconditional;
///         removing an alternate package requires this instance's publication record.
///     </para>
///     <para>
///         The <c>.eof</c> is cheap (a plain binary write) and goes on every save.
///         The <c>.psarc</c> is not — it converts every arrangement to SNG and
///         copies the audio — so it belongs on a background job, coalesced.
///     </para>
/// </remarks>
public interface ISaveCompanionService
{
    /// <summary>
    ///     Writes the Charter-compatible <c>&lt;project&gt;.rscp</c> in <c>export/</c>:
    ///     the project's XML cut down to what Charter's model knows
    ///     (<see cref="CharterProjectSchema" />), so the folder opens in Charter too. Returns
    ///     the path, or null when the project is play-only, when the project path is itself
    ///     a <c>.rscp</c>, or when the <c>.rscp</c> on disk is one this app did not write
    ///     (a Charter project, or one the charter edited in Charter).
    /// </summary>
    /// <remarks>
    ///     The default writes nothing, for hosts that keep no companions; the app's
    ///     <see cref="SaveCompanionService" /> implements it.
    /// </remarks>
    string? WriteCharterProject(string projectPath, ChartProject snapshot) => null;

    /// <summary>
    ///     Writes <c>&lt;project&gt;.eof</c> in <c>export/</c> and, when enabled,
    ///     makes sure the <c>.ogg</c> it references actually exists there — EOF
    ///     resolves its audio relative to the folder the <c>.eof</c> lives in, and
    ///     prompts when it is missing. Returns the path, or null when disabled — or
    ///     when the <c>.eof</c> on disk is one the charter edited in EOF.
    /// </summary>
    string? WriteEof(string projectPath, ChartProject snapshot);

    /// <summary>
    ///     Writes the DLC Builder project file beside the <c>DTRS XML</c> bundle, so
    ///     the RS-XML the bundle already contains is openable in DLC Builder — and
    ///     droppable onto CustomsForge's "Import rs2dlc" control — without a manual
    ///     export step. Returns the path, or null when disabled, when the bundle is
    ///     absent, or when the <c>.rs2dlc</c> on disk is one the charter edited in
    ///     DLC Builder (see <see cref="CompanionOwnership" />).
    /// </summary>
    /// <remarks>
    ///     Must run AFTER <c>IProjectDocumentService.SaveDlcBuilderXmlBundle</c>: it
    ///     references the folder that method swaps into place, and it names the
    ///     files that method wrote.
    /// </remarks>
    string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs);

    /// <summary>
    ///     Builds the shared artist/title/version release name in <c>export/</c>. Returns the path,
    ///     or null when disabled or when the project has no usable audio.
    /// </summary>
    /// <remarks>
    ///     <paramref name="audioLengthMs" /> has to be PASSED IN — the builder cannot
    ///     resolve it and, without it, falls back to the chart's own extent. A beat
    ///     grid routinely runs past the audio (it is usually laid to a round number
    ///     of measures), so omitting it gave the companion psarc a <c>songLength</c>
    ///     past the end of the song — and one that disagreed with the RS-XML bundle
    ///     the SAME save writes beside it.
    /// </remarks>
    Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot, double? audioLengthMs,
        CancellationToken cancellationToken);
}

public sealed class SaveCompanionService : ISaveCompanionService
{
    private readonly IConfigurationService _config;
    private readonly IAppLogger _logger;
    private readonly Action<string> _recycle;
    private readonly RocksmithPackageBuilder _builder;
    private readonly Func<string, PcmAudio> _decodeVolume;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, MediaProcess.MediaProcessResult> _runMedia;

    /// <summary>The last drop summary logged per companion path, so an unchanged loss is logged once, not per save.</summary>
    private readonly ConcurrentDictionary<string, string> _charterDropsLogged = new(StringComparer.OrdinalIgnoreCase);

    public SaveCompanionService(IConfigurationService config, IAppLogger logger)
        : this(config, logger, (executable, arguments, timeout) => MediaProcess.Run(executable, arguments, timeout))
    {
    }

    internal SaveCompanionService(IConfigurationService config, IAppLogger logger,
        Func<string, IReadOnlyList<string>, TimeSpan, MediaProcess.MediaProcessResult>? runMedia = null,
        Func<string, PcmAudio>? decodeVolume = null, Action<string>? recycle = null)
    {
        _runMedia = runMedia ?? ((executable, arguments, timeout) => MediaProcess.Run(executable, arguments, timeout));
        _recycle = recycle ?? RecycleBin.Remove;
        _decodeVolume = decodeVolume ?? (path => PcmAudioLoader.Load(path, []));
        _config = config;
        _logger = logger;
        _builder = new RocksmithPackageBuilder(logger);
    }

    /// <summary>
    /// Play-only content is never exported: every companion is an export, so a
    ///     play-only snapshot writes none. Skipped and logged, never thrown — a
    ///     companion may not fail the save.
    /// </summary>
    private bool RefusedAsPlayOnly(ChartProject snapshot, string companion)
    {
        if (!PlayOnlyExportGuard.IsPlayOnly(snapshot)) return false;
        _logger.Info(AppLogCategory.Project, $"{companion} skipped: the project is play-only.");
        return true;
    }

    public string? WriteCharterProject(string projectPath, ChartProject snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (CharterRscp.IsCharterProject(projectPath)) return null;
        var companionPath = ProjectFolderLayout.CompanionPath(projectPath, ".rscp");
        if (string.Equals(Path.GetFullPath(companionPath), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
            return null;
        if (RefusedAsPlayOnly(snapshot, "Charter .rscp companion")) return null;

        var config = _config.LoadAppConfig();
        EnsureCompanionMedia(projectPath, snapshot, null, config);
        var doc = CharterRscpProjection.Build(ExportCompanionMedia.Snapshot(snapshot), out var dropped);

        // Charter's own project format: a charter who opened and saved it in Charter,
        // or a Charter project this one was imported from, is never overwritten.
        if (!CompanionOwnership.WriteIfUnmodified(companionPath, target =>
                {
                    using var buffer = new MemoryStream();
                    RscpChartSerializer.WriteDocument(buffer, doc);
                    AtomicFile.WriteAllBytes(target, buffer.ToArray());
                },
                out var skipReason))
        {
            _logger.Info(AppLogCategory.Project, $"Leaving the .rscp alone: {skipReason}. Path='{companionPath}'.");
            return null;
        }

        var summary = dropped.LogLine();
        if (!_charterDropsLogged.TryGetValue(companionPath, out var logged) || logged != summary)
        {
            _charterDropsLogged[companionPath] = summary;
            _logger.Info(AppLogCategory.Project,
                $"Charter .rscp companion written. Path='{companionPath}'. DotCharter-only data it cannot carry " +
                $"(dropped from the companion, kept in the .dcproj): {summary}.");
        }

        return companionPath;
    }

    public string? WriteEof(string projectPath, ChartProject snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(snapshot);

        var config = _config.LoadAppConfig();
        if (!config.SaveCompanionEof) return null;
        if (RefusedAsPlayOnly(snapshot, "SaveCompanionEof")) return null;

        var directory = ProjectFolderLayout.ExportDirectory(projectPath);
        var eofPath = ProjectFolderLayout.CompanionPath(projectPath, ".eof");
        if (config.SaveCompanionEofAudio) EnsureCompanionMedia(projectPath, snapshot, null, config);

        // Same rule as the .rs2dlc below: the .eof is Editor on Fire's own project
        // format, so a charter who opened and edited it there must not lose that
        // work to an unrelated Ctrl+S here.
        EofExportResult? result = null;
        if (!CompanionOwnership.WriteIfUnmodified(eofPath,
                target => result = EofExporter.ExportToFile(
                    ExportCompanionMedia.Snapshot(snapshot), target, VocalsExportPolicy.IncludeVocals(config)),
                out var skipReason))
        {
            _logger.Info(AppLogCategory.Project,
                $"Leaving the .eof alone: {skipReason}. Path='{eofPath}'.");
            // The .eof we did NOT write still resolves its audio beside itself, so
            // the sibling ogg stays this companion's job.

            return null;
        }

        // Silent on screen (a companion never nags a save), but the log names WHAT was lost,
        // not just how much; Tools ▸ Chart quality report lists every place.
        var losses = EofLossSummary.From(result!.Report);
        _logger.Info(AppLogCategory.Project,
            $"EOF companion written. Path='{eofPath}', lossless={losses.IsLossless}, " +
            $"approximated={losses.Approximated}, dropped={losses.Dropped}" +
            (losses.IsLossless ? "." : $" ({losses.LogLine()})."));



        return eofPath;
    }

    public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(snapshot);

        var config = _config.LoadAppConfig();
        if (!config.SaveCompanionRs2Dlc) return null;
        if (RefusedAsPlayOnly(snapshot, "SaveCompanionRs2Dlc")) return null;

        var directory = ProjectDirectory(projectPath);
        var bundleDirectory = DlcBuilderBundle.DirectoryFor(projectPath);

        // The bundle is written by SaveDlcBuilderXmlBundle and swapped in by
        // rename. If it is not there, that step was disabled or failed — a
        // project file naming XML that does not exist is worse than none.
        if (!Directory.Exists(bundleDirectory))
        {
            _logger.Info(AppLogCategory.Project,
                $"Skipping .rs2dlc companion: no '{DlcBuilderBundle.DirectoryName}' bundle at '{bundleDirectory}'.");
            return null;
        }

        // Reference what actually resolved on disk, by name relative to the
        // project folder — the builder turns that into the bundle-relative "..\".
        var media = EnsureCompanionMedia(projectPath, snapshot, audioLengthMs, config);
        directory = ProjectFolderLayout.ExportDirectory(projectPath);
        var dto = DlcBuilderProjectFile.Build(config, snapshot, directory, media.Audio, media.Cover, audioLengthMs);

        // The PROJECT ROOT, not the bundle: SaveDlcBuilderXmlBundle swaps the
        // bundle in by renaming the old one aside, so a file written inside it is
        // moved to 'DTRS XML.old' on the next save and deleted on the one after.
        var path = ProjectFolderLayout.CompanionPath(projectPath, ".rs2dlc");

        // The .rs2dlc is DLC Builder's own project format, so the charter opens it
        // and assigns TONES there — which we always write empty. Overwrite only the
        // file we ourselves last wrote.
        if (!CompanionOwnership.WriteIfUnmodified(path, target => Rs2DlcWriter.WriteTo(target, dto),
                out var skipReason))
        {
            _logger.Info(AppLogCategory.Project,
                $"Leaving the .rs2dlc alone: {skipReason}. Path='{path}'.");
            return null;
        }

        _logger.Info(AppLogCategory.Project,
            $"DLC Builder project written. Path='{path}', arrangements={dto.Arrangements.Length}, key='{dto.DlcKey}'.");
        return path;
    }

    /// <summary>
    ///     This song's own volumes: what the project holds, measured first (off the UI thread) when unset or measured
    ///     from other audio. The snapshot is a copy, so a measurement made here is not kept; attaching the audio and
    ///     exporting keep it in the project.
    /// </summary>
    internal async Task<(double Audio, double Preview)> VolumesForAsync(ChartProject snapshot, string? audioPath)
    {
        if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
        {
            var targetLufs = _config.LoadAppConfig().ExportAutoVolumeTargetLufs;
            await Task.Run(() => ProjectVolumes.Measure(snapshot.Chart, audioPath, null, null,
                warning => _logger.Warning(AppLogCategory.Project, warning), _decodeVolume, targetLufs));
        }

        return (ProjectVolumes.AudioOrDefault(snapshot.Chart), ProjectVolumes.PreviewOrDefault(snapshot.Chart));
    }

    public async Task<string?> BuildPsarcAsync(
        string projectPath, ChartProject snapshot, double? audioLengthMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(snapshot);

        var config = _config.LoadAppConfig();
        if (!config.SaveCompanionPsarc) return null;
        if (RefusedAsPlayOnly(snapshot, "SaveCompanionPsarc")) return null;

        if (snapshot.Beats.Count == 0 || snapshot.Arrangements.Count == 0)
        {
            _logger.Info(AppLogCategory.Project,
                "Skipping companion psarc: the project has no beat map or no arrangements.");
            return null;
        }

        var directory = ProjectDirectory(projectPath);
        var audio = ResolveAudio(directory, snapshot.AudioFileName);
        if (audio is null)
        {
            _logger.Info(AppLogCategory.Project,
                $"Skipping companion psarc: no audio file for '{snapshot.AudioFileName}'.");
            return null;
        }

        // Never ship a game-crashing psarc beside a save: the export dialog
        // refuses these charts and the auto-companion must not undercut it.
        // The save and the .eof are untouched, and the previous psarc (if
        // any) survives. KeyNoNotes is tolerated — an empty arrangement is a
        // normal early-charting state and the SNG mapper emits a valid level.
        var blocking = new ChartExportValidator().Validate(snapshot)
            .Where(f => f.Severity == ExportFindingSeverity.Error &&
                        f.MessageKey != ChartExportValidator.KeyNoNotes)
            .ToList();
        if (blocking.Count > 0)
        {
            var summary = string.Join(", ", blocking
                .GroupBy(f => f.MessageKey)
                .Select(g => $"{g.Key.Replace("Export.Validation.", "")} x{g.Count()}"));
            _logger.Warning(AppLogCategory.Project,
                $"Skipping companion psarc: {blocking.Count} blocking validation error(s) ({summary}). " +
                "Open the Rocksmith export dialog for details.");
            return null;
        }

        var (audioVolume, previewVolume) = await VolumesForAsync(snapshot, audio);
        var output = Path.Combine(ProjectFolderLayout.ExportDirectory(projectPath), ExportPathDefaults.PackageFileName(snapshot));

        // Build to a temp file and swap, so a cancelled or failed build never
        // replaces a good psarc with a truncated one.
        var staging = output + ".building";

        try
        {
            var result = await _builder.BuildPsarcAsync(snapshot, new RocksmithBuildOptions
            {
                OutputPath = staging,
                ProjectDirectory = directory,
                // A stale 7-string chord sidecar is looked for beside the FINAL package.
                ChordSidecarPackagePath = output,
                AudioPath = audio,
                // The same value the export dialog passes. Left at 0 the builder
                // resolves the chart's own extent instead, which on any chart whose
                // beat grid outruns the audio is past the end of the song.
                SongLengthMs = audioLengthMs is { } length && length > 0 ? length : 0,
                AlbumArtPath = ResolveCoverArt(directory, snapshot.Chart.CoverArtPath),
                AudioCodec = ParseCodec(config.SaveCompanionPsarcCodec),
                AppId = string.IsNullOrWhiteSpace(config.LastExportAppId) ? "248750" : config.LastExportAppId,
                FallbackCharter = config.DefaultCharterName,
                AudioVolume = audioVolume,
                PreviewVolume = previewVolume,
                IncludeVocals = VocalsExportPolicy.IncludeVocals(config),
                DlcBuilderSteps = DlcBuilderSteps.FromConfig(config),
                ChordNameOptions = DotCharter.Services.Editing.ChordShapes.NewChordNames.OptionsFor(config)
            }, progress: null, cancellationToken);

            // The build's warnings are the ONLY notice this path can give of a
            // skipped arrangement, an unrecognisable wem ("if the song is silent
            // in-game, this is why"), a missing tone body or a failed Wwise
            // conversion. The export dialog shows them; this path must not drop the
            // list on the floor (the builder logs only a count), or every one of
            // them reaches nobody. Logged BEFORE the swap so a failing swap cannot
            // swallow them too.
            foreach (var warning in result.Warnings)
                _logger.Warning(AppLogCategory.Project, $"Companion psarc: {warning}");

            cancellationToken.ThrowIfCancellationRequested();
            output = Path.Combine(Path.GetDirectoryName(output)!,
                RocksmithPackageName.For(snapshot, snapshot.Chart.Version, extended: result.IsPsxr));
            if (!CompanionOwnership.WriteIfUnmodified(output,
                destination => AtomicFile.ReplaceWithRetry(result.PsarcPath, destination), out var reason))
            {
                _logger.Warning(AppLogCategory.Project, $"Companion psarc was left in place: {reason}.");
                return null;
            }
            RemovePublishedSiblings(output, snapshot);

            _logger.Info(AppLogCategory.Project,
                $"Companion psarc built. Path='{output}', warnings={result.Warnings.Count}.");
            return output;
        }
        finally
        {
            // best-effort cleanup; the real error propagates
            if (File.Exists(staging)) _recycle(staging);
        }
    }

    private void RemovePublishedSiblings(string output, ChartProject snapshot)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RocksmithDlcKey.Create(snapshot.Chart.Artist ?? "", snapshot.Chart.SongName ?? "")
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Chart.DlcKey) && !RocksmithDlcKey.IsInvalidTypedKey(snapshot.Chart.DlcKey))
            keys.Add(new string(snapshot.Chart.DlcKey.Where(char.IsAsciiLetterOrDigit).ToArray()));
        foreach (var sibling in Directory.EnumerateFiles(Path.GetDirectoryName(output)!, "*.psarc"))
        {
            if (sibling.Equals(output, StringComparison.OrdinalIgnoreCase) ||
                !sibling.EndsWith("_p.psarc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var legacy = keys.Any(key => Path.GetFileName(sibling).Equals(key + "_p.psarc", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(sibling).Equals(key + "_rsxr_p.psarc", StringComparison.OrdinalIgnoreCase));
            if (!legacy && !RocksmithPackageName.IsReleaseFor(snapshot, sibling)) continue;
            if (CompanionOwnership.IsUnmodified(sibling))
            {
                _recycle(sibling);
                if (!File.Exists(sibling)) CompanionOwnership.Forget(sibling);
                continue;
            }
            _logger.Warning(AppLogCategory.Project,
                $"Companion psarc: '{sibling}' was left in place; it holds an older package of this song.");
        }
    }

    private (string? Audio, string? Cover) EnsureCompanionMedia(string projectPath, ChartProject snapshot,
        double? audioLengthMs, AppConfig config) =>
        new ExportCompanionMedia(_logger, _runMedia).Ensure(projectPath, snapshot, audioLengthMs, config);

    /// <summary>The project's audio on disk. AudioFileName is normally a bare file
    /// name resolved against the project folder, but may be an absolute path.</summary>
    internal static string? ResolveAudio(string directory, string audioFileName, Func<string, bool>? fileExists = null)
    {
        if (!ProjectRelativePath.IsReadableMediaReference(audioFileName)) return null;
        return ProjectAudioLocator.TryResolve(Path.Combine(directory, "project.dcproj"), audioFileName, fileExists ?? File.Exists);
    }

    /// <summary>The chart's cover art on disk. CoverArtPath is a bare file name
    /// relative to the project folder; a rooted, UNC or escaping one is refused
    /// unprobed (<see cref="ProjectRelativePath" />). Returns "" when unset, refused or missing, so the builder generates a placeholder
    /// rather than the DDS pipeline choking on a bad path.</summary>
    private static string ResolveCoverArt(string directory, string coverArtPath)
    {
        if (string.IsNullOrWhiteSpace(coverArtPath)) return "";

        // Copied into the export, so only a file inside the project folder qualifies.
        var full = ProjectRelativePath.TryResolve(directory, coverArtPath);
        return full != null && File.Exists(full) ? full : "";
    }

    private static string ProjectDirectory(string projectPath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Directory.GetCurrentDirectory();

    /// <summary>Vorbis unless the user chose PCM. A stored "ImaAdpcm" (an older default)
    /// and any unknown value read as Vorbis: the game does not play
    /// IMA-ADPCM, the song never starts.</summary>
    internal static WemCodec ParseCodec(string? configured) =>
        string.Equals(configured, nameof(WemCodec.Pcm), StringComparison.OrdinalIgnoreCase)
            ? WemCodec.Pcm
            : WemCodec.Vorbis;
}
