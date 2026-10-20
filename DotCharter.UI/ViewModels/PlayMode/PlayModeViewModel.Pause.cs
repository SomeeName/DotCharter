// PlayModeViewModel: the pause overlay's header figures - the PausedRunFacts snapshot taken once per pause.
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Shared;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The PAUSE overlay's header figures (<c>Play Overlays.dc.html</c>).
///
///     <para>
///         The mockup adds run state to the pause header — accuracy, streak,
///         score, the section you are in and how far through you are — for a
///         plain reason: the old menu told you the song's name, which you knew,
///         and nothing about how the run was going, which is the only question
///         you have while you are looking at it.
///     </para>
///
///     <para>
///         <b>It is a SNAPSHOT, taken once in <see cref="PauseGameplay" />, not
///         a set of live bindings.</b> The run is stopped, so nothing behind
///         these numbers can move while the overlay is up; binding them live
///         would mean keeping the judging view model's change notifications
///         flowing into a surface that by definition cannot change, and would
///         put the 250 Hz playhead behind a menu. A snapshot is also what makes
///         the figures honest after a resume-and-re-pause: they are re-taken.
///     </para>
///
///     <para>
///         <b>Nothing here is invented.</b> Every field comes off the live
///         judging session or the transport; a run with no judging (free play
///         through the highway) produces <see cref="PausedRunFacts.Empty" /> and
///         the header row simply does not render, rather than printing 0 % of a
///         run nobody scored.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    /// <summary>The pause header's figures, re-taken on every pause.</summary>
    [ObservableProperty] private PausedRunFacts _pausedRun = PausedRunFacts.Empty;

    /// <summary>
    ///     Reads the current run into <see cref="PausedRun" />. Called from
    ///     <c>PauseGameplay</c> AFTER the transport has stopped, so the elapsed
    ///     time is the frame the player is looking at.
    /// </summary>
    private void CapturePausedRun()
    {
        if (_livePlay is not { IsActive: true } live)
        {
            PausedRun = PausedRunFacts.Empty;
            return;
        }

        var nowMs = _projectState?.CurrentTimeMs ?? 0;

        // The section the playhead is inside. Sections are ordered and
        // half-open; a run before the first marker is simply "no section",
        // which prints nothing rather than guessing "intro".
        var section = "";
        foreach (var s in live.Sections)
        {
            if (nowMs >= s.StartMs && nowMs < s.EndMs)
            {
                section = s.Name;
                break;
            }
        }

        PausedRun = new PausedRunFacts
        {
            AccuracyLabel = PercentText.Format(live.AccuracyPercent / 100.0, 1),
            StreakLabel = live.Streak.ToString("N0"),
            ScoreLabel = live.Score.ToString("N0"),
            SectionLabel = section,
            ElapsedLabel = FormatClock(nowMs),
            TotalLabel = FormatClock(_projectState?.TotalAudioLengthMs ?? 0),
            HasRun = true
        };
    }

    /// <summary>m:ss, the way every other run readout in the app prints
    /// time.</summary>
    private static string FormatClock(double ms)
    {
        if (ms <= 0) return "0:00";
        var total = (int)Math.Round(ms / 1000.0);
        return $"{total / 60}:{total % 60:00}";
    }

    // ── the COUNT-IN's beat pips ─────────────────────────────────────────────

    /// <summary>How many steps the count-in has. It is the value
    /// <c>BeginCountdown</c> seeds, and the pips are drawn from the SAME number
    /// — a hard-coded four pips under a three-step count is the kind of
    /// decoration that survives a change to the count and then lies.</summary>
    public const int CountdownSteps = 3;

    /// <summary>
    ///     The mockup's row of beat pips under the count. One per step, lit as
    ///     the count passes it, so the row answers "how much longer" without
    ///     reading the digit.
    /// </summary>
    public IReadOnlyList<CountInPip> CountdownPips => BuildCountInPips(CountdownValue);

    /// <summary>Pure, so the lighting rule is testable without a countdown
    /// running. Step <c>i</c> (1-based) is lit once the counter has reached
    /// it — at 3 only the first is lit, at 1 all three are.</summary>
    internal static IReadOnlyList<CountInPip> BuildCountInPips(int countdownValue)
    {
        var pips = new CountInPip[CountdownSteps];
        for (var i = 0; i < CountdownSteps; i++)
            pips[i] = new CountInPip(countdownValue <= CountdownSteps - i);
        return pips;
    }

    partial void OnCountdownValueChanged(int value) => OnPropertyChanged(nameof(CountdownPips));

    /// <summary>
    ///     Abandons a count-in before the run starts, returning to the pre-play
    ///     screen the player came from. Reached by Esc through
    ///     <c>TryCloseOverlay</c>.
    ///     <para>
    ///         It stops the TIMER first. Without that the tick that was already
    ///         scheduled would call <c>BeginGameplay</c> after the screen had
    ///         moved on, starting a run behind the song-select list.
    ///     </para>
    /// </summary>
    private void CancelCountdown()
    {
        if (CurrentScreen != PlayModeScreen.Countdown) return;
        _countdownTimer.Stop();
        _pendingRunStartOverrideMs = null;
        // …and it unwinds the RUN CHAIN. A countdown is not always the first
        // thing that happens: Retry (results), Restart (pause) and
        // retry-from-section all count down while the PREVIOUS run's practice
        // speed, highway scroll speed, drop-tuner shift, backing-track mutes
        // and in-game rig are still applied. Abandoning here left every one of
        // them on, on a pre-play screen the player is free to walk away from.
        EndRunSideEffects();
        CurrentScreen = SelectedSong != null ? PlayModeScreen.TuningCheck : PlayModeScreen.SongSelect;
    }
}
