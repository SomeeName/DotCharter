using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure.Documents;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Online;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed class NewProjectViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-newprojectvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- harness --------------------------------------------------------------

    private sealed class FakeProjectDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        // Mirrors the real service: one stub arrangement with one level.
        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
        {
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead Guitar" };
            for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            project.Arrangements.Add(arr);
            project.Chart = new ChartData { SongName = title, Artist = artist };
            project.AudioFileName = audioFileName;
            return project;
        }
    }

    private sealed class FakeProjectSessionService : IProjectSessionService
    {
        public int FreeAudioCalls { get; private set; }
        public List<string> LoadedAudioFiles { get; } = [];

        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() => FreeAudioCalls++;
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
        {
            LoadedAudioFiles.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);

        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;

        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;

        /// <summary>No shell is listening, so a replace is always allowed.</summary>
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);

        public int CleanMarks { get; private set; }
        public int DirtyMarks { get; private set; }
        public void MarkProjectClean() => CleanMarks++;
        public void MarkProjectDirty() => DirtyMarks++;
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Harness
    {
        public Harness(
            Func<string, AudioMetadataResult>? extractor = null,
            PrepareImportAudio? preparer = null)
        {
            var logger = new FakeAppLogger();
            Transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            Editor = new ChartEditorViewModel(
                State, Transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Vm = new NewProjectViewModel(Storage, Config, new FakeSources(extractor), State,
                FakeLocalization.English(), logger, new TestUiDispatcher());
            // Default to a deterministic verbatim copy so the audio-path tests never
            // spawn the real ffmpeg. Tests that exercise baking inject their own fake.
            Creator = new NewProjectCreator(Editor, Transport, State, Session, Storage, Config, Documents,
                new TempoService(logger), new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger, preparer ?? DefaultVerbatimPreparer,
                FakeLocalization.English());
        }

        public NewProjectCreator Creator { get; }

        /// <summary>The dialog's Choose audio, awaited.</summary>
        public Task ChooseAudioAsync() => ((IAsyncRelayCommand)Vm.ChooseAudioCommand).ExecuteAsync(null);

        /// <summary>Create as the launcher runs it: the dialog answers, then the creator builds on that
        ///     answer. A refused Create leaves the dialog open and creates nothing.</summary>
        public async Task CreateAsync()
        {
            Vm.CreateCommand.Execute(null);
            if (Vm.Result.IsCompleted && await Vm.Result is { } details) await Creator.CreateAsync(details);
        }

        private sealed class FakeSources(Func<string, AudioMetadataResult>? extractor) : INewProjectSources
        {
            public AudioMetadataResult ReadTags(string audioFilePath) =>
                extractor?.Invoke(audioFilePath) ?? AudioMetadataResult.Empty;

            public Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(string artist, string title,
                string album, Action<LatinNameSuggestion> onArtist) =>
                Task.FromResult(OnlineResult<LatinNameSuggestion>.Fail(OnlineError.NotConfigured, "offline in tests"));
        }

        // The verbatim-copy fallback the preparer produces when no silence is
        // baked: copies the source to song.<ext> and reports SilenceBaked=false.
        private static ImportAudioResult DefaultVerbatimPreparer(
            string src, string dir, double lead, double trail)
        {
            var name = "song" + Path.GetExtension(src);
            File.Copy(src, Path.Combine(dir, name), overwrite: true);
            return new ImportAudioResult(name, OriginalFileName: null, SilenceBaked: false);
        }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public FakeProjectDocumentService Documents { get; } = new();
        public FakeProjectSessionService Session { get; } = new();
        public TransportViewModel Transport { get; }
        public ChartEditorViewModel Editor { get; }
        public NewProjectViewModel Vm { get; }
    }

    private static string AudioPickerTitle => FakeLocalization.English()["Picker_SelectAudioFile"];
    private static string FolderPickerTitle => FakeLocalization.English()["Picker_SelectProjectFolder"];

    // A tiny valid-ish source audio file so File.Copy has something to copy.
    private string CreateSourceAudio(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, [0x49, 0x44, 0x33, 1, 2, 3, 4]);
        return path;
    }

    // ---- no-audio path --------------------------------------------------------

    [Fact]
    public async Task StartWithoutAudio_ThenCreate_BuildsEmptyProjectWithDefaultBeatGrid()
    {
        var h = new Harness();
        h.Storage.NextFolder = _tempDir;

        // Stage 2 (no-audio): the form appears, cleared, ready for plain entry.
        h.Vm.StartWithoutAudioCommand.Execute(null);
        Assert.True(h.Vm.IsFormStage);
        h.Vm.Artist = "Some Artist";
        h.Vm.Title = "Some Song";

        await h.CreateAsync();

        // Only the folder picker ran — no audio picker in the no-audio path.
        Assert.Equal(new[] { FolderPickerTitle }, h.Storage.RequestedTitles);

        var projectDir = Path.Combine(_tempDir, "Some Artist - Some Song");
        Assert.True(Directory.Exists(projectDir));
        Assert.Equal(Path.Combine(projectDir, "project.dcproj"), h.State.CurrentProjectPath);

        // No audio: nothing loaded, previous audio freed, no audio file name.
        Assert.Equal("", h.State.CurrentAudioFileName);
        Assert.Empty(h.Session.LoadedAudioFiles);
        Assert.Equal(1, h.Session.FreeAudioCalls);
        Assert.Equal(0, h.State.TotalAudioLengthMs);

        // Default arrangement plus a generated beat grid at the transport BPM.
        Assert.Single(h.Editor.Arrangements);
        Assert.True(h.Editor.Beats.Count > 1);
        var expectedSpacingMs = 60000.0 / h.Transport.Bpm;
        Assert.Equal(expectedSpacingMs, h.Editor.Beats[1].Time - h.Editor.Beats[0].Time, 3);
        Assert.True(h.Editor.Beats[0].IsMeasure);
    }

    /// <summary>
    ///     A new project is DIRTY, and it has to say so out loud.
    ///     <para>
    ///         <c>CurrentProjectPath</c> is where the project WILL be written —
    ///         nothing has written it. Until v0.42.2 this read dirty by accident:
    ///         <c>ApplyProject(resetHistory: true)</c> mints a new history root id
    ///         and nobody re-baselined <c>ProjectDirtyTracker</c>, so every replace
    ///         path was stuck dirty. Fixing that for the LOAD paths (a library song
    ///         genuinely has no unsaved changes) would have silently taken the
    ///         close prompt away from this one too, and a new project with a beat
    ///         grid and copied audio is real work to lose.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Create_MarksTheSessionDirty_BecauseNothingHasWrittenTheProjectYet()
    {
        var h = new Harness();
        h.Storage.NextFolder = _tempDir;
        h.Vm.StartWithoutAudioCommand.Execute(null);
        h.Vm.Artist = "Some Artist";
        h.Vm.Title = "Some Song";

        await h.CreateAsync();

        Assert.Equal(1, h.Session.DirtyMarks);
        Assert.Equal(0, h.Session.CleanMarks);
    }

    /// <summary>
    ///     The folder name is DERIVED from artist + title, so the same song
    ///     always lands on the same one — and <c>Directory.CreateDirectory</c> is
    ///     a no-op on an existing folder. Creating over a folder that already
    ///     holds a project therefore pointed the session at its
    ///     <c>project.dcproj</c> (truncated by the first save) and, on the
    ///     audio-backed path, overwrote its working audio outright.
    /// </summary>
    [Fact]
    public async Task Create_WhenTheDerivedFolderAlreadyHoldsAProject_LeavesItAlone()
    {
        var h = new Harness();
        h.Storage.NextFolder = _tempDir;

        var existing = Path.Combine(_tempDir, "Some Artist - Some Song");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "project.dcproj"), "<project>the earlier one</project>");

        h.Vm.StartWithoutAudioCommand.Execute(null);
        h.Vm.Artist = "Some Artist";
        h.Vm.Title = "Some Song";

        await h.CreateAsync();

        Assert.Equal(Path.Combine(_tempDir, "Some Artist - Some Song (2)", "project.dcproj"),
            h.State.CurrentProjectPath);
        Assert.Equal("<project>the earlier one</project>",
            File.ReadAllText(Path.Combine(existing, "project.dcproj")));
    }

    [Fact]
    public async Task Create_WithoutArtistOrTitle_IsRejected()
    {
        var h = new Harness();
        h.Storage.NextFolder = _tempDir;

        h.Vm.StartWithoutAudioCommand.Execute(null);
        h.Vm.Artist = "";
        h.Vm.Title = "Something";

        await h.CreateAsync();

        Assert.Empty(h.Storage.RequestedTitles); // no picker reached
        Assert.Equal("", h.State.CurrentProjectPath);
        Assert.Equal(0, h.Session.FreeAudioCalls);
    }

    [Fact]
    public async Task Create_WithoutAudio_CancelingFolderPickerAbortsCreation()
    {
        var h = new Harness();
        h.Storage.NextFolder = null; // user cancels the folder picker

        h.Vm.StartWithoutAudioCommand.Execute(null);
        h.Vm.Artist = "A";
        h.Vm.Title = "B";

        await h.CreateAsync();

        Assert.Equal(new[] { FolderPickerTitle }, h.Storage.RequestedTitles);
        Assert.Equal("", h.State.CurrentProjectPath);
        Assert.Equal(0, h.Session.FreeAudioCalls);
    }

    // ---- audio-first path -----------------------------------------------------

    [Fact]
    public async Task ChooseAudio_CancelingPicker_StaysOnChooser()
    {
        var h = new Harness();
        h.Storage.NextOpenFile = null; // user cancels the audio picker

        await h.ChooseAudioAsync();

        Assert.Equal(new[] { AudioPickerTitle }, h.Storage.RequestedTitles);
        Assert.True(h.Vm.IsChooserStage);
        Assert.False(h.Vm.IsFormStage);
    }

    [Fact]
    public async Task ChooseAudio_ExtractsMetadata_PrefillsEditableFields()
    {
        var meta = new AudioMetadataResult(
            "Tag Artist", "Tag Title", "Tag Album", "2019", "2019-05-01", "Metal", null);
        var h = new Harness(_ => meta);
        h.Storage.NextOpenFile = CreateSourceAudio("source.mp3");

        await h.ChooseAudioAsync();

        Assert.True(h.Vm.IsFormStage);
        Assert.Equal("Tag Artist", h.Vm.Artist);
        Assert.Equal("Tag Title", h.Vm.Title);
        Assert.Equal("Tag Album", h.Vm.Album);
        Assert.Equal("2019", h.Vm.AlbumYear);
        Assert.Equal("Metal", h.Vm.Genre);
    }

    [Fact]
    public async Task ChooseAudio_EmptyTags_FallsBackToFilenameHeuristic()
    {
        var h = new Harness(_ => AudioMetadataResult.Empty);
        h.Storage.NextOpenFile = CreateSourceAudio("Some Band - Cool Song.mp3");

        await h.ChooseAudioAsync();

        Assert.True(h.Vm.IsFormStage);
        Assert.Equal("Some Band", h.Vm.Artist);
        Assert.Equal("Cool Song", h.Vm.Title);
    }

    [Fact]
    public async Task Create_WithAudio_AppliesMetadataAndWritesCoverArt()
    {
        var cover = new byte[] { 10, 20, 30, 40, 50 };
        var meta = new AudioMetadataResult(
            "Tag Artist", "Tag Title", "Tag Album", "2019", "2019-05-01", "Metal", cover);
        var h = new Harness(_ => meta);
        h.Storage.NextOpenFile = CreateSourceAudio("source.mp3");
        h.Storage.NextFolder = _tempDir;

        // Audio first…
        await h.ChooseAudioAsync();
        Assert.True(h.Vm.IsFormStage);

        // …then folder + create.
        await h.CreateAsync();

        // Ordering: audio picker, THEN folder picker.
        Assert.Equal(new[] { AudioPickerTitle, FolderPickerTitle }, h.Storage.RequestedTitles);

        var projectDir = Path.Combine(_tempDir, "Tag Artist - Tag Title");
        Assert.True(Directory.Exists(projectDir));
        Assert.Equal(Path.Combine(projectDir, "project.dcproj"), h.State.CurrentProjectPath);

        // Audio copied as song.<ext> and loaded through the session.
        Assert.Equal("song.mp3", h.State.CurrentAudioFileName);
        Assert.True(File.Exists(Path.Combine(projectDir, "song.mp3")));
        Assert.Contains(Path.Combine(projectDir, "song.mp3"), h.Session.LoadedAudioFiles);

        // All six metadata fields land on the chart.
        var chart = h.State.ActiveProject!.Chart;
        Assert.Equal("Tag Title", chart.SongName);
        Assert.Equal("Tag Artist", chart.Artist);
        Assert.Equal("Tag Album", chart.AlbumName);
        Assert.Equal("2019", chart.AlbumYear);
        Assert.Equal("2019-05-01", chart.ReleaseDate);
        Assert.Equal("Metal", chart.Genre);

        // Cover art written into the folder and referenced by the chart.
        var coverPath = Path.Combine(projectDir, "cover.png");
        Assert.True(File.Exists(coverPath));
        Assert.Equal(cover, File.ReadAllBytes(coverPath));
        Assert.Equal("cover.png", chart.CoverArtPath);
    }

    [Fact]
    public async Task Create_WithAudio_FailedExtraction_StillAdvancesUsingFilename()
    {
        var h = new Harness(_ => throw new InvalidOperationException("ffprobe blew up"));
        h.Storage.NextOpenFile = CreateSourceAudio("Broken Band - Broken Song.flac");
        h.Storage.NextFolder = _tempDir;

        await h.ChooseAudioAsync();

        // A throwing extractor degrades to filename-derived fields, still on the form.
        Assert.True(h.Vm.IsFormStage);
        Assert.Equal("Broken Band", h.Vm.Artist);
        Assert.Equal("Broken Song", h.Vm.Title);

        await h.CreateAsync();

        var projectDir = Path.Combine(_tempDir, "Broken Band - Broken Song");
        Assert.True(Directory.Exists(projectDir));
        Assert.Equal("song.flac", h.State.CurrentAudioFileName);
        Assert.False(File.Exists(Path.Combine(projectDir, "cover.png")));
        Assert.Equal("", h.State.ActiveProject!.Chart.CoverArtPath);
    }

    // ---- silence baking / timeline offset -------------------------------------

    [Fact]
    public async Task Create_WithAudio_SilenceBaked_UsesSongOggAndKeepsBeatGridOffset()
    {
        double? capturedLead = null, capturedTrail = null;
        // Fake a successful bake: song.ogg working audio + preserved original.
        var h = new Harness(
            extractor: _ => AudioMetadataResult.Empty,
            preparer: (src, _, lead, trail) =>
            {
                capturedLead = lead;
                capturedTrail = trail;
                return new ImportAudioResult("song.ogg", "original" + Path.GetExtension(src), SilenceBaked: true);
            });
        h.Storage.NextOpenFile = CreateSourceAudio("Band - Song.mp3");
        h.Storage.NextFolder = _tempDir;

        // Defaults: pad on, 10 s lead + 2 s trail.
        h.Config.AppConfig.PadSilenceOnNewProject = true;
        h.Config.AppConfig.LeadingSilenceSeconds = 10.0;
        h.Config.AppConfig.TrailingSilenceSeconds = 2.0;

        await h.ChooseAudioAsync();
        // A padded song is longer than the audio; give the beat generator a length.
        h.State.TotalAudioLengthMs = 60_000;

        await h.CreateAsync();

        // The configured lead/trail reached the preparer as SECONDS.
        Assert.Equal(10.0, capturedLead);
        Assert.Equal(2.0, capturedTrail);

        // Baked ⇒ the working audio is song.ogg (loaded through the session).
        Assert.Equal("song.ogg", h.State.CurrentAudioFileName);
        var projectDir = Path.Combine(_tempDir, "Band - Song");
        Assert.Contains(Path.Combine(projectDir, "song.ogg"), h.Session.LoadedAudioFiles);

        // Beat 0 sits at the leading boundary (10 s) — the offset is NOT double
        // counted; GenerateInitialBeats still received exactly leadingMs.
        Assert.True(h.Editor.Beats.Count > 1);
        Assert.Equal(10_000.0, h.Editor.Beats[0].Time, 3);

        // The baked leading amount is recorded on the chart (EOF convention).
        Assert.Equal(10_000.0, h.State.ActiveProject!.Chart.MainAudioOffsetAdded, 3);
    }

    [Fact]
    public async Task Create_WithAudio_BakeFallback_CopiesVerbatimAndStillShiftsGrid()
    {
        // Fake the fallback: no ffmpeg / encode failed ⇒ verbatim song.<ext>.
        var h = new Harness(
            extractor: _ => AudioMetadataResult.Empty,
            preparer: (src, dir, _, _) =>
            {
                var name = "song" + Path.GetExtension(src);
                File.Copy(src, Path.Combine(dir, name), overwrite: true);
                return new ImportAudioResult(name, OriginalFileName: null, SilenceBaked: false);
            });
        h.Storage.NextOpenFile = CreateSourceAudio("Band - Song.mp3");
        h.Storage.NextFolder = _tempDir;
        h.Config.AppConfig.PadSilenceOnNewProject = true;
        h.Config.AppConfig.LeadingSilenceSeconds = 10.0;
        h.Config.AppConfig.TrailingSilenceSeconds = 2.0;

        await h.ChooseAudioAsync();
        h.State.TotalAudioLengthMs = 60_000;

        await h.CreateAsync();

        // Fallback ⇒ verbatim copy keeps the source extension.
        Assert.Equal("song.mp3", h.State.CurrentAudioFileName);
        Assert.True(File.Exists(Path.Combine(_tempDir, "Band - Song", "song.mp3")));

        // The grid is STILL shifted by the leading amount (today's behaviour).
        Assert.Equal(10_000.0, h.Editor.Beats[0].Time, 3);

        // Not baked ⇒ no offset recorded (the audio on disk is unpadded).
        Assert.Equal(0.0, h.State.ActiveProject!.Chart.MainAudioOffsetAdded, 3);
    }

    [Fact]
    public async Task Create_WithAudio_PadSilenceOff_NoShiftNoOffset()
    {
        var h = new Harness(extractor: _ => AudioMetadataResult.Empty);
        h.Storage.NextOpenFile = CreateSourceAudio("Band - Song.mp3");
        h.Storage.NextFolder = _tempDir;
        h.Config.AppConfig.PadSilenceOnNewProject = false;

        await h.ChooseAudioAsync();
        h.State.TotalAudioLengthMs = 60_000;

        await h.CreateAsync();

        // Off ⇒ verbatim copy, and the beat grid starts at 0 (no shift).
        Assert.Equal("song.mp3", h.State.CurrentAudioFileName);
        Assert.Equal(0.0, h.Editor.Beats[0].Time, 3);
        Assert.Equal(0.0, h.State.ActiveProject!.Chart.MainAudioOffsetAdded, 3);
    }

    // ---- pure filename heuristic ----------------------------------------------

    [Theory]
    [InlineData("Polyphia - Playing God.mp3", "Polyphia", "Playing God")]
    [InlineData("Some Band - Cool Song.flac", "Some Band", "Cool Song")]
    [InlineData("A - B - C.mp3", "A", "B - C")] // split on the FIRST separator
    [InlineData("  Spaced Band  -  Spaced Song  .ogg", "Spaced Band", "Spaced Song")]
    [InlineData("JustATitle.wav", "", "JustATitle")]
    [InlineData("no-separator-dashes.mp3", "", "no-separator-dashes")]
    [InlineData("", "", "")]
    public void ParseArtistTitleFromFileName_HandlesConventions(string fileName, string artist, string title)
    {
        var (parsedArtist, parsedTitle) = NewProjectCreator.ParseArtistTitleFromFileName(fileName);
        Assert.Equal(artist, parsedArtist);
        Assert.Equal(title, parsedTitle);
    }

    [Fact]
    public void ParseArtistTitleFromFileName_StripsDirectoryAndExtension()
    {
        var path = Path.Combine("C:", "music", "Polyphia - Playing God.flac");
        var (artist, title) = NewProjectCreator.ParseArtistTitleFromFileName(path);
        Assert.Equal("Polyphia", artist);
        Assert.Equal("Playing God", title);
    }

    // ── a second song in one open starts from nothing ─────────────────────────

    /// <summary>
    ///     The dialog is one instance per open, so nothing survives a reopen. What is left
    ///     is the switch INSIDE one open: a Japanese-tagged file read, then "Start without
    ///     audio". The creator writes the Japanese names unconditionally, so a leftover would
    ///     ship the first song's original-script artist and title in the second project's
    ///     manifest, and a leftover suggestion would paste its Latin names over the new form.
    /// </summary>
    [Fact]
    public async Task StartingWithoutAudio_ClearsWhatTheAudioPathWrote()
    {
        var h = new Harness(_ => new AudioMetadataResult("ヨルシカ", "だから僕は音楽を辞めた", "エルマ", "", "", "", null));
        h.Storage.NextOpenFile = CreateSourceAudio("song.mp3");
        await h.ChooseAudioAsync();
        Assert.Equal("ヨルシカ", h.Vm.JapaneseArtist);
        h.Vm.SuggestedArtist = "Yorushika";
        Assert.True(h.Vm.HasNameSuggestion);

        h.Vm.StartWithoutAudioCommand.Execute(null);

        Assert.Equal("", h.Vm.JapaneseArtist);
        Assert.Equal("", h.Vm.JapaneseTitle);
        Assert.False(h.Vm.HasNameSuggestion);
        Assert.Equal("", h.Vm.Artist);
        Assert.Equal("", h.Vm.Album);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var h = new Harness();
        Assert.Empty(DotCharter.Tests.Presentation.ContractConformanceTests.Violations(
            typeof(DotCharter.Presentation.Contracts.Dialogs.INewProjectScreen), h.Vm));
    }

    [Fact]
    public void A_fresh_dialog_opens_on_the_chooser_with_an_empty_form()
    {
        var h = new Harness();

        Assert.True(h.Vm.IsChooserStage);
        Assert.Equal("", h.Vm.Artist);
        Assert.Equal("", h.Vm.JapaneseArtist);
        Assert.False(h.Vm.HasNameSuggestion);
    }

    [Fact]
    public async Task Cancel_answers_nothing_and_creates_nothing()
    {
        var h = new Harness();
        h.Vm.CancelCommand.Execute(null);

        Assert.Null(await h.Vm.Result);
        Assert.Equal("", h.State.CurrentProjectPath);
    }
}
