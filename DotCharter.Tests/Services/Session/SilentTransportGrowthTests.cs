using System.Globalization;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     A chart-only project runs on a GENERATED silent WAV so the transport,
///     scrubbing, count-in and Play Mode still work. Its length is derived from
///     the project's content — once, at load.
///     <para>
///         That is the bug: a chart-only project is exactly the kind that gets
///         EXTENDED afterwards (paste a section past the end, generate more
///         beats, import a longer arrangement into the open session). Past the
///         silent file's end there was no transport at all — the playhead
///         stopped there, every seek clamped to it, and Play Mode could not
///         reach notes the editor was drawing.
///     </para>
///     <para>
///         The growth check hangs off the beat grid, which is the timeline
///         length, and the bucket grid (<c>SilentAudioTrack.BucketMs</c>) is
///         what keeps it cheap: the planned length only moves in 15 s steps, so
///         editing inside the current bucket costs one comparison and the WAV is
///         reused from the cache whenever a bucket is revisited.
///     </para>
/// </summary>
public sealed class SilentTransportGrowthTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-silentgrowth-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- the pure decision ----------------------------------------------------

    /// <summary>
    ///     The rule, on the bucket grid. A 30 s track covers everything that
    ///     plans to 30 s; the first content end that plans past it needs a longer
    ///     one. 0 means "not on a silent transport", which must never answer yes
    ///     — that is real audio, or nothing at all.
    /// </summary>
    [Theory]
    [InlineData(30_000, 5_000, false)] // plans to 15 s: well inside
    [InlineData(30_000, 27_000, false)] // plans to 30 s exactly: still covered
    [InlineData(30_000, 28_001, true)] // +tail crosses the bucket
    [InlineData(30_000, 120_000, true)]
    [InlineData(0, 600_000, false)] // not on a silent transport at all
    public void NeedsLongerTrack_AnswersOnTheBucketGrid(double loaded, double contentEnd, bool expected)
        => Assert.Equal(expected, SilentAudioTrack.NeedsLongerTrack(loaded, contentEnd));

    // ---- the session ----------------------------------------------------------

    /// <summary>
    ///     THE regression: extend the chart past the silent track and the session
    ///     regenerates a longer one, so the timeline still reaches the notes.
    /// </summary>
    [Fact]
    public async Task ExtendingTheChartPastTheSilentTrack_RegeneratesALongerOne()
    {
        var h = new Harness();
        await h.Session.LoadFile(h.NewProjectPath("Chart Only"));

        // Loaded on a 30 s silent track: 20 s of beats + 2 s tail, up to the
        // 15 s bucket grid.
        Assert.Equal(["silence-30s.wav"], h.LoadedFileNames);
        Assert.Equal(30_000, h.Audio.TotalAudioLengthMs);

        // Now the chart grows to two minutes — the same gesture as pasting a
        // section past the end.
        h.ExtendBeatsTo(120_000);

        // The track that ends up in the engine covers the whole chart: 120 s of
        // beats + the 2 s runout, rounded up to the 15 s bucket grid.
        Assert.Equal("silence-135s.wav", h.LoadedFileNames[^1]);
        Assert.Equal(135_000, h.Audio.TotalAudioLengthMs);
        Assert.Equal(135_000, h.State.EffectiveTimelineLengthMs);

        // …and it grew one bucket at a time, because the beats arrive one at a
        // time. Every file is a real cached WAV, so the count is bounded by the
        // buckets CROSSED (7 here) and never by the number of beats added (200).
        Assert.Equal(8, h.LoadedFileNames.Count);
        Assert.All(h.LoadedFileNames, n => Assert.StartsWith("silence-", n, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The control that keeps the check from being "regenerate on every
    ///     edit": beats added INSIDE the current bucket change nothing, and the
    ///     regeneration is a whole WAV write plus an engine load.
    /// </summary>
    [Fact]
    public async Task EditingInsideTheCurrentBucket_RegeneratesNothing()
    {
        var h = new Harness();
        await h.Session.LoadFile(h.NewProjectPath("Inside The Bucket"));
        Assert.Equal(["silence-30s.wav"], h.LoadedFileNames);

        // 24 s of chart still plans to 30 s (24 + 2 tail, rounded up to 30).
        h.ExtendBeatsTo(24_000);

        Assert.Equal(["silence-30s.wav"], h.LoadedFileNames);
    }

    /// <summary>
    ///     …and the growth never runs for a project on REAL audio: the engine's
    ///     length is the song's, and rewriting it into generated silence would
    ///     be a spectacular way to lose the user's audio mid-session.
    /// </summary>
    [Fact]
    public async Task AProjectOnRealAudio_IsNeverGivenASilentTrack()
    {
        var h = new Harness(audioFileName: "song.ogg");
        // The declared audio really is on disk for this one, so the ordinary
        // decode path runs and the silent fallback does not.
        await h.Session.LoadFile(h.NewProjectPathWithAudio("With Audio", "song.ogg"));
        var afterLoad = h.LoadedFileNames.ToList();
        Assert.DoesNotContain(afterLoad, n => n.StartsWith("silence-", StringComparison.Ordinal));

        h.ExtendBeatsTo(600_000);

        Assert.Equal(afterLoad, h.LoadedFileNames);
    }

    // ---- harness --------------------------------------------------------------

    /// <summary>Reports the length the file NAME encodes, which is what the real
    /// engine reports after decoding it — the growth check is about the
    /// timeline, so a fake that always said 0 would make every case vacuous.</summary>
    private sealed class LengthFromNameAudioService : FakeAudioPlaybackService
    {
        public override double TotalAudioLengthMs
        {
            get
            {
                var name = LoadedPaths.Count == 0 ? "" : Path.GetFileNameWithoutExtension(LoadedPaths[^1]);
                if (!name.StartsWith("silence-", StringComparison.Ordinal)) return 1_000;
                return int.Parse(name["silence-".Length..^1], CultureInfo.InvariantCulture) * 1000.0;
            }
            set => _ = value;
        }
    }

    private sealed class StubDocumentService(ChartProject toLoad) : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => toLoad;
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

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

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot,
            double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Harness
    {
        private readonly string _root;

        public Harness(string audioFileName = "")
        {
            _root = Directory.CreateTempSubdirectory("dotcharter-silentgrowth-h-").FullName;

            var logger = new FakeAppLogger();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            // 20 s of chart, so the first silent track is a 30 s one.
            Project = new ChartProject { AudioFileName = audioFileName };
            Project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
            Project.Chart = new ChartData { SongName = "Chart Only" };
            for (var ms = 0.0; ms <= 20_000; ms += 500)
                Project.Beats.Add(new BeatMarker { Time = ms });

            Session = new ProjectSessionService(
                Audio, config, logger, new FakeFileStorageService(),
                new StubDocumentService(Project), State, transport, editor,
                new TempoService(logger), new NoOpCrashRecoveryService(), history,
                new NoOpSaveCompanionService(),
                silentAudio: new SilentAudioTrack(logger, Path.Combine(_root, "silence")),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English());
        }

        public LengthFromNameAudioService Audio { get; } = new();
        public ProjectState State { get; } = new();
        public ChartProject Project { get; }
        public ProjectSessionService Session { get; }

        public IReadOnlyList<string> LoadedFileNames =>
            Audio.LoadedPaths.Select(Path.GetFileName).ToList()!;

        public string NewProjectPath(string folderName)
        {
            var dir = Path.Combine(_root, folderName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "project.rscp");
            File.WriteAllText(path, "");
            return path;
        }

        public string NewProjectPathWithAudio(string folderName, string audioName)
        {
            var path = NewProjectPath(folderName);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, audioName), new byte[1024]);
            return path;
        }

        /// <summary>Extends the beat grid — the timeline length — out to
        /// <paramref name="endMs" />, exactly as a paste past the end or a tempo
        /// regeneration does.</summary>
        public void ExtendBeatsTo(double endMs)
        {
            var last = State.Beats.Count == 0 ? 0 : State.Beats[^1].Time;
            for (var ms = last + 500; ms <= endMs; ms += 500)
                State.Beats.Add(new BeatMarker { Time = ms });
        }
    }
}
