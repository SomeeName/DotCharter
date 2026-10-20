using DotCharter.Analysis.AudioToChart;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Infrastructure.Documents;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>#292: the Chart quality report projects the verdict model onto its screen, jumps to a
/// finding's arrangement, level and element, and folds the audio check in when asked. #690: the
/// checks run on a copy, on the injected off-thread runner (<see cref="ChartQualityReportOffThreadTests" />).</summary>
public sealed class ChartQualityReportViewModelTests
{
    private sealed class SongLoaded : FakeAudioPlaybackService, IPlaybackTransport
    {
        string IPlaybackTransport.CurrentAudioPath => "song.ogg";
    }

    private sealed class FakeAudioLint(Func<Arrangement, AudioChartLintOutcome> outcome) : IAudioChartLintService
    {
        public Task<AudioChartLintOutcome> LintAsync(Arrangement arrangement, IList<BeatMarker> beats, string? audioPath,
            CancellationToken cancellationToken = default) => Task.FromResult(outcome(arrangement));
    }

    /// <summary>The checks the report runs, on <paramref name="runner" />, copied after <paramref name="afterPaint" />;
    /// by default both run where they are called.</summary>
    internal static QualityReportChecks Checks(Func<Func<QualityReportCheck>, Task<QualityReportCheck>>? runner = null,
        Action<Action>? afterPaint = null) =>
        new(new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())), new TestUiDispatcher())
        {
            OffThread = runner ?? (work => Task.FromResult(work())),
            AfterPaint = afterPaint ?? (copy => copy())
        };

    internal static ChartQualityReportViewModel Open(ChartEditHarness h, IAudioChartLintService? lint = null,
        IPlaybackTransport? playback = null, QualityReportChecks? checks = null,
        IChartMaintenanceService? maintenance = null, IProjectDocumentService? documents = null) =>
        new(checks ?? Checks(), h.Services,
            RecordingProxy.Create<IDialogs>().Proxy, maintenance ?? RecordingProxy.Create<IChartMaintenanceService>().Proxy,
            documents ?? new CheckpointsViewModelTests.FakeDocumentService(), h.Transport,
            new QualityAudioCheck(lint ?? new FakeAudioLint(_ => AudioChartLintOutcome.Failed("none")),
                playback ?? new FakeAudioPlaybackService()));

    /// <summary>The harness chart with two levels and a fret the game cannot play on the hard one.</summary>
    internal static (ChartEditHarness H, ChartLevel Hard, GuitarNote Bad) ChartWithABadFret()
    {
        var h = new ChartEditHarness();
        var arrangement = h.Project.Arrangements[0];
        var hard = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 4; beat < 12; beat++)
            hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 1, Fret = 5 });
        var bad = hard.Notes[3];
        bad.Fret = 25;
        arrangement.Levels.Add(hard);
        return (h, hard, bad);
    }

    [Fact]
    public void AChartTheExportRefuses_IsNotReady_AndListsTheBlockersFirst()
    {
        var (h, _, _) = ChartWithABadFret();

        IChartQualityReportScreen screen = Open(h);

        Assert.False(screen.IsReady);
        Assert.True(screen.Groups[0].IsBlocking);
        Assert.Contains(screen.Groups[0].Items, f => f.Message.Contains("25", StringComparison.Ordinal));
        Assert.StartsWith(screen.Groups[0].Items.Count == 1 ? "1 thing" : $"{screen.Groups[0].Items.Count} things",
            screen.Verdict, StringComparison.Ordinal);
        Assert.Equal("Lead", Assert.Single(screen.Arrangements).Name);
    }

    /// <summary>
    ///     #397: on a chart with a difficulty ladder the easier levels repeat the hardest level's
    ///     findings. They are listed on request and counted either way; one that stops the export
    ///     always shows, so the verdict and the blocking list stay true.
    /// </summary>
    [Fact]
    public void FindingsOnEasierLevels_AreListedOnRequest_ButWhatStopsTheExportAlwaysShows()
    {
        var (h, _, _) = ChartWithABadFret();
        var easy = h.Project.Arrangements[0].Levels[0];
        easy.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 0,
            BendValues = { new BendValue(new FractionalPosition(2), 1.0) }
        });
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 3, Fret = 25 });

        IChartQualityReportScreen screen = Open(h);
        List<IQualityFinding> Listed() => [.. screen.Groups.SelectMany(g => g.Items)];

        Assert.False(screen.ShowEasierLevels);
        Assert.True(screen.HasEasierLevelFindings);
        Assert.EndsWith("on easier levels", screen.EasierLevelsNote, StringComparison.Ordinal);
        Assert.DoesNotContain(Listed(), f => f.Message.Contains("played open", StringComparison.Ordinal));
        Assert.Contains(Listed(), f => f.Location.Contains("level 0", StringComparison.Ordinal)
                                       && f.Message.Contains("25", StringComparison.Ordinal));

        screen.ShowEasierLevels = true;

        Assert.Contains(Listed(), f => f.Message.Contains("played open", StringComparison.Ordinal));
    }

    [Fact]
    public void GoTo_SwitchesToTheFindingsLevel_SeeksAndSelectsItsNote()
    {
        var (h, hard, bad) = ChartWithABadFret();
        h.State.ActiveLevel = h.Project.Arrangements[0].Levels[0];
        IChartQualityReportScreen screen = Open(h);
        var row = screen.Groups.SelectMany(g => g.Items).First(f => f.Message.Contains("25", StringComparison.Ordinal));

        row.JumpCommand.Execute(null);

        Assert.Same(hard, h.State.ActiveLevel);
        Assert.Contains(bad, h.Editor.Selection.SelectedNotes);
        Assert.Contains("level 1", row.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingAgainstTheAudio_AddsItsDoubts_AndSaysHowManySoundsItConfirmed()
    {
        var (h, _, _) = ChartWithABadFret();
        var lint = new FakeAudioLint(_ => new AudioChartLintOutcome(true, null, 8, 7, [new ChartAudioFinding(2_000, 45, 0.9f, 47, 0.8f)]));
        var screen = Open(h, lint, new SongLoaded());
        Assert.True(screen.CanVerifyAudio);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)screen.VerifyAudioCommand).ExecuteAsync(null);

        Assert.Equal("7 of 8 sounds confirmed by the audio.", screen.AudioStatus);
        Assert.Contains(screen.Groups.SelectMany(g => g.Items), f => f.Source == "Audio" && f.Message.Contains("A2", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoSongLoaded_TheAudioCheckCannotRun_AndSaysWhy()
    {
        var (h, _, _) = ChartWithABadFret();

        IChartQualityReportScreen screen = Open(h);

        Assert.False(screen.CanVerifyAudio);
        Assert.Equal("No song audio is loaded, so nothing can be checked against it.", screen.AudioStatus);
    }

    [Fact]
    public void WithNoChartOpen_TheVerdictSaysSo()
    {
        var h = new ChartEditHarness();
        h.State.ActiveProject = null;

        IChartQualityReportScreen screen = Open(h);

        Assert.Equal("No chart is open.", screen.Verdict);
        Assert.False(screen.HasFindings);
    }
}
