// LivePlayViewModel: the seam to the tuning-drift monitor and the hint it raises, plus the end-of-pass summary and the speed request it can raise.

using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The tuning-drift hint and the seam to <see cref="LiveTuningDriftMonitor" />,
///     which owns the watchdog, the pitch engine and the 4 Hz cadence: this file
///     only fills the window it asks for from the non-destructive capture tap and
///     carries the hint it answers on the tick snapshot. The end-of-pass summary
///     and the playback-speed request it can produce sit here too - the speed
///     ladder's only writer.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>"You've drifted flat — check your tuning." Empty while the
    /// verdict is Stable or there is not enough evidence yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTuningDrift))]
    private string _tuningDriftHint = "";

    public bool HasTuningDrift => TuningDriftHint.Length > 0;

    private void ResetTuningDrift(Arrangement? arrangement)
    {
        _tuningDrift.Reset(arrangement?.CentOffset ?? 0.0);
        TuningDriftHint = "";
    }

    private void UpdateTuningDrift(bool isPlaying, TickUiState ui)
    {
        if (!isPlaying)
        {
            return;
        }

        if (_tuningDrift.BeginCheck(Environment.TickCount64, _audio.InputSampleRate) is not { } window)
        {
            return;
        }

        // Non-destructive windowed read: any number of these can coexist with the
        // single streaming detection consumer above, so this never starves it.
        if (_tuningDrift.CompleteCheck(_audio.ReadInputCaptureWindow(window)) is { } hint)
        {
            ui.TuningDriftHint = hint;
        }
    }

    private void OnPassCompleted(LivePlayTickResult tick, double playheadMs, TickUiState ui)
    {
        ui.PassSummary = string.Format(_localization["LivePlay_PassSummary"],
            _session.PassCount, Math.Round(tick.PassAccuracy * 100.0, 1));

        var reset = new List<string>(Math.Min(MissListCap, _session.LastPassMisses.Count));
        foreach (var miss in _session.LastPassMisses.Take(MissListCap))
            reset.Add($"{TimeLabel(miss.TimeMs)}  {miss.Label}");
        ui.MissReset = reset;
        ui.MissLines = null; // the reset supersedes anything queued this tick

        if (ShowDetectionMonitor)
        {
            AddDetectionEntry(ui,
                $"{TimeTag(playheadMs)} PASS {_session.PassCount}  " +
                $"{tick.PassAccuracy * 100.0:0.0}%  judged {tick.PassJudged}" +
                (tick.SpeedChange is { } s ? $"  -> speed {s * 100:0}%" : string.Empty));
        }
    }

    private void RequestSpeed(double speed)
    {
        CurrentSpeedPercent = (int)Math.Round(speed * 100);
        var handler = PlaybackSpeedChangeRequested;
        if (handler != null) handler(speed);
        else _audio.PlaybackSpeed = speed;
    }
}
