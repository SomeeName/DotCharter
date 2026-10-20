using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed class RocksmithExportDialogViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-exportvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- harness --------------------------------------------------------------

    internal sealed class FakeProjectDocumentService : IProjectDocumentService
    {
        public int AssembleCalls { get; private set; }

        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) =>
            throw new NotSupportedException();

        public ChartProject CreateNewProject(string songName, string artist, string audioFileName) =>
            throw new NotSupportedException();
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics)
        {
            AssembleCalls++;
            return baseProject ?? new ChartProject();
        }
    }

    /// <summary>Answers instantly and records the dialog's in-flight state at the
    /// moment the modal went up — the window in which <c>Close()</c> used to refuse.</summary>
    internal sealed class RecordingPrompt : IMessagePrompt
    {
        public Func<bool>? Observe { get; set; }
        public bool? IsBuildingWhileShown { get; private set; }
        public int ShowCalls { get; private set; }

        public Task<bool> ShowAsync(string title, string message, MessageDialogKind kind = MessageDialogKind.Info,
            string? details = null, bool confirmation = false)
        {
            ShowCalls++;
            IsBuildingWhileShown = Observe?.Invoke();
            return Task.FromResult(true);
        }
    }

    internal sealed class FakeGameProbe : DotCharter.Services.Export.Rocksmith.IRocksmithGameProbe
    {
        public bool IsRunning { get; set; }
    }

    internal sealed class Harness
    {
        private readonly ChartEditDialogServices _services;
        private readonly RocksmithExportServices _io;
        private readonly RecordingPrompt _prompt;
        private RocksmithExportViewModel? _vm;

        public Harness(ChartProject project, RecordingPrompt? prompt = null)
        {
            var logger = new FakeAppLogger();
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements.FirstOrDefault();
            State.TotalAudioLengthMs = 32_000;

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            History = history;
            var editor = new ChartEditorViewModel(
                State, transport, history, selection, new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
            _prompt = prompt ?? new RecordingPrompt();
            (_maintenance, MaintenanceCalls) =
                DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>();
            _services = new ChartEditDialogServices(editor, history, State, Config, FakeLocalization.English(), logger);
            _io = new RocksmithExportServices(transport, Documents, Storage,
                new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(),
                DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Tone.Infrastructure.Shell.IShellLauncher>().Proxy,
                _prompt, Game);
        }

        /// <summary>The running-game answer a test export asks; closed unless a test opens it.</summary>
        public FakeGameProbe Game { get; } = new();

        private readonly DotCharter.Services.Editing.Maintenance.IChartMaintenanceService _maintenance;

        /// <summary>Every call the dialog made on the maintenance service (Fix).</summary>
        public DotCharter.Tests.Presentation.Dialogs.RecordingProxy MaintenanceCalls { get; }

        public HistoryService History { get; }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public FakeProjectDocumentService Documents { get; } = new();
        public DialogMemory Memory { get; } = new();

        /// <summary>What the dialog opens other dialogs through; a bare slot unless a test records.</summary>
        public DotCharter.Presentation.Dialogs.IDialogs Dialogs { get; set; } = new DotCharter.Presentation.Dialogs.DialogsSlot();

        /// <summary>The dialog of the current open; an open is a fresh view model, as the host builds one.</summary>
        public RocksmithExportViewModel Vm => _vm ?? throw new InvalidOperationException("open the dialog first");

        public RocksmithExportViewModel Open()
        {
            _vm = new RocksmithExportViewModel(_services, _io, Dialogs, Memory, _maintenance);
            _prompt.Observe = () => _vm.IsBuilding;
            return _vm;
        }

        public void Forget() => _vm = null;

        /// <summary>The hosted Tone designer over this harness's project, as the export dialog's button opens it.</summary>
        public ToneDesignerViewModel OpenToneDesigner()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var chain = new DotCharter.Tone.Audio.Dsp.NullDspChainService();
            var services = new ChartEditDialogServices(
                DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy,
                history, State, Config, FakeLocalization.English(), logger);
            return new ToneDesignerViewModel(services, Storage,
                FakeTonelabSession.Targets(new TonePreviewSession(chain, new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigApplier(chain))),
                new ToneDesignerTools(new DotCharter.Services.Editing.Tones.ToneGenerationRun(history,
                    new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger), new DotCharter.Presentation.Dialogs.DialogsSlot()), Memory,
                DotCharter.Services.Plugins.PackageFeatures.All);
        }
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    /// <summary>
    ///     #293: "Full report" opens the Chart quality report over the export, and whatever
    ///     changed while it was open (a Fix run from it) is in the export's own list when it closes.
    /// </summary>
    [Fact]
    public async Task FullReport_OpensTheQualityReport_AndTheExportRechecksWhenItCloses()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        var dialogs = new ReportDialogs(() =>
            project.Arrangements[0].Levels[0].Notes[0].Fret = 30);
        harness.Dialogs = dialogs;
        var vm = harness.Open();
        Assert.Empty(vm.ValidationItems);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ReviewReportCommand).ExecuteAsync(null);

        Assert.Equal([typeof(DotCharter.Presentation.Contracts.Dialogs.IChartQualityReportScreen)], dialogs.Opened);
        Assert.NotEmpty(vm.ValidationItems);
    }

    /// <summary>Records each open and runs <paramref name="whileOpen" /> as if the user had
    /// fixed (or broken) something in the dialog before closing it.</summary>
    private sealed class ReportDialogs(Action whileOpen) : DotCharter.Presentation.Dialogs.IDialogs
    {
        public List<Type> Opened { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DotCharter.Presentation.Dialogs.DialogChange>? Changed
        {
            add { }
            remove { }
        }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, DotCharter.Presentation.Dialogs.IDialogScreen<TResult>
        {
            Opened.Add(typeof(TContract));
            whileOpen();
            return Task.FromResult(default(TResult));
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }

    /// <summary>A minimal project that passes the export validator cleanly.</summary>
    internal static ChartProject CreateValidProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            Chart = { SongName = "Dialog Song", Artist = "DotCharter" }
        };
        for (var i = 0; i < 64; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var beats = project.Beats;

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Phrases.AddRange(["COUNT", "verse", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 2000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 30000), 2));
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 2000), Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 30000), Section = "noguitar" });

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(Pos(beats, 2000), 1, 4));
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000), StringIndex = 0, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 3000), StringIndex = 2, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var vocals = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = Pos(beats, 8000) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 8000),
            EndPosition = Pos(beats, 8400),
            Text = "test ",
            IsPhraseEnd = true,
            Note = 60
        });
        vocals.Phrases.Add(phrase);
        project.VocalTracks.Add(vocals);

        return project;
    }

    private string CreateFakeWem()
    {
        var path = Path.Combine(_tempDir, "audio.wem");
        var data = new byte[80_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    // ---- tests ------------------------------------------------------------------

    [Fact]
    public void Opening_without_a_project_assembles_nothing_and_offers_no_export()
    {
        var harness = new Harness(CreateValidProject());
        harness.State.ActiveProject = null;

        var vm = harness.Open();

        Assert.Equal(0, harness.Documents.AssembleCalls);
        Assert.Empty(vm.Arrangements);
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void Open_populates_arrangements_vocals_and_validation()
    {
        var harness = new Harness(CreateValidProject());

        harness.Open();

        Assert.Equal(1, harness.Documents.AssembleCalls);
        var item = Assert.Single(harness.Vm.Arrangements);
        Assert.Equal("Lead", item.Name);
        Assert.True(item.IsSelected);
        Assert.True(harness.Vm.HasVocals);
        Assert.True(harness.Vm.IncludeVocals);
        Assert.False(harness.Vm.HasBlockingErrors);
        Assert.Empty(harness.Vm.ValidationItems);
        Assert.EndsWith("_p.psarc", harness.Vm.OutputPath);
        Assert.Contains("DCDotCharterDialogSong", harness.Vm.DlcKeyWatermark);
    }

    /// <summary>A row named after its type said it twice ("Lead Lead", gallery capture 2026-09-24).</summary>
    [Fact]
    public void A_row_names_its_type_only_where_the_name_does_not()
    {
        var project = CreateValidProject();
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass 2", StringCount = 4 });
        var harness = new Harness(project);

        harness.Open();

        Assert.Equal(["", "Bass"], harness.Vm.Arrangements.Select(r => r.TypeLabel));
    }

    /// <summary>
    ///     The dialog is not modal to the timeline, so <c>Export</c> re-assembles and
    ///     re-VALIDATES the live project. It did not re-read the arrangement ROWS,
    ///     and those are both the <c>ArrangementFilter</c> and the lookup that decides
    ///     whether a finding sits on an INCLUDED arrangement. An arrangement added
    ///     while the dialog stood matched no row, so its error fell through to the
    ///     vocal branch and blocked an export the user had no tick box to clear.
    /// </summary>
    [Fact]
    public async Task Export_re_reads_the_arrangement_rows_it_re_validates()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "late_p.psarc");
        Assert.Single(harness.Vm.Arrangements);

        // The user adds an arrangement in the timeline behind the open dialog.
        var added = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Broken", StringCount = 6 };
        added.Levels.Add(new ChartLevel());
        project.Arrangements.Add(added);

        await harness.Vm.ExportCommand.ExecuteAsync(null);

        // The row exists, so the blocking error is attributable — and untickable.
        Assert.Equal(2, harness.Vm.Arrangements.Count);
        var row = Assert.Single(harness.Vm.Arrangements, a => a.Name == "Broken");
        Assert.True(harness.Vm.HasBlockingErrors);

        row.IsSelected = false;
        Assert.False(harness.Vm.HasBlockingErrors);
    }

    /// <summary>
    ///     The button was enabled — that is how the command ran. When the re-validate
    ///     takes the gate away the method returned in silence: no status line, no
    ///     dialog, no log, and the previous success banner left standing. It read as
    ///     a dead Export button.
    /// </summary>
    [Fact]
    public async Task Export_blocked_by_the_re_validation_says_why()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "blocked_p.psarc");
        harness.State.SetMessage("");

        var added = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Broken", StringCount = 6 };
        added.Levels.Add(new ChartLevel());
        project.Arrangements.Add(added);

        await harness.Vm.ExportCommand.ExecuteAsync(null);

        Assert.False(harness.Vm.HasResult);
        Assert.Equal(
            FakeLocalization.English()["Dialog_Export_ErrorsBlockExport"],
            harness.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, harness.State.MainDisplaySeverity);
    }

    /// <summary>
    ///     A failed build cleared <c>IsBuilding</c> in <c>finally</c> — which runs only
    ///     AFTER the error dialog is dismissed. For as long as the modal stood,
    ///     <c>Close()</c> read the dialog as busy and cancelled a build that had
    ///     already failed instead of closing.
    /// </summary>
    [Fact]
    public async Task A_failed_export_is_no_longer_in_flight_while_its_error_is_on_screen()
    {
        var messageDialog = new RecordingPrompt();
        var harness = new Harness(CreateValidProject(), messageDialog);
        harness.Open();
        // A path that cannot be resolved: the build throws.
        harness.Vm.AudioPath = Path.Combine(_tempDir, "does-not-exist.wem");
        harness.Vm.OutputPath = Path.Combine(_tempDir, "failed_p.psarc");

        await harness.Vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, messageDialog.ShowCalls);
        Assert.False(messageDialog.IsBuildingWhileShown);
        Assert.False(harness.Vm.IsBuilding);
        // …and the bar does not keep the failed run's position.
        Assert.Equal(0, harness.Vm.ProgressFraction);

        harness.Vm.CloseCommand.Execute(null);
        Assert.True(harness.Vm.Result.IsCompleted);
    }

    [Fact]
    public void Deselecting_a_broken_arrangement_unblocks_the_export()
    {
        var project = CreateValidProject();
        var broken = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Broken", StringCount = 6 };
        broken.Levels.Add(new ChartLevel());
        project.Arrangements.Add(broken);

        var harness = new Harness(project);
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");

        // "Broken" has no notes -> error finding that blocks while selected.
        Assert.True(harness.Vm.HasBlockingErrors);
        Assert.False(harness.Vm.ExportCommand.CanExecute(null));

        harness.Vm.Arrangements.First(a => a.Name == "Broken").IsSelected = false;

        Assert.False(harness.Vm.HasBlockingErrors);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));
    }

    /// <summary>
    ///     #183: a greyed Export always says why, above the action bar and as the
    ///     button's tooltip. The reason names the failing gate clauses, is empty exactly
    ///     when Export can run, and is announced on every change of the gate.
    /// </summary>
    [Fact]
    public void A_greyed_export_always_names_its_reason_and_only_then()
    {
        var project = CreateValidProject();
        var broken = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Broken", StringCount = 6 };
        broken.Levels.Add(new ChartLevel());
        project.Arrangements.Add(broken);
        var harness = new Harness(project);
        harness.Open();
        var vm = harness.Vm;
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ExportBlockedReason)) raised++;
        };

        void AssertMatchesTheGate(string step)
        {
            var canExport = vm.ExportCommand.CanExecute(null);
            Assert.True(canExport == (vm.ExportBlockedReason.Length == 0),
                $"{step}: Export {(canExport ? "runs" : "is grey")} but the reason is '{vm.ExportBlockedReason}'");
            Assert.Equal(!canExport, vm.IsExportBlocked);
            if (!canExport) Assert.Equal(string.Join("\n", vm.BlockingReasons()), vm.ExportBlockedReason);
        }

        AssertMatchesTheGate("no paths yet");
        Assert.True(vm.MissingPaths);

        vm.AudioPath = CreateFakeWem();
        vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");
        AssertMatchesTheGate("the broken arrangement blocks");
        Assert.True(vm.HasBlockingErrors);

        vm.Arrangements.First(a => a.Name == "Broken").IsSelected = false;
        AssertMatchesTheGate("exportable");
        Assert.Equal("", vm.ExportBlockedReason);

        vm.DlcKey = "abc";
        AssertMatchesTheGate("short DLC key");
        Assert.NotEqual("", vm.ExportBlockedReason);
        vm.DlcKey = "";

        foreach (var arrangement in vm.Arrangements) arrangement.IsSelected = false;
        vm.IncludeVocals = false;
        AssertMatchesTheGate("nothing ticked");
        Assert.True(vm.NothingSelectedToExport);

        Assert.True(raised >= 5, $"ExportBlockedReason was announced {raised} time(s) across five gate changes");
    }

    /// <summary>
    ///     #214: the dialog says each reason ONCE, in the strip above the footer, so that strip
    ///     names EVERY failing clause, one per line, in gate order (Mac, a blocking error, a short
    ///     DLC key, missing paths, nothing ticked), and clearing a clause removes exactly its line.
    /// </summary>
    [Fact]
    public void A_greyed_export_names_every_failing_clause_once_in_gate_order()
    {
        var project = CreateValidProject();
        var broken = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Broken", StringCount = 6 };
        broken.Levels.Add(new ChartLevel());
        project.Arrangements.Add(broken);
        // No keys: a Mac package cannot be encrypted, so Mac alone blocks (#782).
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(project);
        harness.Open();
        var vm = harness.Vm;
        var loc = FakeLocalization.English();
        string[] Lines() => vm.ExportBlockedReason.Split('\n');

        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.First(o => o.Value == DotCharter.Services.Export.ExportPlatform.Mac);
        vm.DlcKey = "ab";
        vm.AudioPath = "";
        Assert.Equal(
            [loc.Format("Dialog_Export_MacUnavailable", loc["Dialog_Export_MacNeedsEncryption"]), loc["Dialog_Export_ErrorsBlockExport"],
                loc["Dialog_Export_DlcKeyInvalid"], loc["Dialog_Export_MissingPathsHint"]],
            Lines());

        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.First(o => o.Value == DotCharter.Services.Export.ExportPlatform.Pc);
        vm.DlcKey = "";
        Assert.Equal([loc["Dialog_Export_ErrorsBlockExport"], loc["Dialog_Export_MissingPathsHint"]], Lines());

        foreach (var arrangement in vm.Arrangements) arrangement.IsSelected = false;
        vm.IncludeVocals = false;
        Assert.Equal([loc["Dialog_Export_MissingPathsHint"], loc["Dialog_Export_NothingSelectedHint"]], Lines());
    }

    [Fact]
    public void Short_dlc_key_blocks_export_until_cleared()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");

        harness.Vm.DlcKey = "abc";
        Assert.True(harness.Vm.HasInvalidDlcKey);
        Assert.False(harness.Vm.ExportCommand.CanExecute(null));

        harness.Vm.DlcKey = "";
        Assert.False(harness.Vm.HasInvalidDlcKey);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));
    }

    /// <summary>#747: a typed key with kana or an accented letter builds a package the game
    /// does not list, so it blocks export; ASCII punctuation is still stripped, not refused.</summary>
    [Fact]
    public void Non_ascii_dlc_key_blocks_export()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");

        harness.Vm.DlcKey = "DCAveMujicaザホールブルーワールドA";
        Assert.True(harness.Vm.HasInvalidDlcKey);
        Assert.False(harness.Vm.ExportCommand.CanExecute(null));

        harness.Vm.DlcKey = "DCBeyoncéHalo";
        Assert.True(harness.Vm.HasInvalidDlcKey);

        harness.Vm.DlcKey = "DC-AveMujica-01";
        Assert.False(harness.Vm.HasInvalidDlcKey);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));
    }

    /// <summary>#747: a Japanese title is a WARNING row (the game font may lack it), not a
    /// rewrite and not a block; the derived key in the watermark is ASCII.</summary>
    [Fact]
    public void Cjk_title_warns_without_blocking_and_the_derived_key_is_ascii()
    {
        var project = CreateValidProject();
        project.Chart.SongName = "ザホールブルーワールド";
        var harness = new Harness(project);
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");

        var row = Assert.Single(harness.Vm.ValidationItems, r => r.Message.Contains("ザホールブルーワールド", StringComparison.Ordinal));
        Assert.False(row.IsError);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));
        Assert.All(harness.Vm.DlcKeyWatermark.Split(" — ")[^1], c => Assert.True(char.IsAsciiLetterOrDigit(c), harness.Vm.DlcKeyWatermark));
    }

    /// <summary>
    ///     The one <c>CanExport</c> clause that used to have no readout of its
    ///     own. The other four each raise a strip already (invalid DLC key,
    ///     missing paths, blocking findings, a build in flight), so an
    ///     all-unticked chart reached a greyed Export with nothing on screen
    ///     saying why. <c>NothingSelectedToExport</c> is what the notice binds
    ///     to, and it has to track BOTH halves of that clause.
    /// </summary>
    [Fact]
    public void Unticking_every_arrangement_says_why_the_export_is_unavailable()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "out_p.psarc");

        Assert.False(harness.Vm.NothingSelectedToExport);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));

        foreach (var arrangement in harness.Vm.Arrangements) arrangement.IsSelected = false;

        // Both halves matter: this project HAS a lyric track and vocals are
        // still included, so there is genuinely something left to export and
        // the notice must stay down.
        Assert.True(harness.Vm.HasVocals);
        Assert.False(harness.Vm.NothingSelectedToExport);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));

        harness.Vm.IncludeVocals = false;

        Assert.True(harness.Vm.NothingSelectedToExport);
        Assert.False(harness.Vm.ExportCommand.CanExecute(null));

        harness.Vm.Arrangements[0].IsSelected = true;
        Assert.False(harness.Vm.NothingSelectedToExport);
        Assert.True(harness.Vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void Missing_paths_block_export()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = "";

        Assert.True(harness.Vm.MissingPaths);
        Assert.False(harness.Vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_builds_a_psarc_and_persists_presets()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "dialog_p.psarc");
        harness.Vm.AppId = "123456";
        harness.Vm.AudioVolume = -10.5;

        await harness.Vm.ExportCommand.ExecuteAsync(null);

        Assert.True(harness.Vm.HasResult);
        Assert.True(File.Exists(FirstResult(harness.Vm)));
        Assert.False(harness.Vm.IsBuilding);

        var config = harness.Config.AppConfig;
        Assert.Equal(_tempDir, config.LastExportDir);
        Assert.Equal("123456", config.LastExportAppId);
        Assert.Equal(-10.5, config.ExportAudioVolume);
        Assert.True(config.ExportIncludeVocals);
    }

    /// <summary>The designer commits its slot rows to the arrangement when it closes, and the export reads the arrangement.</summary>
    [Fact]
    public async Task Export_applies_the_tone_designers_slot_edits()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "tones_p.psarc");

        var designer = harness.OpenToneDesigner();
        var row = Assert.Single(designer.Arrangements);
        row.ToneBase = "dc_base";
        row.ToneA = "dc_base";
        designer.CloseCommand.Execute(null);

        await harness.Vm.ExportCommand.ExecuteAsync(null);
        Assert.True(harness.Vm.HasResult);

        // The package is built from the arrangement (the manifest's Tone_Base/Tone_A come from
        // these fields; the builder's own tests pin that), so the edits must be on it.
        var lead = harness.State.ActiveProject!.Arrangements[0];
        Assert.Equal("dc_base", lead.ToneBase);
        Assert.Equal("dc_base", lead.ToneA);
        Assert.True(File.Exists(FirstResult(harness.Vm)));
    }

    /// <summary>
    ///     Two arrangements with the SAME name do not crash the dialog on reopen.
    ///     Nothing in the app enforces unique arrangement names — importing the
    ///     same Guitar Pro file twice, or one whose tracks share a name, produces
    ///     exactly this — and the reopen path rebuilt its "keep the ticks the
    ///     user set" map with <c>ToDictionary(a => a.Name, …)</c>, which throws
    ///     <see cref="ArgumentException" /> on the duplicate key. The first Open
    ///     was fine (the map starts empty for a new project), so the throw came
    ///     out of the second one, from a <c>[RelayCommand]</c> with no catch.
    /// </summary>
    [Fact]
    public void Reopening_for_a_project_with_duplicate_arrangement_names_keeps_the_ticks()
    {
        var project = CreateValidProject();
        var twin = new Arrangement
        {
            ArrangementType = ArrangementType.Rhythm,
            Name = project.Arrangements[0].Name,
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) twin.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(twin);

        var harness = new Harness(project);
        harness.Open();
        Assert.Equal(2, harness.Vm.Arrangements.Count);

        harness.Vm.Arrangements[1].IsSelected = false;
        harness.Vm.CloseCommand.Execute(null);

        harness.Open();

        Assert.Equal(2, harness.Vm.Arrangements.Count);
        // Name-keyed carry-over: the last row's state is what the map holds.
        Assert.All(harness.Vm.Arrangements, a => Assert.False(a.IsSelected));
    }

    /// <summary>
    ///     #782: PC + Mac is the default wherever a Mac package can be encrypted, and the note
    ///     under the picker names both files; Mac alone re-suffixes the output to <c>_m</c>.
    /// </summary>
    [Fact]
    public void Platform_choice_sets_the_output_suffix_and_names_both_packages()
    {
        var loc = FakeLocalization.English();
        var harness = new Harness(CreateValidProject());
        harness.Open();
        var vm = harness.Vm;
        vm.OutputPath = Path.Combine(_tempDir, "pair_p.psarc");

        Assert.Equal(ExportPlatform.PcAndMac, vm.Target.SelectedPlatformOption.Value);
        Assert.Null(vm.MacBlock);
        Assert.Equal(loc.Format("Dialog_Export_WritesBoth", "pair_p.psarc", "pair_m.psarc"), vm.PlatformNote);
        Assert.True(vm.HasPlatformNote);

        Pick(vm, ExportPlatform.Mac);
        Assert.EndsWith("pair_m.psarc", vm.OutputPath);
        Assert.False(vm.HasPlatformNote);

        Pick(vm, ExportPlatform.Pc);
        Assert.EndsWith("pair_p.psarc", vm.OutputPath);
        Assert.False(vm.HasPlatformNote);
    }

    /// <summary>
    ///     #782: with no Mac encryption, Mac alone is blocked with its reason and PC + Mac exports the
    ///     PC package alone, saying why — never a plain Mac package (only the Windows enabler loads
    ///     plain ones). The reason is neutral: it names encryption, not keys.
    /// </summary>
    [Fact]
    public async Task Without_mac_encryption_mac_blocks_and_pc_and_mac_writes_only_pc()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var loc = FakeLocalization.English();
        var harness = new Harness(CreateValidProject());
        harness.Open();
        var vm = harness.Vm;
        vm.AudioPath = CreateFakeWem();
        vm.OutputPath = Path.Combine(_tempDir, "nokeys_p.psarc");
        Assert.Equal(ExportPlatform.Pc, vm.Target.SelectedPlatformOption.Value);
        Assert.Equal(RocksmithMacBlock.NoEncryption, vm.MacBlock);

        Pick(vm, ExportPlatform.Mac);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Contains(loc.Format("Dialog_Export_MacUnavailable", loc["Dialog_Export_MacNeedsEncryption"]),
            vm.ExportBlockedReason.Split('\n'));
        Assert.Equal(loc["Dialog_Export_MacNeedsEncryption"], vm.PlatformNote);

        Pick(vm, ExportPlatform.PcAndMac);
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.Equal(loc.Format("Dialog_Export_PcOnlyBecause", loc["Dialog_Export_MacNeedsEncryption"]), vm.PlatformNote);

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);
        Assert.Equal(Path.Combine(_tempDir, "nokeys_p.psarc"), vm.ResultPath);
        Assert.False(File.Exists(Path.Combine(_tempDir, "nokeys_m.psarc")));
    }

    /// <summary>#782: a TICKED 7-string guitar rules the Mac package out (rs7string is
    /// Windows-only); unticking it brings PC + Mac back.</summary>
    [Fact]
    public void A_ticked_seven_string_guitar_rules_the_mac_package_out()
    {
        var loc = FakeLocalization.English();
        var project = CreateValidProject();
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Seven", StringCount = 7 });
        var harness = new Harness(project);
        harness.Open();
        var vm = harness.Vm;
        vm.OutputPath = Path.Combine(_tempDir, "seven_p.psarc");
        var seven = vm.Arrangements.Single(a => a.Name == "Seven");
        Assert.True(seven.IsExtendedString);

        Assert.Equal(RocksmithMacBlock.ExtendedStrings, vm.MacBlock);
        Assert.Equal(loc.Format("Dialog_Export_PcOnlyBecause", loc["Dialog_Export_MacSixStringsOnly"]), vm.PlatformNote);

        Pick(vm, ExportPlatform.Mac);
        Assert.Contains(loc.Format("Dialog_Export_MacUnavailable", loc["Dialog_Export_MacSixStringsOnly"]),
            vm.ExportBlockedReason.Split('\n'));

        seven.IsSelected = false;
        Assert.Null(vm.MacBlock);
        Assert.DoesNotContain(loc.Format("Dialog_Export_MacUnavailable", loc["Dialog_Export_MacSixStringsOnly"]),
            vm.ExportBlockedReason.Split('\n'));
        Pick(vm, ExportPlatform.PcAndMac);
        Assert.Equal(loc.Format("Dialog_Export_WritesBoth", "seven_p.psarc", "seven_m.psarc"), vm.PlatformNote);
    }

    /// <summary>#782: one export, two packages — the PC one and the Mac one beside it, both
    /// listed in the result and the PC one revealed.</summary>
    [Fact]
    public async Task Pc_and_mac_export_writes_both_packages()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();
        var vm = harness.Vm;
        vm.AudioPath = CreateFakeWem();
        vm.OutputPath = Path.Combine(_tempDir, "both_p.psarc");

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        var pc = Path.Combine(_tempDir, "both_p.psarc");
        var mac = Path.Combine(_tempDir, "both_m.psarc");
        Assert.Equal([pc, mac], vm.ResultPath.Split('\n'));
        Assert.True(File.Exists(pc));
        Assert.True(File.Exists(mac));
        // Two different packages, not one written twice (the Mac layout itself is the builder's tests').
        Assert.NotEqual(File.ReadAllBytes(pc), File.ReadAllBytes(mac));
    }

    private static void Pick(RocksmithExportViewModel vm, ExportPlatform platform) =>
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.First(o => o.Value == platform);

    private static string FirstResult(RocksmithExportViewModel vm) => vm.ResultPath.Split('\n')[0];

    [Fact]
    public async Task Browse_output_appends_psarc_suffix_and_remembers_directory()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();

        harness.Storage.NextSaveFile = Path.Combine(_tempDir, "mysong_p.psarc");
        await harness.Vm.BrowseOutputCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(_tempDir, "mysong_p.psarc"), harness.Vm.OutputPath);
        Assert.Equal(_tempDir, harness.Config.AppConfig.LastExportDir);
    }

    // ---- version, test export, auto volume (#953) ---------------------------------

    [Fact]
    public void The_release_file_is_named_after_artist_title_and_version_and_follows_the_version()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        var vm = harness.Open();

        Assert.Equal("1", vm.Version);
        Assert.Equal("DotCharter_Dialog-Song_v1_p.psarc", Path.GetFileName(vm.OutputPath));

        vm.Version = "2.1";
        Assert.Equal("DotCharter_Dialog-Song_v2_1_p.psarc", Path.GetFileName(vm.OutputPath));

        // A path the user chose keeps its name.
        vm.OutputPath = Path.Combine(_tempDir, "mine_p.psarc");
        vm.Version = "3";
        Assert.Equal("mine_p.psarc", Path.GetFileName(vm.OutputPath));
    }

    [Fact]
    public async Task The_version_is_in_the_build_and_becomes_the_songs_when_the_dialog_closes()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        var vm = harness.Open();

        vm.Version = " 4.0 ";
        Assert.Equal("4.0", vm.BuildOptions(project).Version);
        vm.Version = "";
        Assert.Equal("1", vm.BuildOptions(project).Version);

        vm.Version = "4.0";
        vm.CloseCommand.Execute(null);
        Assert.Equal("4.0", project.Chart.Version);

        // One undo step brings the old one back.
        harness.History.Undo();
        Assert.Equal("1", project.Chart.Version);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Test_export_writes_into_the_test_folder_replacing_earlier_builds_while_the_game_is_closed()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = CreateValidProject();
        var harness = new Harness(project);
        var vm = harness.Open();
        var folder = Path.Combine(_tempDir, "testbuilds");
        vm.TestFolder = folder;
        vm.AudioPath = CreateFakeWem();
        vm.Version = "9";
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "dcdotcharterdialogsong_v3_p.psarc");
        File.WriteAllText(stale, "old");

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal(Path.Combine(folder, "dcdotcharterdialogsong_p.psarc"), FirstResult(vm));
        Assert.False(File.Exists(stale));
        // The release is untouched: the chart keeps its version and the output field its name.
        Assert.Equal("1", project.Chart.Version);
        Assert.Equal("Dialog Song", project.Chart.SongName);
        Assert.Equal(folder, harness.Config.AppConfig.ExportTestFolder);
        Assert.NotEqual(folder, harness.Config.AppConfig.LastExportDir);
    }

    [Fact]
    public async Task Test_export_adds_a_version_to_name_key_and_title_while_the_game_runs()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = CreateValidProject();
        var harness = new Harness(project);
        var vm = harness.Open();
        var folder = Path.Combine(_tempDir, "running");
        vm.TestFolder = folder;
        vm.AudioPath = CreateFakeWem();
        harness.Game.IsRunning = true;

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
        var first = FirstResult(vm);
        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
        var second = FirstResult(vm);

        Assert.Equal(Path.Combine(folder, "dcdotcharterdialogsong_v1_p.psarc"), first);
        Assert.Equal(Path.Combine(folder, "dcdotcharterdialogsong_v2_p.psarc"), second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        // The title change is on the build's private copy only.
        Assert.Equal("Dialog Song", project.Chart.SongName);

        // Game closed again: the plain name is back and every earlier build goes.
        harness.Game.IsRunning = false;
        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
        Assert.Equal(["dcdotcharterdialogsong_p.psarc"],
            Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Test_export_needs_audio_and_a_clean_chart_but_not_an_output_file()
    {
        var harness = new Harness(CreateValidProject());
        var vm = harness.Open();

        Assert.False(vm.TestExportCommand.CanExecute(null));
        vm.AudioPath = CreateFakeWem();
        vm.OutputPath = "";
        Assert.True(vm.TestExportCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Auto_volume_sets_the_volume_from_the_measured_loudness()
    {
        var harness = new Harness(CreateValidProject());
        var vm = harness.Open();
        Assert.False(vm.AutoVolumeCommand.CanExecute(null));
        Assert.False(vm.AutoPreviewVolumeCommand.CanExecute(null));

        var path = Path.Combine(_tempDir, "tone.wav");
        var frames = 48000 * 3;
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(0.2 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / 48000));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, 48000));
        vm.AudioPath = path;
        vm.PreviewAudioPath = path;

        await ((IAsyncRelayCommand)vm.AutoVolumeCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)vm.AutoPreviewVolumeCommand).ExecuteAsync(null);

        var expected = RocksmithAutoVolume.Calculate(path);
        Assert.NotEqual(-8.0, expected);
        Assert.Equal(expected, vm.AudioVolume);
        Assert.Equal(expected, vm.PreviewVolume);
        Assert.False(vm.HasVolumeMessage);
    }

    [Fact]
    public async Task Auto_volume_says_why_it_measured_nothing()
    {
        var harness = new Harness(CreateValidProject());
        var vm = harness.Open();
        vm.AudioPath = Path.Combine(_tempDir, "missing.wav");

        await ((IAsyncRelayCommand)vm.AutoVolumeCommand).ExecuteAsync(null);

        Assert.Equal(-8.0, vm.AudioVolume);
        Assert.True(vm.HasVolumeMessage);
        Assert.False(vm.IsMeasuringVolume);
    }

    // ---- the closed project must not stay pinned -------------------------------

    /// <summary>
    ///     The dialog is an app-wide singleton and remembers WHICH project it was
    ///     last opened for, so that reopening the same one keeps the fields the
    ///     user filled in. That memory held the project STRONGLY, which pinned its
    ///     whole chart graph — every arrangement, level, note, chord, hand shape
    ///     and anchor — for the life of the process, once per project the session
    ///     ever exported from.
    ///     <para>
    ///         Structural assertion per the root <c>AGENTS.md</c> rule: whether the
    ///         closed graph is collectable, never a byte count.
    ///     </para>
    /// </summary>
    [Fact]
    public void Switching_projects_leaves_the_exported_project_collectable()
    {
        var (harness, probe) = OpenTheDialogForAProjectThenAbandonIt();

        harness.State.ActiveArrangement = null;
        harness.State.ActiveProject = new ChartProject();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive,
            "the export dialog is still holding the project it was last opened for — its whole chart " +
            "graph stays alive for the rest of the session.");
    }

    /// <summary>
    ///     …and the memory itself must survive, or the fix would be "satisfied" by
    ///     simply forgetting on close. Reopening the SAME project keeps the DLC key
    ///     and the per-arrangement ticks; opening a DIFFERENT one resets them.
    ///     That is exactly why clearing the field at <c>Close</c> is the wrong fix.
    /// </summary>
    [Fact]
    public void Reopening_the_same_project_keeps_the_fields_but_a_different_one_resets_them()
    {
        var harness = new Harness(CreateValidProject());
        harness.Open();

        harness.Vm.DlcKey = "TypedByHand";
        harness.Vm.Arrangements[0].IsSelected = false;
        harness.Vm.CloseCommand.Execute(null);

        harness.Open();
        Assert.Equal("TypedByHand", harness.Vm.DlcKey);
        Assert.False(harness.Vm.Arrangements[0].IsSelected);

        var second = CreateValidProject();
        harness.State.ActiveProject = second;
        harness.State.ActiveArrangement = second.Arrangements[0];
        harness.Open();

        Assert.Equal("", harness.Vm.DlcKey);
        Assert.True(harness.Vm.Arrangements[0].IsSelected);
    }

    /// <summary>Kept out of the caller's frame so the project it builds cannot be
    /// held alive by a stack slot the JIT has not yet reused.</summary>
    private static (Harness Harness, WeakReference Probe) OpenTheDialogForAProjectThenAbandonIt()
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        harness.Open().CloseCommand.Execute(null);
        harness.Forget();
        return (harness, new WeakReference(project));
    }

    // ---- #184: Fix per finding ---------------------------------------------------------

    [Fact]
    public void A_fixable_finding_offers_Fix_and_runs_its_verb_on_its_own_arrangement()
    {
        var project = CreateValidProject();
        project.Arrangements[0].Levels[0].Anchors.Clear();
        project.Arrangements[0].Levels[0].Anchors.Add(new FretAnchor(Pos(project.Beats, 2500), 3, 4));
        var harness = new Harness(project);
        harness.Open();

        var row = Assert.Single(harness.Vm.ValidationItems);
        Assert.True(row.HasFix);
        Assert.Contains("Lead", row.FixDescription);
        row.FixCommand.Execute(null);

        var call = Assert.Single(harness.MaintenanceCalls.Invocations, i => i.Method == "FixExportFinding");
        Assert.Equal(DotCharter.Services.Export.Validation.ExportFixKind.FillFhp, call.Args[0]);
        Assert.Same(project.Arrangements[0], call.Args[1]);
    }

    /// <summary>#195: an undefined tone the project can only mean one way. Fix says exactly what it
    /// renames and asks the maintenance verb for it — no Tone designer.</summary>
    [Fact]
    public void An_undefined_tone_with_one_possible_repair_offers_the_rename_as_its_Fix()
    {
        var project = CreateValidProject();
        var lead = project.Arrangements[0];
        lead.ToneBase = "lead_dist";
        lead.ToneA = "lead_dist";
        lead.StartingTone = "lead_dist";
        lead.ToneChanges.Add(new ToneChange { Position = Pos(project.Beats, 4000), ToneName = "lead_dist" });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "lead_crunch", Name = "lead_crunch", Json = "{}" });
        var harness = new Harness(project);
        harness.Open();

        var row = Assert.Single(harness.Vm.ValidationItems, r => r.Message.Contains("lead_dist", StringComparison.Ordinal));
        Assert.True(row.HasFix);
        Assert.Equal(FakeLocalization.English().Format("Export_FixTip_RenameTone", "lead_crunch", "lead_dist"), row.FixDescription);
        row.FixCommand.Execute(null);

        var call = Assert.Single(harness.MaintenanceCalls.Invocations, i => i.Method == "RepairUndefinedToneKey");
        Assert.Equal("lead_dist", call.Args[0]);
        Assert.DoesNotContain(harness.MaintenanceCalls.Invocations, i => i.Method == "FixExportFinding");
    }

    [Fact]
    public void A_finding_no_verb_can_fix_offers_no_Fix()
    {
        var project = CreateValidProject();
        project.Arrangements[0].Levels[0].Notes[0].Fret = 30;
        var harness = new Harness(project);
        harness.Open();

        var row = Assert.Single(harness.Vm.ValidationItems);
        Assert.False(row.HasFix);
        Assert.Equal("", row.FixDescription);
    }

    /// <summary>#743: a 7-string guitar offers the split option, off by default; ticked, the
    /// package's SNG carries the 7th-string chord as single notes and no chord sidecar is
    /// written. A six-string project never shows it.</summary>
    [Fact]
    public async Task Split_option_shows_for_a_seven_string_guitar_and_reaches_the_package()
    {
        Assert.False(OpenHarness(CreateValidProject()).Vm.HasExtendedStringGuitar);

        var project = CreateValidProject();
        var lead = project.Arrangements[0];
        lead.StringCount = 7;
        lead.TuningPitches.Add(new TuningOffset(0));
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[0] = 2; // low B
        template.Frets[1] = 4; // low E
        lead.ChordTemplates.Add(template);
        var chord = new Chord { Position = Pos(project.Beats, 5000), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 4 });
        lead.Levels[0].Chords.Add(chord);

        var harness = OpenHarness(project);
        Assert.True(harness.Vm.HasExtendedStringGuitar);
        Assert.False(harness.Vm.SplitExtendedStringChords);
        harness.Vm.SplitExtendedStringChords = true;
        harness.Vm.AudioPath = CreateFakeWem();
        harness.Vm.OutputPath = Path.Combine(_tempDir, "split_p.psarc");

        await harness.Vm.ExportCommand.ExecuteAsync(null);

        Assert.True(harness.Vm.HasResult, harness.Vm.ExportBlockedReason);
        // What Split does to the SNG is the RS7 builder's tests'; the dialog must ask for it.
        Assert.Equal(DotCharter.Services.Export.Rocksmith.Rs7ChordMode.Split, harness.Vm.BuildOptions(project).Rs7ChordMode);
        Assert.False(File.Exists(DotCharter.Services.Export.Rocksmith.Rs7ChordSidecar.PathFor(FirstResult(harness.Vm), null)),
            "split mode wrote a chord sidecar");
    }

    private static Harness OpenHarness(ChartProject project)
    {
        var harness = new Harness(project);
        harness.Open();
        return harness;
    }
}
