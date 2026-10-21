using System.Collections.ObjectModel;
using System.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Parsing;
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

    public ProjectDocumentService(IServiceProvider serviceProvider, IAppLogger logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///     The atomic-write temp suffix used by save paths (write to
    ///     <c>project.rscp.saving</c>, then swap in). The serializer is chosen by
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
            ".rscp" => "rscp",
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
    {
        var format = ResolveFormat(filePath);

        _logger.Info(AppLogCategory.Project,
            $"Saving chart '{filePath}' (format={format}, arrangements={project.Arrangements.Count}, beats={project.Beats.Count}).");

        var serializer = _serviceProvider.GetRequiredKeyedService<IChartSerializer>(format);
        // "RS XML for 6-string tools": the file the user saves as .xml is often headed
        // for EOF, which aborts on a 7th string. Off by default: the spec XML keeps all of them.
        if (serializer is XmlChartSerializer xml)
        {
            var config = TryLoadAppConfig();
            xml.SixStringProjection = config?.RsXmlSixStringProjection ?? false;
            xml.MoverPhrases = config?.RsXmlMoverPhrases ?? false;
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            serializer.Save(filePath, project, audioLengthMs);
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
    public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(project);

        // The bundle is Rocksmith XML: play-only content is never exported.
        // Skipped, never thrown — a companion may not fail the save.
        if (PlayOnlyExportGuard.IsPlayOnly(project)) return;

        RefreshArrangementFileManifest(project);

        // Write the whole bundle into a sibling staging directory and swap
        // it in only after every file succeeded. The old delete-then-write
        // sequence left the DTRS XML folder half-populated (stale or
        // missing arrangements) if anything failed mid-bundle.
        var exportDirectory = GetDlcBuilderXmlDirectory(projectPath);
        var stagingDirectory = exportDirectory + ".saving";
        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        Directory.CreateDirectory(stagingDirectory);

        // DLC Builder is the bundle's consumer and indexes six-wide arrays, so a 7-/8-string
        // guitar goes in as its six-string projection. Six-string guitars and basses
        // are written exactly as before.
        var config = TryLoadAppConfig() ?? new AppConfig();
        var arrangementWriter = new RsXmlSongWriter { SixStringProjection = true, MoverPhrases = config.RsXmlMoverPhrases };
        var vocalsWriter = new RsXmlVocalsWriter();
        var showlightsWriter = new RsXmlShowlightsWriter();

        var arrangementCount = 0;
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
                var arrangement = ChartProjectCloner.CloneArrangement(project.Arrangements[i]);
                PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, songLengthMs);
                arrangement = DlcBuilderBuildSteps.ApplyOrKeep(project, arrangement, audioLengthMs, steps, _logger);
                // songLength is EOF's min(chart, audio) - 1 ms: when the audio
                // is SHORTER than the chart it must win, or the exported
                // arrangement never reaches an end in-game. A null here is the
                // no-audio-loaded branch and keeps the chart's own length.
                arrangementWriter.WriteTo(
                    CreateArrangementExportProject(project, arrangement),
                    Path.Combine(stagingDirectory, project.ArrangementFiles[i]),
                    audioLengthMs);
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

                vocalsWriter.WriteTo(
                    CreateVocalExportProject(project, track),
                    Path.Combine(stagingDirectory, BuildVocalTrackFileName(i + 1, track)));
                vocalCount++;
            }

            var showlightsWritten = false;
            if (project.Showlights.Count > 0)
            {
                showlightsWriter.WriteTo(
                    CreateShowlightsExportProject(project),
                    Path.Combine(stagingDirectory, ShowlightsFileName));
                showlightsWritten = true;
            }

            SwapInBundle(stagingDirectory, exportDirectory);

            _logger.Info(AppLogCategory.Project,
                $"Saved DLC Builder XML bundle to '{exportDirectory}'. Arrangements={arrangementCount}, vocals={vocalCount}, showlights={showlightsWritten}.");
        }
        catch
        {
            // best-effort cleanup; the real error propagates
            BestEffortDelete.Directory(stagingDirectory);
            throw;
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
    private static void SwapInBundle(string stagingDirectory, string exportDirectory)
    {
        var previousDirectory = exportDirectory + ".old";
        if (Directory.Exists(previousDirectory)) Directory.Delete(previousDirectory, true);

        var movedAside = false;
        if (Directory.Exists(exportDirectory))
        {
            Directory.Move(exportDirectory, previousDirectory);
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
        BestEffortDelete.Directory(previousDirectory);
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
                $"Could not read the app configuration for the XML bundle; using defaults. {ex.Message}");
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
