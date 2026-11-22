using System.Collections.ObjectModel;
using System.Diagnostics;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Showlights;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Parsing.Xml.Vocals;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Services.Infrastructure.Documents;

public class ProjectDocumentService : IProjectDocumentService
{
    private const string ShowlightsFileName = DlcBuilderBundle.ShowlightsFileName;
    private readonly IAppLogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Action<string> _recycle;

    public ProjectDocumentService(IServiceProvider serviceProvider, IAppLogger logger)
        : this(serviceProvider, logger, RecycleBin.Remove) { }

    internal ProjectDocumentService(IServiceProvider serviceProvider, IAppLogger logger, Action<string> recycle)
    {
        _recycle = recycle;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///     The atomic-write temp suffix used by save paths (write to
    ///     <c>project.dcproj.saving</c>, then swap in). The serializer is chosen by
    ///     the REAL extension underneath it — the temp suffix must never change
    ///     which format gets written, and a leftover temp stays loadable.
    /// </summary>
    private const string AtomicTempSuffix = ".saving";

    private static string ResolveFormat(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (name.EndsWith(AtomicTempSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^AtomicTempSuffix.Length];

        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            // A .dcproj and a Charter .rscp share one XML format, so one serializer reads both.
            ProjectFiles.Extension or CharterRscp.Extension => "rscp",
            ".xml" => "xml",
            _ => throw new NotSupportedException($"File format {ext} is not supported.")
        };
    }

    public ChartProject LoadChart(string filePath)
    {
        var format = ResolveFormat(filePath);

        var fileSize = SafeGetFileSize(filePath);
        _logger.Info(AppLogCategory.Project,
            $"Loading chart '{filePath}' (format={format}, size={fileSize} bytes).");

        var serializer = _serviceProvider.GetRequiredKeyedService<IChartSerializer>(format);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var project = serializer.Load(filePath);
            stopwatch.Stop();
            _logger.Info(AppLogCategory.Project,
                $"Loaded chart '{filePath}' in {stopwatch.ElapsedMilliseconds}ms. Arrangements={project.Arrangements.Count}, beats={project.Beats.Count}.");
            return project;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(AppLogCategory.Project,
                $"Failed to load chart '{filePath}' after {stopwatch.ElapsedMilliseconds}ms.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        => PrepareSave(filePath, project, audioLengthMs)(filePath);

    /// <inheritdoc />
    public Action<string> PrepareSave(string destinationPath, ChartProject project, double? audioLengthMs = null)
    {
        var format = ResolveFormat(destinationPath);
        var serializer = _serviceProvider.GetRequiredKeyedService<IChartSerializer>(format);
        // "RS XML for 6-string tools": the file the user saves as .xml is often headed
        // for EOF, which aborts on a 7th string. Off by default: the spec XML keeps all of them.
        if (serializer is XmlChartSerializer xml)
        {
            var config = TryLoadAppConfig();
            xml.SixStringProjection = config?.RsXmlSixStringProjection ?? false;
            xml.Warning = message => _logger.Warning(AppLogCategory.Project, message);
            xml.MoverPhrases = config?.RsXmlMoverPhrases ?? false;
            xml.ChordNameOptions = config == null ? DoubleStopNameOptions.Default : NewChordNames.OptionsFor(config);
        }
        var kind = XmlChartSerializer.GetDocumentKind(destinationPath, project);
        return filePath => WriteChart(filePath, project, audioLengthMs, serializer, kind);
    }

    private void WriteChart(string filePath, ChartProject project, double? audioLengthMs,
        IChartSerializer serializer, XmlChartSerializer.DocumentKind kind)
    {
        _logger.Info(AppLogCategory.Project,
            $"Saving chart '{filePath}' (arrangements={project.Arrangements.Count}, beats={project.Beats.Count}).");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (serializer is XmlChartSerializer xml) xml.Save(filePath, project, kind, audioLengthMs, failOnRefusal: true);
            else serializer.Save(filePath, project, audioLengthMs);
            stopwatch.Stop();
            var fileSize = SafeGetFileSize(filePath);
            _logger.Info(AppLogCategory.Project,
                $"Saved chart '{filePath}' in {stopwatch.ElapsedMilliseconds}ms (size={fileSize} bytes).");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(AppLogCategory.Project,
                $"Failed to save chart '{filePath}' after {stopwatch.ElapsedMilliseconds}ms.", ex);
            throw;
        }
    }

    public void RefreshArrangementFileManifest(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        project.ArrangementFiles.Clear();
        for (var i = 0; i < project.Arrangements.Count; i++)
            project.ArrangementFiles.Add(BuildArrangementFileName(i + 1, project.Arrangements[i]));
    }

    /// <inheritdoc />
    public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) =>
        SaveDlcBuilderXmlBundleToDirectory(GetDlcBuilderXmlDirectory(projectPath), project, audioLengthMs);

    public void SaveDlcBuilderXmlBundleToDirectory(string exportDirectory, ChartProject project, double? audioLengthMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentNullException.ThrowIfNull(project);

        // The bundle is Rocksmith XML: play-only content is never exported.
        // Skipped, never thrown — a companion may not fail the save.
        if (PlayOnlyExportGuard.IsPlayOnly(project)) return;

        RefreshArrangementFileManifest(project);

        // Write the whole bundle into a sibling staging directory and swap
        // it in only after every file succeeded. The old delete-then-write
        // sequence left the DTRS XML folder half-populated (stale or
        // missing arrangements) if anything failed mid-bundle.
        var stagingDirectory = exportDirectory + ".saving";
        OwnedDlcBuilderBundle.RequireOwned(exportDirectory);
        OwnedDlcBuilderBundle.Recycle(stagingDirectory, _recycle);
        Directory.CreateDirectory(stagingDirectory);

        // The bundle follows the same "RS XML for 6-string tools" option as the saved .xml: off
        // writes every string the chart has (for rs7string tooling), on writes the six-string
        // projection a stock DLC Builder indexes. Six-string guitars and basses are identical either way.
        var config = TryLoadAppConfig() ?? new AppConfig();
        var arrangementWriter = new RsXmlSongWriter
        {
            SixStringProjection = config.RsXmlSixStringProjection, MoverPhrases = config.RsXmlMoverPhrases,
            Warning = message => _logger.Warning(AppLogCategory.Project, message),
            ChordNameOptions = NewChordNames.OptionsFor(config)
        };
        var vocalsWriter = new RsXmlVocalsWriter();
        var showlightsWriter = new RsXmlShowlightsWriter();

        var arrangementCount = 0;
        long cloneTicks = 0, stepsTicks = 0, writeTicks = 0;
        var timer = Stopwatch.StartNew();
        // The bundle is what DLC Builder builds from, and the .rs2dlc names it: each
        // arrangement goes in with the fixes and levels the package export gives it.
        var steps = DlcBuilderSteps.FromConfig(config);
        try
        {
            var songLengthMs = project.Beats.Count > 0 ? project.Beats[^1].Time : 0;
            for (var i = 0; i < project.Arrangements.Count; i++)
            {
                // The DTRS XML is the DLC-Builder-importable artifact — it
                // needs the same real COUNT/p0…pN/END phrase structure the
                // .psarc build generates (RocksmithPackageBuilder); without it
                // the companion XML has zero phraseIterations/sections and
                // re-imports as a structureless arrangement. Generate on a
                // CLONE: the structure is derived output, never project
                // content, and callers may pass a live (non-snapshot) project.
                var stage = timer.ElapsedTicks;
                var arrangement = ChartProjectCloner.CloneArrangement(project.Arrangements[i], retainIdentities: true);
                if (RsxrStringLayout.XmlExportRefusal(arrangement, config.RsXmlSixStringProjection) is { } refusal)
                {
                    _logger.Warning(AppLogCategory.Project, refusal);
                    continue;
                }
                PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, songLengthMs);
                cloneTicks += timer.ElapsedTicks - stage;
                stage = timer.ElapsedTicks;
                arrangement = DlcBuilderBuildSteps.ApplyOrKeep(project, arrangement, audioLengthMs, steps, _logger);
                stepsTicks += timer.ElapsedTicks - stage;
                stage = timer.ElapsedTicks;
                // songLength is EOF's min(chart, audio) - 1 ms: when the audio
                // is SHORTER than the chart it must win, or the exported
                // arrangement never reaches an end in-game. A null here is the
                // no-audio-loaded branch and keeps the chart's own length.
                var written = false;
                WriteOwnedBundleFile(Path.Combine(stagingDirectory, project.ArrangementFiles[i]),
                    target => written = arrangementWriter.WriteTo(CreateArrangementExportProject(project, arrangement), target, audioLengthMs));
                if (!written) continue;
                writeTicks += timer.ElapsedTicks - stage;
                if (arrangementWriter.LastLoss is { IsEmpty: false } loss)
                    _logger.Warning(AppLogCategory.Project, loss.Describe(project.Arrangements[i]));
                arrangementCount++;
            }

            // The DTRS XML bundle is an EXPORT, so it honours the same
            // ExportIncludeVocals flag the psarc does — through the one shared
            // VocalsExportPolicy, or a charter who turned vocals off still gets
            // a bundle full of lyrics and a .rs2dlc naming those files.
            var includeVocals = VocalsExportPolicy.IncludeVocals(TryLoadAppConfig());
            var vocalCount = 0;
            for (var i = 0; i < project.VocalTracks.Count; i++)
            {
                var track = project.VocalTracks[i];
                if (!VocalsExportPolicy.ShouldWrite(track, includeVocals)) continue;

                WriteOwnedBundleFile(Path.Combine(stagingDirectory, BuildVocalTrackFileName(i + 1, track)),
                    target => vocalsWriter.WriteTo(CreateVocalExportProject(project, track), target));
                vocalCount++;
            }

            var showlightsWritten = false;
            if (project.Showlights.Count > 0)
            {
                WriteOwnedBundleFile(Path.Combine(stagingDirectory, ShowlightsFileName),
                    target => showlightsWriter.WriteTo(CreateShowlightsExportProject(project), target));
                showlightsWritten = true;
            }

            OwnedDlcBuilderBundle.Record(stagingDirectory);
            SwapInBundle(stagingDirectory, exportDirectory, _recycle);
            OwnedDlcBuilderBundle.Record(exportDirectory);

            _logger.Info(AppLogCategory.Project,
                $"Saved DLC Builder XML bundle to '{exportDirectory}'. Arrangements={arrangementCount}, vocals={vocalCount}, showlights={showlightsWritten}, " +
                $"totalMs={timer.ElapsedMilliseconds}, cloneMs={Ms(cloneTicks)}, buildStepsMs={Ms(stepsTicks)}, writeXmlMs={Ms(writeTicks)}.");
        }
        catch
        {
            // best-effort cleanup; the real error propagates
            try { OwnedDlcBuilderBundle.Recycle(stagingDirectory, _recycle); }
            catch (IOException) { /* A locked or unrecorded stage is left for the next open. */ }
            throw;
        }
    }

    private static long Ms(long stopwatchTicks) => stopwatchTicks * 1000 / Stopwatch.Frequency;

    private static void WriteOwnedBundleFile(string path, Action<string> write)
    {
        if (File.Exists(path)) throw new IOException("The staged XML destination already exists.");
        try { write(path); }
        finally
        {
            // This attempt created the destination, including a partial serialization.
            // Stamp it even when the writer throws so cleanup never adopts unknown files.
            if (File.Exists(path)) CompanionOwnership.AdoptMovedFile(path);
        }
    }

    /// <summary>
    ///     Replaces <paramref name="exportDirectory" /> with <paramref name="stagingDirectory" />
    ///     by MOVING the old bundle aside first, never by deleting it in place.
    /// </summary>
    /// <remarks>
    ///     <c>Directory.Delete(recursive)</c> is not atomic: one locked file (an
    ///     editor, an indexer, AV) leaves the export folder half-deleted, and the
    ///     caller's cleanup then removes staging too — so BOTH copies of the bundle
    ///     are gone. A rename is atomic, so the worst case here is a leftover
    ///     <c>.old</c> folder (cleaned up on the next save) with the previous bundle
    ///     still intact.
    /// </remarks>
    private static void SwapInBundle(string stagingDirectory, string exportDirectory, Action<string> recycle)
    {
        var previousDirectory = exportDirectory + ".old";
        OwnedDlcBuilderBundle.Recycle(previousDirectory, recycle);

        var movedAside = false;
        if (Directory.Exists(exportDirectory))
        {
            Directory.Move(exportDirectory, previousDirectory);
            OwnedDlcBuilderBundle.Record(previousDirectory);
            movedAside = true;
        }

        try
        {
            Directory.Move(stagingDirectory, exportDirectory);
        }
        catch
        {
            if (movedAside)
            {
                try { Directory.Move(previousDirectory, exportDirectory); }
                catch { /* the previous bundle stays as '.old'; better than nothing */ }
            }

            throw;
        }

        if (!movedAside) return;
        // the new bundle is in place; a stale '.old' is harmless
        try { OwnedDlcBuilderBundle.Recycle(previousDirectory, recycle); }
        catch (IOException) { /* The new bundle is intact; a locked old copy stays. */ }
    }

    public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
        IEnumerable<LyricPhrase> lyrics)
    {
        var project = baseProject ?? new ChartProject();
        project.Bpm = bpm;
        project.AudioFileName = audioFileName;

        var lyricsList = lyrics.ToList();
        if (lyricsList.Count > 0) MergeLyricsIntoActiveVocalTrack(project, lyrics, lyricsList);

        _logger.Trace(AppLogCategory.Project,
            $"Assembled project with bpm={bpm}, audio='{audioFileName}', lyrics={lyricsList.Count}, reusingBase={baseProject != null}.");
        return project;
    }

    /// <summary>
    ///     Writes the edited lyrics back into the ACTIVE vocal track, leaving every
    ///     other track — and the active track's own <see cref="VocalTrack.Name" />
    ///     and RGBA colour — untouched.
    /// </summary>
    /// <remarks>
    ///     This must not be <c>VocalTracks.Clear()</c> followed by adding one
    ///     default-constructed <see cref="VocalTrack" />, which would DELETE tracks
    ///     2..N and reset track 1's name to "Vocals" and its colour to
    ///     255,255,255,255 — losing a harmony track and every imported RS vocals
    ///     track's name/colour. It runs from the save path, the crash snapshot AND
    ///     the auto-backup <c>DispatcherTimer</c>, so the loss would happen with no
    ///     user action at all, and it mutates the LIVE project, so it would not be
    ///     confined to the file.
    ///     <para>
    ///         Every caller passes <c>ChartEditorViewModel.LyricPhrases</c>, which
    ///         IS the active track's <see cref="VocalTrack.Phrases" /> collection by
    ///         reference (<c>OnActiveVocalTrackChanged</c>), so reference identity
    ///         is what identifies the track to update — falling back to the first
    ///         track, and creating one only when there are none.
    ///     </para>
    /// </remarks>
    private static void MergeLyricsIntoActiveVocalTrack(ChartProject project, IEnumerable<LyricPhrase> lyrics,
        List<LyricPhrase> lyricsList)
    {
        var target = project.VocalTracks.FirstOrDefault(t => ReferenceEquals(t.Phrases, lyrics))
                     ?? project.VocalTracks.FirstOrDefault();

        if (target == null)
        {
            target = new VocalTrack();
            project.VocalTracks.Add(target);
        }

        // Already the same collection (the normal case) — replacing it would
        // detach the editor's binding for no gain.
        if (ReferenceEquals(target.Phrases, lyrics)) return;

        target.Phrases = new ObservableCollection<LyricPhrase>(lyricsList);
    }

    public ChartProject CreateNewProject(string title, string artist, string audioFileName)
    {
        var newProject = new ChartProject();
        var defaultArr = new Arrangement { Name = "Lead Guitar" };
        for (var i = 0; i < 6; i++) defaultArr.TuningPitches.Add(new TuningOffset(0));
        // A level must exist so ActiveLevel binds to this project's empty collections
        // rather than falling back to the last loaded project's notes.
        defaultArr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        newProject.Arrangements.Add(defaultArr);
        newProject.Chart = new ChartData { SongName = title, Artist = artist };
        newProject.AudioFileName = audioFileName;

        _logger.Info(AppLogCategory.Project,
            $"Created new project (title='{title}', artist='{artist}', audio='{audioFileName}').");
        return newProject;
    }

    private static long SafeGetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return -1;
        }
    }

    // The four below are DlcBuilderBundle's, not ours: the .rs2dlc companion
    // POINTS AT these files by name, so a second definition here would produce a
    // project file referencing XML that does not exist.
    private static string GetDlcBuilderXmlDirectory(string projectPath) =>
        DlcBuilderBundle.DirectoryFor(projectPath);

    private static string BuildArrangementFileName(int id, Arrangement arrangement) =>
        DlcBuilderBundle.ArrangementFileName(id, arrangement);

    private static string BuildVocalTrackFileName(int id, VocalTrack track) =>
        DlcBuilderBundle.VocalTrackFileName(id, track);

    /// <summary>
    ///     The app configuration, or null when this service was composed without one
    ///     (the headless/test container registers the document service alone).
    ///     <see cref="VocalsExportPolicy.IncludeVocals" /> maps a null onto
    ///     <c>AppConfig</c>'s own default, so a missing container entry never
    ///     silently DROPS vocals from a bundle.
    /// </summary>
    private AppConfig? TryLoadAppConfig()
    {
        try
        {
            return _serviceProvider.GetService<IConfigurationService>()?.LoadAppConfig();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project,
                "Could not read the app configuration for the XML bundle; using defaults.", ex);
            return null;
        }
    }

    private static ChartProject CreateArrangementExportProject(ChartProject source, Arrangement arrangement)
    {
        return new ChartProject
        {
            AudioFileName = source.AudioFileName,
            Bpm = source.Bpm,
            Chart = source.Chart,
            Beats = source.Beats,
            Arrangements = [arrangement]
        };
    }

    private static ChartProject CreateVocalExportProject(ChartProject source, VocalTrack track)
    {
        return new ChartProject
        {
            AudioFileName = source.AudioFileName,
            Bpm = source.Bpm,
            Chart = source.Chart,
            Beats = source.Beats,
            VocalTracks = [track]
        };
    }

    private static ChartProject CreateShowlightsExportProject(ChartProject source)
    {
        return new ChartProject
        {
            AudioFileName = source.AudioFileName,
            Bpm = source.Bpm,
            Chart = source.Chart,
            Beats = source.Beats,
            Showlights = source.Showlights
        };
    }
}
