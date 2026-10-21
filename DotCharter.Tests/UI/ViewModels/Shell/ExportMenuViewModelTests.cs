using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tests.Presentation.Dialogs;
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
        var (dialogs, recorder) = RecordingProxy.Create<IDialogs>();
        var exports = Build(state, new FakeFileStorageService(), new ExportTargetWriter(), dialogs);
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

        var init = Assert.IsType<Action<IChartQualityReportScreen>>(Assert.Single(recorder.Invocations).Args[0]);
        var screen = FakeChartQualityReportScreen.Default();
        init(screen);
        Assert.True(screen.FocusEofRoundTrip);
        Assert.True(screen.EofRoundTrip.IsExpanded);

        state.Document.EofRoundTripProject = null;
        exports.ReportEofExport(project, "song.eof", EofLossSummary.Empty);
        Assert.Same(project, state.Document.EofRoundTripProject);
        Assert.Equal(StatusSeverity.Success, state.MainDisplaySeverity);
        Assert.Null(state.MainDisplayAction);
    }

    private static ExportMenuViewModel Build(ProjectState state, IFileStorageService storage, IExportTargetWriter writer,
        IDialogs? dialogs = null)
    {
        var harness = new ChartEditHarness();
        var (highway, _) = RecordingProxy.Create<IHighwayEditingService>();
        var sheet = new SheetMusicPaneViewModel(harness.State, harness.Editor, highway, harness.History,
            new FakeConfigurationService());
        return new ExportMenuViewModel(state, sheet, new FakeAppLogger(), FakeLocalization.English(), storage, writer, dialogs);
    }
}
