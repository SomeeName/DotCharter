using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     File ▸ Export and View ▸ Screenshot left the shell for
///     <see cref="ExportMenuViewModel" />, which takes its save picker and its file
///     writer through the constructor — so the menu's guards and the storage seam
///     it uses can be driven without booting the app.
/// </summary>
public sealed class ExportMenuViewModelTests
{
    [Fact]
    public async Task EveryExport_WithoutAChart_SaysSoAndOpensNoPicker()
    {
        var state = new ProjectState();
        var storage = new FakeFileStorageService();
        var exports = Build(state, storage, new ExportTargetWriter());
        var loc = FakeLocalization.English();

        await exports.ExportGuitarProCommand.ExecuteAsync(null);
        Assert.Equal(loc["GpExport_NoChart"], state.MainDisplayMessage);

        await exports.ExportEditorOnFireCommand.ExecuteAsync(null);
        Assert.Equal(loc["EofExport_NoChart"], state.MainDisplayMessage);

        await exports.ExportSheetPdfCommand.ExecuteAsync(null);
        Assert.Equal(loc["SheetPdf_NoChart"], state.MainDisplayMessage);

        Assert.Empty(storage.RequestedTitles);
    }

    [Fact]
    public async Task AnExportOfAChart_AsksTheInjectedPickerForItsPath_AndACancelWritesNothing()
    {
        var harness = new ChartEditHarness();
        var storage = new FakeFileStorageService { NextSaveFile = null };
        var exports = Build(harness.State, storage, new ExportTargetWriter());
        harness.State.MainDisplayMessage = "before";

        await exports.ExportGuitarProCommand.ExecuteAsync(null);

        Assert.Equal([FakeLocalization.English()["GpExport_Title"]], storage.RequestedTitles);
        Assert.Equal("before", harness.State.MainDisplayMessage);
    }

    /// <summary>The .eof carries the vocals: a lyric draft still in its text box is applied before
    /// the picker opens.</summary>
    [Fact]
    public async Task AnEditorOnFireExport_AppliesALyricDraftFirst()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lo");
        var storage = new FakeFileStorageService { NextSaveFile = null };
        var exports = Build(harness.State, storage, new ExportTargetWriter(), harness);

        await exports.ExportEditorOnFireCommand.ExecuteAsync(null);

        Assert.Equal("lo", rig.Syllable.Text);
        Assert.Single(storage.RequestedTitles);
    }

    [Fact]
    public async Task AGuitarProExport_OfASoundOnAStringTheTuningLacks_IsRefusedBeforeThePickerOpens()
    {
        var harness = new ChartEditHarness();
        harness.State.ActiveLevel!.Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1000, harness.State.Beats),
            StringIndex = 9,
            Fret = 3
        });
        var storage = new FakeFileStorageService { NextSaveFile = "unused.gp" };
        var exports = Build(harness.State, storage, new ExportTargetWriter());
        var loc = FakeLocalization.English();

        await exports.ExportGuitarProCommand.ExecuteAsync(null);

        Assert.Empty(storage.RequestedTitles);
        Assert.Equal(StatusSeverity.Error, harness.State.MainDisplaySeverity);
        Assert.Equal(
            loc.Format("GpExport_Failed", loc.Format(ChartExportValidator.KeyStringBeyondTuning, "9")),
            harness.State.MainDisplayMessage);
    }

    [Fact]
    public async Task TheScreenshot_WithNoCaptureWired_ReportsTheFailure_AndCreatesNoFile()
    {
        var state = new ProjectState();
        var (writer, recorder) = RecordingProxy.Create<IExportTargetWriter>();
        var exports = Build(state, new FakeFileStorageService(), writer);

        await exports.CaptureHighwayScreenshotCommand.ExecuteAsync(null);

        Assert.Equal(FakeLocalization.English()["Status_ScreenshotFailed"], state.MainDisplayMessage);
        Assert.Empty(recorder.Calls);
    }

    /// <summary>
    ///     #638: a lossy EOF export says how many places it lost and offers Show, which opens the
    ///     Chart quality report on its Editor on Fire round trip; a lossless one offers nothing.
    ///     Either way the project is marked as one this charter exports to EOF (#685).
    /// </summary>
    [Fact]
    public void ALossyEofExport_CountsItsPlaces_AndShowOpensTheRoundTrip()
    {
        var state = new ProjectState();
        var project = new ChartProject();
        var exports = Build(state, new FakeFileStorageService(), new ExportTargetWriter());
        var shown = 0;
        exports.ShowChartReport = () => shown++;
        var report = new EofConversionReport();
        report.Add(EofConversionKind.Dropped, EofLoss.UnusedTone, "Lead", "m", detail: "clean");
        for (var level = 0; level < 3; level++)
            report.Add(EofConversionKind.Approximated, EofLoss.VibratoStrength, "Lead", "m", 1000, "120");

        Assert.Null(state.Document.EofRoundTripProject);
        exports.ReportEofExport(project, "song.eof", EofLossSummary.From(report));

        Assert.Same(project, state.Document.EofRoundTripProject);
        Assert.Equal(StatusSeverity.Warning, state.MainDisplaySeverity);
        Assert.Equal(FakeLocalization.English().Format("EofExport_DoneLossy", "song.eof", 2), state.MainDisplayMessage);
        var show = state.MainDisplayAction!;
        Assert.Equal(FakeLocalization.English()["EofExport_ShowLosses"], show.Label);

        show.Run();

        // "Show" brings the Issues tab up; the tab opens the round trip because the project is marked above.
        Assert.Equal(1, shown);

        state.Document.EofRoundTripProject = null;
        exports.ReportEofExport(project, "song.eof", EofLossSummary.Empty);
        Assert.Same(project, state.Document.EofRoundTripProject);
        Assert.Equal(StatusSeverity.Success, state.MainDisplaySeverity);
        Assert.Null(state.MainDisplayAction);
    }

    private static ExportMenuViewModel Build(ProjectState state, IFileStorageService storage, IExportTargetWriter writer,
        ChartEditHarness? editorHarness = null)
    {
        var harness = editorHarness ?? new ChartEditHarness();
        var (highway, _) = RecordingProxy.Create<IHighwayEditingService>();
        var sheet = new SheetMusicPaneViewModel(harness.State, harness.Editor, highway, harness.History,
            new FakeConfigurationService());
        return new ExportMenuViewModel(state, sheet, harness.Editor, new FakeAppLogger(), FakeLocalization.English(), new FakeConfigurationService(), storage, writer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingTheProjectWhileThePickerIsOpen_CancelsTheExport(bool sheet)
    {
        var harness = new ChartEditHarness();
        var dir = Path.Combine(Path.GetTempPath(), "dotcharter-export-picker-" + Guid.NewGuid().ToString("N"));
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new PendingPicker(picked.Task);
        var exports = Build(harness.State, storage, new ExportTargetWriter());
        var command = sheet ? exports.ExportSheetPdfCommand : exports.ExportGuitarProCommand;
        var exporting = command.ExecuteAsync(null);
        Assert.True(storage.Opened);
        Assert.False(exporting.IsCompleted);
        var replacement = new ChartProject();
        replacement.Chart.SongName = "Another recording";
        replacement.Beats.Add(new BeatMarker { Bpm = 60, Time = 0 });
        harness.State.ActiveProject = replacement;
        harness.State.MainDisplayMessage = "replacement loaded";
        try
        {
            picked.SetResult(Path.Combine(dir, sheet ? "song.pdf" : "song.gp"));
            await exporting;

            Assert.False(Directory.Exists(dir));
            Assert.Equal("replacement loaded", harness.State.MainDisplayMessage);
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    private sealed class PendingPicker(Task<string?> result) : IFileStorageService
    {
        public bool Opened { get; private set; }

        public Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
            IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null)
        {
            Opened = true;
            return result;
        }

        public Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
            string? startingDirectory = null) => throw new NotSupportedException();

        public Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null) =>
            throw new NotSupportedException();
    }
}
