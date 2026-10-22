using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     The session side of the <c>.dcproj</c> project file: a save writes a <c>.dcproj</c> and
///     its Charter companion; a session opened from an older DotCharter <c>.rscp</c> moves to the
///     <c>.dcproj</c> on its first save and keeps the old file under its backup name; a plain
///     Charter <c>.rscp</c> opens as before and is never written to; and when a folder holds both,
///     the <c>.dcproj</c> is what opens.
/// </summary>
public sealed class ProjectFileExtensionSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-dcproj-session-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string SongFolder()
    {
        var folder = Path.Combine(_dir, "Band - Song");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static ChartProject Project(string title = "Song")
    {
        var project = new ChartProject { Chart = { SongName = title, Artist = "Band" } };
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        var arrangement = new Arrangement { Name = "Lead" };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    [Fact]
    public async Task ASave_WritesTheDcprojAndItsCharterCompanion()
    {
        var path = ProjectFiles.DefaultPath(SongFolder());
        var h = new Harness(Project(), path);

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(path));

        Assert.True(CharterRscp.IsWrittenByDotCharter(path));
        var companion = CharterRscp.CompanionPath(path);
        Assert.True(File.Exists(companion));
        Assert.DoesNotContain("dcWriterVersion", File.ReadAllText(companion), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenItsDcprojAlreadyExists_TheUserPicksTheName_AndATypedRscpBecomesDcproj()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(legacy, Project());
        var existing = Path.Combine(folder, "project.dcproj");
        File.WriteAllText(existing, "someone else's project");

        var h = new Harness(Project(), legacy);
        h.Storage.NextSaveFile = Path.Combine(folder, "typed-name.rscp");
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(legacy));

        Assert.Equal(Path.Combine(folder, "typed-name.dcproj"), h.State.CurrentProjectPath);
        Assert.Equal("someone else's project", File.ReadAllText(existing));
    }

    [Fact]
    public async Task AnOlderDotCharterRscp_SavesAsDcproj_AndIsKeptAsABackup()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(legacy, Project());
        var legacyBytes = File.ReadAllBytes(legacy);
        Assert.True(CharterRscp.IsWrittenByDotCharter(legacy));

        var h = new Harness(Project(), legacy);
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(legacy));

        var dcproj = Path.Combine(folder, "project.dcproj");
        Assert.Equal(dcproj, h.State.CurrentProjectPath);
        Assert.True(File.Exists(dcproj));
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacy + ".bak"));
        // The .rscp name now holds the Charter companion, not DotCharter's XML.
        Assert.False(CharterRscp.IsWrittenByDotCharter(legacy));
        Assert.Contains("project.rscp.bak", h.State.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondMigration_NeverOverwritesAnEarlierBackup()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        File.WriteAllText(legacy + ".bak", "earlier backup");
        new RscpChartSerializer().Save(legacy, Project());

        var h = new Harness(Project(), legacy);
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(legacy));

        Assert.Equal("earlier backup", File.ReadAllText(legacy + ".bak"));
        Assert.True(File.Exists(Path.Combine(folder, "project.rscp.2.bak")));
    }

    [Fact]
    public async Task APlainCharterRscp_OpensAsBefore_AndIsNeverWritten()
    {
        var folder = SongFolder();
        var charter = Path.Combine(folder, "project.rscp");
        File.WriteAllText(charter, CharterXml);
        var charterBytes = File.ReadAllBytes(charter);
        Assert.False(CharterRscp.IsWrittenByDotCharter(charter));

        var h = new Harness(new ChartProject(), "");
        await h.Session.LoadFile(charter);
        Assert.Equal(charter, h.State.CurrentProjectPath);
        Assert.Equal("Charter Song", h.State.ActiveProject?.Chart.SongName);

        Assert.True(await h.Session.Saver.SaveFromRscpAsync(charter));

        Assert.Equal(Path.Combine(folder, "project.dcproj"), h.State.CurrentProjectPath);
        Assert.Equal(charterBytes, File.ReadAllBytes(charter));
        Assert.False(File.Exists(charter + ".bak"));
    }

    [Fact]
    public async Task AMigratedProject_KeepsItsPlayModeStatsAndBestScores()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(legacy, Project());
        var stats = new StatsRig(_dir);
        stats.Practice.Record(legacy, "Lead:Lead", new LivePlaySessionResult(120_000, 0.95, 95, 5, 100, 60, 200, 100, 1.0), "Band - Song");
        stats.Practice.Record(legacy, "Lead:Lead", new LivePlaySessionResult(90_000, 0.80, 80, 20, 100, 30, 200, 100, 1.0));

        var h = new Harness(Project(), legacy, stats.Rekey);
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(legacy));

        var dcproj = Path.Combine(folder, "project.dcproj");
        Assert.Equal(dcproj, h.State.CurrentProjectPath);
        var kept = stats.Practice.GetProfile(dcproj, "Lead:Lead");
        Assert.Equal(120_000, kept.BestScore);
        Assert.Equal(0.95, kept.BestAccuracy, 3);
        Assert.Equal(2, kept.Sessions.Count);
        Assert.Equal(0, stats.Practice.GetProfile(legacy, "Lead:Lead").BestScore);
        // The same figures Play Mode's song list and Get Ready read.
        Assert.Equal(120_000, new DotCharter.Services.Gameplay.Stats.GameplayStatsService(stats.Practice).GetBestForSong(dcproj)?.BestScore);

        // The migration already ran: asking again moves nothing and keeps every figure.
        Assert.Equal(0, stats.Rekey.Rekey(legacy, dcproj).Total);
        Assert.Equal(120_000, stats.Practice.GetProfile(dcproj, "Lead:Lead").BestScore);
        Assert.Equal(2, stats.Practice.GetProfile(dcproj, "Lead:Lead").Sessions.Count);
    }

    [Fact]
    public async Task APlainCharterRscp_ThatWasPlayedInDotCharter_KeepsItsStatsToo()
    {
        var folder = SongFolder();
        var charter = Path.Combine(folder, "project.rscp");
        File.WriteAllText(charter, CharterXml);
        var stats = new StatsRig(_dir);
        stats.Practice.Record(charter, "Lead:Lead", new LivePlaySessionResult(70_000, 0.9, 90, 10, 100, 40, 200, 100, 1.0));

        var h = new Harness(new ChartProject(), "", stats.Rekey);
        await h.Session.LoadFile(charter);
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(charter));

        Assert.Equal(70_000, stats.Practice.GetProfile(Path.Combine(folder, "project.dcproj"), "Lead:Lead").BestScore);
    }

    [Fact]
    public async Task ASaveAsUnderAnotherName_IsAnotherProject_AndStartsFresh()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(legacy, Project());
        File.WriteAllText(Path.Combine(folder, "project.dcproj"), "someone else's project");
        var stats = new StatsRig(_dir);
        stats.Practice.Record(legacy, "Lead:Lead", new LivePlaySessionResult(120_000, 0.95, 95, 5, 100, 60, 200, 100, 1.0));

        var h = new Harness(Project(), legacy, stats.Rekey);
        h.Storage.NextSaveFile = Path.Combine(folder, "typed-name.dcproj");
        Assert.True(await h.Session.Saver.SaveFromRscpAsync(legacy));

        Assert.Equal(0, stats.Practice.GetProfile(Path.Combine(folder, "typed-name.dcproj"), "Lead:Lead").BestScore);
        Assert.Equal(120_000, stats.Practice.GetProfile(legacy, "Lead:Lead").BestScore);
    }

    [Fact]
    public async Task AFailedMigrationSave_MovesNoStats()
    {
        var folder = SongFolder();
        var legacy = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(legacy, Project());
        var stats = new StatsRig(_dir);
        stats.Practice.Record(legacy, "Lead:Lead", new LivePlaySessionResult(120_000, 0.95, 95, 5, 100, 60, 200, 100, 1.0));
        // A directory where the .dcproj has to go: the write cannot succeed.
        Directory.CreateDirectory(Path.Combine(folder, "project.dcproj.saving"));
        Directory.CreateDirectory(Path.Combine(folder, "project.dcproj"));

        var h = new Harness(Project(), legacy, stats.Rekey);
        h.Storage.NextSaveFile = null;
        var saved = await h.Session.Saver.SaveFromRscpAsync(legacy);

        Assert.False(saved);
        Assert.Equal(120_000, stats.Practice.GetProfile(legacy, "Lead:Lead").BestScore);
    }

    /// <summary>The real stores, over a temp data root, wired the way the app wires them.</summary>
    private sealed class StatsRig
    {
        public StatsRig(string root)
        {
            var profiles = new PlayerProfileService(FakeLocalization.English(), null,
                Path.Combine(root, "player-profiles.json"), "Tester");
            Practice = new PracticeProfileService(profiles, null, Path.Combine(root, "legacy-practice.json"));
            Rekey = new ProjectPathRekey(Practice, new PlaySetlistStore(profiles), profiles,
                new FakeConfigurationService(), new FakeAppLogger());
        }

        public PracticeProfileService Practice { get; }

        public ProjectPathRekey Rekey { get; }
    }

    [Fact]
    public async Task WhenAFolderHoldsBoth_TheDcprojOpens_WhicheverWasAskedFor()
    {
        var folder = SongFolder();
        var dcproj = Path.Combine(folder, "project.dcproj");
        var companion = Path.Combine(folder, "project.rscp");
        new RscpChartSerializer().Save(dcproj, Project("From dcproj"));
        File.WriteAllText(companion, CharterXml);

        var h = new Harness(new ChartProject(), "");
        await h.Session.LoadFile(companion);

        Assert.Equal(dcproj, h.State.CurrentProjectPath);
        Assert.Equal("From dcproj", h.State.ActiveProject?.Chart.SongName);
        Assert.Equal(dcproj, h.Config.AppConfig.LastProjectPath);
    }

    [Fact]
    public void TheRecentList_ShowsTheDcproj_NotItsCompanion()
    {
        var entries = new List<RecentProjectEntry>
        {
            new() { Path = @"C:\Songs\A\project.rscp" },
            new() { Path = @"C:\Songs\A\project.dcproj" },
            new() { Path = @"C:\Songs\B\charter.rscp" }
        };
        var onDisk = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

        var shown = RecentProjectsList.Prune(entries, onDisk.Contains).Select(e => e.Path).ToList();

        Assert.Equal([@"C:\Songs\A\project.dcproj", @"C:\Songs\B\charter.rscp"], shown);
    }

    private const string CharterXml =
        "<?xml version='1.0' encoding='UTF-8'?>\n<project>\n  <chartFormatVersion>3</chartFormatVersion>\n" +
        "  <artistName>Charter Band</artistName>\n  <title>Charter Song</title>\n" +
        "  <beats>\n    <beat2 t=\"0.0\" sig=\"4/4\" bar=\"T\"/>\n    <beat2 t=\"500.0\" sig=\"4/4\"/>\n  </beats>\n" +
        "  <arrangements/>\n</project>";

    /// <summary>Reads and writes real project files, and skips the export bundle.</summary>
    private sealed class FileDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => new RscpChartSerializer().Load(filePath);

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
            => new RscpChartSerializer().Save(filePath, project);

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class NoOpCrashRecoveryService : ICrashRecoveryService
    {
        public string SnapshotRootDirectory => string.Empty;
        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    /// <summary>Writes the Charter companion for real; every other companion is off in the fake config.</summary>
    private sealed class CharterOnlyCompanions(SaveCompanionService real) : ISaveCompanionService
    {
        public string? WriteCharterProject(string projectPath, ChartProject snapshot) => real.WriteCharterProject(projectPath, snapshot);

        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot, double? audioLengthMs,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Harness
    {
        public Harness(ChartProject project, string projectPath, IProjectPathRekey? pathRekey = null)
        {
            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            Editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Editor.ApplyProject(project, resetHistory: true);
            State.CurrentProjectPath = projectPath;

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), Config, logger, Storage,
                new FileDocumentService(), State, transport, Editor, new TempoService(logger),
                new NoOpCrashRecoveryService(), history,
                new CharterOnlyCompanions(new SaveCompanionService(Config, logger)),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: pathRekey ?? new FakeProjectPathRekey());
        }

        public ChartEditorViewModel Editor { get; }
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public ProjectSessionService Session { get; }
    }
}
