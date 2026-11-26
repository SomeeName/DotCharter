using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     #638: the Chart quality report's Editor on Fire round trip names each kind of loss in the
///     user's words, each place by arrangement, bar.beat and time, lists the first few until Show
///     all, opens the whole list as text, and jumps to a place. #685: it is computed only once the
///     section opens (or the report is opened from an EOF export), on the injected off-thread
///     runner, and a result for a chart that has since been rechecked is dropped.
/// </summary>
public sealed class EofRoundTripSectionViewModelTests
{
    private static readonly IReadOnlyList<BeatMarker> Beats =
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static (EofRoundTripSectionViewModel Section, RecordingProxy Dialogs, List<(string, double?)> Jumps) Section()
    {
        var (dialogs, recorder) = RecordingProxy.Create<IDialogs>();
        var jumps = new List<(string, double?)>();
        var section = new EofRoundTripSectionViewModel(FakeLocalization.English(), dialogs, new FakeAppLogger(),
            (subject, ms) => jumps.Add((subject, ms)), () => null);
        return (section, recorder, jumps);
    }

    private static EofLossSummary EightWidthsAndATone()
    {
        var report = new EofConversionReport();
        for (var i = 0; i < 8; i++)
            report.Add(EofConversionKind.Approximated, EofLoss.AnchorWidth, "Lead", "m", 2000 + i * 500.0, "3");
        report.Add(EofConversionKind.Dropped, EofLoss.UnusedTone, "Rhythm", "m", detail: "clean");
        report.Add(EofConversionKind.Approximated, EofLoss.MeasureStart, EofLosses.BeatsSubject, "m", 2500);
        return EofLossSummary.From(report);
    }

    [Fact]
    public void EachKindIsNamed_AndEachPlaceSaysWhere()
    {
        var (section, _, _) = Section();
        var loc = FakeLocalization.English();

        section.Show(EightWidthsAndATone(), "", Beats);

        Assert.Equal("1 dropped · 9 approximated", section.Summary);
        Assert.Equal([loc["EofLoss_UnusedTone"], loc["EofLoss_AnchorWidth"], loc["EofLoss_MeasureStart"]],
            section.Groups.Select(g => g.Title));
        var tone = section.Groups[0];
        Assert.True(tone.IsDropped);
        Assert.Equal("Dropped (1)", tone.Count);
        Assert.Equal(("Rhythm", "clean", false), (tone.Items[0].Subject + tone.Items[0].Where, tone.Items[0].Detail, tone.Items[0].HasTime));
        var width = section.Groups[1].Items[0];
        Assert.Equal(("Lead", "M2.1 · 00:02.00"), (width.Subject, width.Where));
        Assert.Equal("3", width.Detail);
        Assert.Equal(("Beat map", "M2.2 · 00:02.50"), (section.Groups[2].Items[0].Subject, section.Groups[2].Items[0].Where));
    }

    [Fact]
    public void AKindListsItsFirstPlaces_UntilShowAll()
    {
        var (section, _, _) = Section();
        section.Show(EightWidthsAndATone(), "", Beats);
        var widths = section.Groups[1];

        Assert.True(section.CanShowAll);
        Assert.Equal(EofLossSummary.DefaultPlacesPerGroup, widths.Items.Count);
        Assert.True(widths.HasMore);
        Assert.StartsWith("3 more", widths.MoreNote, StringComparison.Ordinal);

        section.ShowAll = true;

        Assert.Equal(8, section.Groups[1].Items.Count);
        Assert.False(section.Groups[1].HasMore);
    }

    [Fact]
    public void OpenAsText_ShowsEveryPlaceOfEveryKind()
    {
        var (section, dialogs, _) = Section();
        section.Show(EightWidthsAndATone(), "", Beats);

        section.OpenListCommand.Execute(null);

        // The text-document dialog it opens, configured the way the host configures it.
        var init = Assert.IsType<Action<ITextDocumentScreen>>(Assert.Single(dialogs.Invocations).Args[0]);
        var document = new TextDocumentViewModel(RecordingProxy.Create<ISystemClipboard>().Proxy,
            RecordingProxy.Create<IShellLauncher>().Proxy, new FakeAppLogger());
        init(document);
        Assert.Equal(FakeLocalization.English()["Dialog_QualityReport_EofTitle"], document.Title);
        Assert.Contains("Lead · M3.4 · 00:05.50 · 3", document.Text, StringComparison.Ordinal);
        Assert.Contains("Rhythm · clean", document.Text, StringComparison.Ordinal);
        Assert.Equal(10, document.Text.Split('\n').Count(l => l.StartsWith("    ", StringComparison.Ordinal)));
    }

    [Fact]
    public void GoTo_HandsBackTheSubjectAndTime()
    {
        var (section, _, jumps) = Section();
        section.Show(EightWidthsAndATone(), "", Beats);

        section.Groups[1].Items[2].JumpCommand.Execute(null);

        var (subject, ms) = Assert.Single(jumps);
        Assert.Equal("Lead", subject);
        Assert.Equal(3000, ms);
        Assert.False(section.Groups[0].Items[0].JumpCommand.CanExecute(null));
    }

    [Fact]
    public void NothingLost_OrNotChecked_SaysSo()
    {
        var (section, _, _) = Section();
        var loc = FakeLocalization.English();

        section.Show(EofLossSummary.Empty, "", Beats);
        Assert.Equal(loc["Dialog_QualityReport_EofLossless"], section.Summary);
        Assert.False(section.HasGroups);
        Assert.False(section.OpenListCommand.CanExecute(null));

        section.Show(null, "boom", Beats);
        Assert.Equal(loc.Format("Dialog_QualityReport_EofFailed", "boom"), section.Summary);
    }

    /// <summary>
    ///     The off-thread seam: every run is handed out, held, and finished by the test on a pool
    ///     thread, so a section that computed on the calling thread would show its groups before
    ///     the test lets the run finish.
    /// </summary>
    private sealed class HeldRunner
    {
        public List<(Func<EofRoundTripCheck> Work, TaskCompletionSource<EofRoundTripCheck> Done)> Runs { get; } = [];

        public Task<EofRoundTripCheck> Run(Func<EofRoundTripCheck> work)
        {
            var done = new TaskCompletionSource<EofRoundTripCheck>();
            Runs.Add((work, done));
            return done.Task;
        }

        public async Task FinishAsync(int run)
        {
            var (work, done) = Runs[run];
            done.SetResult(await Task.Run(work));
        }
    }

    /// <summary>A chart with a cent offset on Rhythm and a partial tremolo chord at 3 s.</summary>
    private static (ChartEditHarness H, Arrangement Rhythm) ChartEofApproximates()
    {
        var h = new ChartEditHarness();
        var rhythm = new Arrangement { Name = "Rhythm", CentOffset = 20 };
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(6) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Tremolo = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        rhythm.Levels.Add(level);
        // The export gate's "no show lights" warning is song-wide and would add a row: this chart has them.
        h.Project.Showlights.Add(new DotCharter.Core.Domain.Events.Showlight());
        // Likewise the song-list metadata warnings (missing title, artist, album, year) are song-wide: this chart is filled in.
        h.Project.Chart.SongName = "Song";
        h.Project.Chart.Artist = "Artist";
        h.Project.Chart.AlbumName = "Album";
        h.Project.Chart.AlbumYear = "1999";
        h.Project.Arrangements.Add(rhythm);
        return (h, rhythm);
    }

    private static ExportReadinessViewModel Report(ChartEditHarness h) => new(h.State,
        new CheckpointsViewModelTests.FakeDocumentService(), h.Transport, h.Editor, FakeLocalization.English(),
        RecordingProxy.Create<IChartMaintenanceService>().Proxy, RecordingProxy.Create<IDialogs>().Proxy, new FakeAppLogger(),
        new ChartQualityReporter(new DotCharter.Services.Editing.Validation.ArrangementValidationService(new FakeAppLogger())), new DotCharter.Tests.Infrastructure.TestUiDispatcher());

    /// <summary>
    ///     A charter who never exported to EOF: the Issues tab opens with the section closed and
    ///     runs nothing, however often it is refreshed. Opening it hands the export to the
    ///     off-thread runner, shows the busy state until the run lands, then the groups.
    /// </summary>
    [Fact]
    public async Task TheRoundTrip_IsComputedOnlyOnceOpened_AndOffTheCallingThread()
    {
        var (h, _) = ChartEofApproximates();
        var loc = FakeLocalization.English();
        var report = Report(h);
        var section = report.EofRoundTripModel;
        var runner = new HeldRunner();
        section.OffThread = runner.Run;

        report.RefreshChartReport();
        Assert.False(section.IsExpanded);
        Assert.Same(Task.CompletedTask, section.Pending);
        Assert.Empty(runner.Runs);
        Assert.Empty(section.Groups);

        section.IsExpanded = true;

        Assert.Single(runner.Runs);
        Assert.True(section.IsBusy);
        Assert.Equal(loc["Dialog_QualityReport_EofChecking"], section.Summary);
        Assert.Empty(section.Groups);

        await runner.FinishAsync(0);
        await section.Pending;

        Assert.False(section.IsBusy);
        Assert.Contains(section.Groups, g => g.Title == loc["EofLoss_CentOffset"]);
        section.IsExpanded = false;
        section.IsExpanded = true;
        Assert.Single(runner.Runs);
    }

    /// <summary>
    ///     A run that lands after the chart changed and the tab was refreshed is the OLD chart's: it
    ///     is dropped, and the one for the chart as it is now is shown.
    /// </summary>
    [Fact]
    public async Task ARunThatLandsAfterAnEditAndARefresh_IsDropped()
    {
        var (h, rhythm) = ChartEofApproximates();
        var loc = FakeLocalization.English();
        var report = Report(h);
        var section = report.EofRoundTripModel;
        var runner = new HeldRunner();
        section.OffThread = runner.Run;
        section.IsExpanded = true;
        var stale = section.Pending;

        rhythm.CentOffset = 0;
        report.RefreshChartReport();
        Assert.Equal(2, runner.Runs.Count);
        var current = section.Pending;
        await runner.FinishAsync(1);
        await current;

        Assert.False(section.IsBusy);
        Assert.Contains(section.Groups, g => g.Title == loc["EofLoss_PartialChordTremolo"]);
        Assert.DoesNotContain(section.Groups, g => g.Title == loc["EofLoss_CentOffset"]);

        // The old chart's run lands last: it still had the cent offset, and it changes nothing.
        await runner.FinishAsync(0);
        await stale;

        Assert.False(section.IsBusy);
        Assert.DoesNotContain(section.Groups, g => g.Title == loc["EofLoss_CentOffset"]);
        Assert.Contains(section.Groups, g => g.Title == loc["EofLoss_PartialChordTremolo"]);
    }

    /// <summary>
    ///     Opened from an EOF export's Show (which marks the project), the Issues tab opens the section and
    ///     runs it; and a project this charter exports to EOF opens with it open next time, until they close it.
    /// </summary>
    [Fact]
    public async Task FromAnEofExport_TheSectionOpens_AndStaysOpenForThatProject()
    {
        var (h, _) = ChartEofApproximates();
        h.State.Document.EofRoundTripProject = h.Project;
        var report = Report(h);
        var runner = new HeldRunner();
        report.EofRoundTripModel.OffThread = runner.Run;

        report.RefreshChartReport();

        Assert.True(report.EofRoundTrip.IsExpanded);
        Assert.Single(runner.Runs);
        Assert.Same(h.Project, h.State.Document.EofRoundTripProject);

        var again = Report(h);
        again.RefreshChartReport();
        Assert.True(again.EofRoundTrip.IsExpanded);
        await again.EofRoundTripModel.Pending;
        Assert.NotEmpty(again.EofRoundTrip.Groups);

        again.EofRoundTrip.IsExpanded = false;
        Assert.Null(h.State.Document.EofRoundTripProject);
        var closed = Report(h);
        closed.RefreshChartReport();
        Assert.False(closed.EofRoundTrip.IsExpanded);
    }

    /// <summary>The Issues tab lists each arrangement's estimated difficulty (moved from the dialog), and the
    /// round trip's Go to switches to the arrangement and seeks.</summary>
    [Fact]
    public async Task TheIssuesTab_ListsTheArrangements_AndTheRoundTrip_AndGoToSwitchesArrangementAndSeeks()
    {
        var (h, rhythm) = ChartEofApproximates();
        var report = Report(h);
        var loc = FakeLocalization.English();

        report.RefreshChartReport();

        await report.ProjectReportPending;
        Assert.True(report.HasProjectArrangements);
        Assert.Equal(h.Project.Arrangements.Select(a => a.Name), report.ProjectArrangements.Select(r => r.Name));
        Assert.All(report.ProjectArrangements, r => Assert.NotEmpty(r.Difficulty));

        report.EofRoundTrip.IsExpanded = true;
        await report.EofRoundTripModel.Pending;

        var titles = report.EofRoundTrip.Groups.Select(g => g.Title).ToList();
        Assert.Contains(loc["EofLoss_CentOffset"], titles);
        var tremolo = Assert.Single(report.EofRoundTrip.Groups, g => g.Title == loc["EofLoss_PartialChordTremolo"]);
        var place = Assert.Single(tremolo.Items);
        Assert.Equal(("Rhythm", "M2.3 · 00:03.00"), (place.Subject, place.Where));

        place.JumpCommand.Execute(null);

        Assert.Same(rhythm, h.Editor.ActiveArrangement);
        Assert.Equal(3000, h.State.CurrentTimeMs);
    }
}
