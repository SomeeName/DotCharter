using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The nine Play Mode flow redesigns' fixtures cannot quietly decay into
///     empty ones.
///     <para>
///         This is the cheap half of their evidence and it needs no Avalonia
///         session, so it runs in the DEFAULT gate. It exists because every one
///         of these screens is normally reviewed in the ONE state that shows the
///         least: a run that has not started, a profile with no history, a
///         co-op roster with two clean seats, a chart with no sections. A
///         capture harness pointed at those is green and useless. If someone
///         trims a populated variant, this fails instead of the pictures quietly
///         getting easier.
///     </para>
/// </summary>
public sealed class PlayModeFlowFakesTests
{
    [Fact]
    public void Start_HasRealProfilesAndThreeStatusFacts_AndAFirstRunThatIsGenuinelyEmpty()
    {
        var ready = FakeStartScreen.Ready();
        Assert.True(ready.Profiles.Count >= 3,
            $"Ready() offers {ready.Profiles.Count} profile(s) — the switcher would never wrap");
        Assert.True(ready.HasProfiles);
        Assert.True(ready.CanStart);
        Assert.Equal(1, ready.Profiles.Count(p => p.IsActive));
        Assert.Equal(3, ready.StatusFacts.Count);
        Assert.All(ready.StatusFacts, f => Assert.NotEqual("", f.Value));
        Assert.Contains(ready.Profiles, p => p.IsLefty);
        Assert.False(ready.IsGuestEntryOpen);

        var first = FakeStartScreen.FirstRun();
        Assert.False(first.HasProfiles);
        Assert.False(first.CanStart);
        Assert.True(first.IsGuestEntryOpen, "the first-run door has no way in at all");
        Assert.False(first.CanStartGuestSession);
        Assert.All(first.StatusFacts, f => Assert.True(f.IsWarning));

        // Picking a profile republishes the name over START and takes the
        // handedness with it.
        var second = ready.Profiles[1];
        second.SelectCommand.Execute(null);
        Assert.True(second.IsActive);
        Assert.Equal(second.Name, ready.ActiveProfileName);
        Assert.Equal(second.IsLefty, ready.IsLefty);
    }

    [Fact]
    public void TuningCheck_HasSixStringsInThreeStatesAtOnce_AndACaseThatCannotCheck()
    {
        var needs = FakeTuningCheckScreen.NeedsTuning();
        Assert.Equal(6, needs.Strings.Count);
        Assert.True(needs.HasTunerCheck);
        Assert.False(needs.AllVerified);
        Assert.Contains(needs.Strings, s => s.IsOff);
        Assert.Contains(needs.Strings, s => s.IsAwaiting);
        Assert.Contains(needs.Strings, s => s.IsVerified);
        Assert.All(needs.Strings, s => Assert.InRange(s.NeedlePercent, 0, 100));
        Assert.All(needs.Strings, s => Assert.NotEqual("", s.StateText));
        Assert.True(needs.Paths.Count >= 3);
        Assert.NotNull(needs.SelectedPath);
        Assert.True(needs.CanStart);
        Assert.False(needs.HasBest);

        var ready = FakeTuningCheckScreen.Ready();
        Assert.True(ready.AllVerified);
        Assert.True(ready.HasBest);
        Assert.Contains("6", ready.VerifiedLabel, StringComparison.Ordinal);
        Assert.Contains(ready.Paths, p => p.HasRecord);
        Assert.Contains(ready.Paths, p => !p.HasRecord);

        var none = FakeTuningCheckScreen.NoInput();
        Assert.False(none.IsInputOpen);
        Assert.False(none.HasTunerCheck,
            "the tuner reads as live with no input open — a capability the app cannot honour");
        Assert.Empty(none.Strings);
        Assert.True(none.CanStart, "a run cannot be started just because the tuner is unavailable");

        // Skip verifies everything; restart puts it back.
        needs.SkipTunerCommand.Execute(null);
        Assert.True(needs.AllVerified);
        needs.RestartTunerCommand.Execute(null);
        Assert.False(needs.AllVerified);
    }

    /// <summary>
    ///     Picking a path that is ALREADY picked still republishes — the Song
    ///     Select rail's lesson, because the click has already flipped the
    ///     visual by the time the command runs.
    /// </summary>
    [Fact]
    public void TuningCheck_PickingTheSamePathTwice_StillRepublishesIt()
    {
        var screen = FakeTuningCheckScreen.Ready();
        var tile = screen.Paths[0];

        var republished = 0;
        screen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ITuningCheckScreen.SelectedPath)) republished++;
        };

        tile.SelectCommand.Execute(null);
        tile.SelectCommand.Execute(null);

        Assert.Equal(2, republished);
        Assert.Same(tile, screen.SelectedPath);
        Assert.True(tile.IsSelected);
    }

    [Fact]
    public void Countdown_CountsFourBeats_AndCarriesTheLockup()
    {
        var three = FakeCountdownScreen.ThreeToGo();
        Assert.Equal(4, three.Pips.Count);
        Assert.Equal(1, three.Pips.Count(p => p.IsLit));
        Assert.False(three.IsGo);
        Assert.NotEqual("", three.SongTitle);
        Assert.NotEqual("", three.ArrangementName);
        Assert.NotEqual("", three.TuningLabel);
        Assert.True(three.HasArtist);

        var go = FakeCountdownScreen.Go();
        Assert.True(go.IsGo);
        Assert.Equal(4, go.Pips.Count(p => p.IsLit));

        var stress = FakeCountdownScreen.LongText();
        Assert.True(stress.SongTitle.Length > 30);
        Assert.True(stress.Artist.Length > 40);
        Assert.True(stress.TuningLabel.Length > 20);
    }

    [Fact]
    public void Playing_HasSoloStrugglingAndExtremeVariants()
    {
        var solo = FakePlayingScreen.Solo();
        Assert.True(solo.HasSection);
        Assert.InRange(solo.SectionProgress, 0, 1);
        Assert.InRange(solo.SongProgress, 0, 1);
        Assert.InRange(solo.EnergyLevel, 0, 1);
        Assert.False(solo.IsEnergyLow);
        foreach (var label in new[]
                 {
                     solo.ScoreLabel, solo.MultiplierLabel, solo.AccuracyLabel, solo.StreakLabel,
                     solo.HitsLabel, solo.MissesLabel, solo.ElapsedLabel, solo.TotalLabel
                 })
        {
            Assert.NotEqual("", label);
        }

        var struggling = FakePlayingScreen.Struggling();
        Assert.False(struggling.HasSection, "the no-sections case still carries a section");
        Assert.True(struggling.IsEnergyLow);

        var extreme = FakePlayingScreen.Extreme();
        Assert.True(extreme.ScoreLabel.Length >= 10, "the stress score is not wide enough to break a cell");
        Assert.True(extreme.SongTitle.Length > 60);
    }

    [Fact]
    public void Paused_HidesItsFiguresBeforeTheFirstJudgedNote_AndAlwaysOffersFiveTools()
    {
        var mid = FakePausedScreen.MidRun();
        Assert.True(mid.HasRun);
        Assert.True(mid.HasSection);
        // Tuner, riff repeater, settings, Amp & FX, pedalboard: the live pause
        // menu's tools (the practice plan went with the 2026-09-19 cut).
        Assert.Equal(5, mid.Tools.Count);
        Assert.All(mid.Tools, t => Assert.NotEqual("", t.Caption));
        Assert.InRange(mid.Progress, 0, 1);

        var fresh = FakePausedScreen.NotStarted();
        Assert.False(fresh.HasRun, "a run that has not started reads as a bad one");
        Assert.Equal("", fresh.ScoreLabel);
        Assert.False(fresh.HasSection);
        Assert.Equal(5, fresh.Tools.Count);

        var stress = FakePausedScreen.LongText();
        Assert.True(stress.SongTitle.Length > 60);
        Assert.True(stress.SectionLabel.Length > 10);
        Assert.Equal(5, stress.Tools.Count);
        Assert.Contains(stress.Tools, t => t.Caption.Length > 60);
    }

    /// <summary>
    ///     <c>Empty</c> is genuinely empty and <c>Full</c> is a list at its realistic maximum
    ///     (target-architecture.md § 3), on every flow screen that has one. A variant that
    ///     decays into a copy of <c>Default</c> fails here before the captures go quietly easy.
    /// </summary>
    [Fact]
    public void EmptyAndFull_AreTheEndsOfTheRange_NotCopiesOfDefault()
    {
        var silent = FakeCountdownScreen.Empty();
        Assert.Empty(silent.Pips);
        Assert.False(silent.HasArtist);
        Assert.Equal(8, FakeCountdownScreen.Full().Pips.Count);

        var first = FakePlayingScreen.Empty();
        Assert.False(first.HasSection);
        Assert.Equal(0, first.SongProgress);
        Assert.False(first.IsEnergyLow, "the first frame of a run reads as a failing one");

        var nothing = FakeTuningCheckScreen.Empty();
        Assert.Empty(nothing.Paths);
        Assert.Empty(nothing.Strings);
        Assert.Null(nothing.SelectedPath);
        Assert.False(nothing.HasTunerCheck, "the tuner reads as live with no path to check");
        Assert.False(nothing.CanStart, "a run can start with no path to play");

        var most = FakeTuningCheckScreen.Full();
        Assert.Equal(8, most.Paths.Count);
        Assert.Equal(7, most.Strings.Count);
        Assert.Contains(most.Strings, s => s.IsOff);
        Assert.Contains(most.Strings, s => s.IsAwaiting);
        Assert.Contains(most.Strings, s => s.IsVerified);

        var household = FakeStartScreen.Full();
        Assert.Equal(12, household.Profiles.Count);
        Assert.Equal(1, household.Profiles.Count(p => p.IsActive));
        Assert.True(household.CanStart);

        Assert.Equal(7, FakePausedScreen.Full().Tools.Count);

        var board = FakeResultsScreen.Full();
        Assert.Equal(32, board.Sections.Count);
        Assert.Contains(board.Sections, s => s.IsWeak);
        Assert.Contains(board.Sections, s => s.IsFair);
        Assert.Contains(board.Sections, s => s.IsStrong);
        Assert.Equal(12, board.Leaderboard.Count);
        Assert.Equal(1, board.Leaderboard.Count(r => r.IsActiveProfile));
        Assert.All(board.Sections, s => Assert.InRange(s.Accuracy, 0, 1));
    }

    [Fact]
    public void Failed_SaysHowFarYouGot_AndDropsTheSectionVerbWhenThereIsNone()
    {
        var withSection = FakeFailedScreen.WithSection();
        Assert.True(withSection.HasSection);
        Assert.NotEqual("", withSection.ProgressLabel);
        Assert.NotEqual("", withSection.AccuracyLabel);
        Assert.InRange(withSection.Progress, 0.01, 0.99);

        var none = FakeFailedScreen.NoSection();
        Assert.False(none.HasSection, "a chart with no sections still offers a section to retry");
        Assert.NotEqual("", none.ProgressLabel);

        var stress = FakeFailedScreen.LongText();
        Assert.True(stress.SongTitle.Length > 60);
        Assert.True(stress.SectionLabel.Length > 10);
        Assert.True(stress.ArrangementName.Length > 20);
    }

    [Fact]
    public void Results_HasAFullBoardAndOneWithAllThreePanelsEmpty()
    {
        var strong = FakeResultsScreen.Strong();
        Assert.True(strong.HasSections);
        Assert.True(strong.Sections.Count >= 8, "the breakdown never scrolls");
        Assert.True(strong.HasTiming);
        Assert.True(strong.TimingBars.Count >= 9);
        Assert.All(strong.TimingBars, b => Assert.InRange(b.BarHeight, 1, 96));
        Assert.Equal(1, strong.TimingBars.Count(b => b.IsCentre));
        Assert.True(strong.HasLeaderboard);
        Assert.Equal(1, strong.Leaderboard.Count(r => r.IsActiveProfile));
        Assert.True(strong.IsFullCombo);
        Assert.True(strong.IsNewRecord);
        Assert.False(strong.CanPractice, "a clean run still offers a weak section to practise");

        var weak = FakeResultsScreen.Weak();
        Assert.True(weak.CanPractice);
        Assert.True(weak.Sections.Count(s => s.IsWeak) >= 3);
        Assert.Contains(weak.Sections, s => s.IsFair);
        Assert.Contains(weak.Sections, s => s.IsStrong);
        Assert.All(weak.Sections, s => Assert.InRange(s.Accuracy, 0, 1));
        Assert.All(weak.Sections, s => Assert.True(s.IsWeak ^ s.IsFair ^ s.IsStrong,
            $"'{s.Name}' is in {(s.IsWeak ? 1 : 0) + (s.IsFair ? 1 : 0) + (s.IsStrong ? 1 : 0)} bands"));

        var first = FakeResultsScreen.FirstRun();
        Assert.False(first.HasSections);
        Assert.False(first.HasTiming);
        Assert.False(first.HasLeaderboard);
        Assert.False(first.CanListenBack);
        Assert.False(first.CanPractice);
        Assert.NotEqual("", first.GradeLabel);

        var stress = FakeResultsScreen.LongText();
        Assert.Contains(stress.Sections, s => s.Name.Length > 50);
        Assert.Contains(stress.Leaderboard, r => r.ProfileName.Length > 40);
        Assert.True(stress.ComparisonLabel.Length > 80);

        // The one verb that moves.
        Assert.False(weak.IsTakePlaying);
        weak.ListenBackCommand.Execute(null);
        Assert.True(weak.IsTakePlaying);
        weak.ListenBackCommand.Execute(null);
        Assert.False(weak.IsTakePlaying);
    }
}
