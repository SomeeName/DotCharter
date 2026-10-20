// LivePlayViewModel: TickUiState - the worker-to-UI payload, its forward MERGE for a coalesced channel, and the one place it is applied on the UI thread.

using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The worker-to-UI payload: <c>TickUiState</c>, the forward MERGE that lets
///     the coalescing channel drop intermediate payloads without losing list
///     items or edge flags, and <c>ApplyTickUiState</c> - the ONE place any of
///     it reaches an observable property, on the UI thread.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    // ---- worker → UI snapshot ------------------------------------------------

    /// <summary>
    ///     One tick's UI-facing output. Composed on the worker (under the gate),
    ///     marshaled through <see cref="CoalescedUiChannel{T}"/> — at most one
    ///     posted apply in flight; a snapshot that lands on a still-pending one
    ///     replaces it, with <see cref="LivePlayViewModel.MergeTickUiState"/>
    ///     folding the pending list payloads forward so no monitor line or miss
    ///     entry is lost while scalars keep only the newest value.
    /// </summary>
    private sealed class TickUiState
    {
        public int Epoch;

        // Session stats (PublishStats mirror). Valid ⇔ HasStats.
        public bool HasStats;
        public long Score;
        public int Streak, BestStreak, Multiplier, Hits, Misses, TotalSounds,
            PerfectCount, GreatCount, GoodCount;
        public double AccuracyPercent;
        public int SectionsVersion;

        // Rock meter (armed runs only). Valid ⇔ HasRockMeter.
        public bool HasRockMeter;
        public double RockEnergy;

        // Crowd meter HUD (Play Mode runs only). Valid ⇔ HasCrowdMeter.
        public bool HasCrowdMeter;
        public double CrowdValue;

        // Newest judged verdict of the tick. Valid ⇔ HasVerdict.
        public bool HasVerdict;
        public bool LastWasHit;
        public string LastFeedback = string.Empty;

        // Newest timing cue: a judgment's, or a late grade's (CQT mode, which
        // times a hit after it was shown). Valid ⇔ HasTiming.
        public bool HasTiming;
        public bool TimingEarly, TimingLate;
        public string TimingLabel = string.Empty;

        public bool HasCountIn;
        public int CountInRemaining;

        // Null = unchanged this tick.
        public bool? InputSilent, InputStarved;
        public string? TuningDriftHint;
        public string? PassSummary;
        public double? SpeedRequest;

        /// <summary>Pass-completed rebuild of the miss list (chronological).</summary>
        public List<string>? MissReset;

        /// <summary>Free-play misses to prepend (chronological).</summary>
        public List<string>? MissLines;

        /// <summary>Monitor lines to prepend (chronological, capped).</summary>
        public List<LiveDetectionEntry>? LogLines;

        /// <summary>The monitor's per-string confidence, sampled this tick (null = monitor hidden).</summary>
        public LiveStringConfidenceReading[]? StringConfidence;

        public bool SectionsChanged;

        // Calibration outcome — exempt from the epoch guard (a run boundary
        // does not invalidate a measured latency).
        public bool CalibrationFinished;
        public double? CalibrationResultMs;

        public bool HasContent =>
            HasStats || HasRockMeter || HasCrowdMeter || HasVerdict || HasTiming || HasCountIn ||
            InputSilent != null || InputStarved != null || TuningDriftHint != null ||
            PassSummary != null || SpeedRequest != null || MissReset != null ||
            MissLines != null || LogLines != null || StringConfidence != null || SectionsChanged ||
            CalibrationFinished;
    }

    /// <summary>Folds a still-pending snapshot into its replacement (producer
    /// side of the coalescing channel — single-threaded by contract).</summary>
    private static TickUiState MergeTickUiState(TickUiState old, TickUiState incoming)
    {
        if (old.Epoch != incoming.Epoch)
        {
            // Run boundary between the two: the old payload is stale, except a
            // calibration outcome, which must survive.
            if (old.CalibrationFinished && !incoming.CalibrationFinished)
            {
                incoming.CalibrationFinished = true;
                incoming.CalibrationResultMs = old.CalibrationResultMs;
            }

            return incoming;
        }

        // Scalars: the incoming (newer) value wins; carry the old one forward
        // only where the new tick produced none.
        if (!incoming.HasStats && old.HasStats)
        {
            incoming.HasStats = true;
            incoming.Score = old.Score;
            incoming.Streak = old.Streak;
            incoming.BestStreak = old.BestStreak;
            incoming.Multiplier = old.Multiplier;
            incoming.Hits = old.Hits;
            incoming.Misses = old.Misses;
            incoming.TotalSounds = old.TotalSounds;
            incoming.PerfectCount = old.PerfectCount;
            incoming.GreatCount = old.GreatCount;
            incoming.GoodCount = old.GoodCount;
            incoming.AccuracyPercent = old.AccuracyPercent;
            incoming.SectionsVersion = old.SectionsVersion;
        }

        if (!incoming.HasRockMeter && old.HasRockMeter)
        {
            incoming.HasRockMeter = true;
            incoming.RockEnergy = old.RockEnergy;
        }

        if (!incoming.HasCrowdMeter && old.HasCrowdMeter)
        {
            incoming.HasCrowdMeter = true;
            incoming.CrowdValue = old.CrowdValue;
        }

        if (!incoming.HasVerdict && old.HasVerdict)
        {
            incoming.HasVerdict = true;
            incoming.LastWasHit = old.LastWasHit;
            incoming.LastFeedback = old.LastFeedback;
        }

        if (!incoming.HasTiming && old.HasTiming)
        {
            incoming.HasTiming = true;
            incoming.TimingEarly = old.TimingEarly;
            incoming.TimingLate = old.TimingLate;
            incoming.TimingLabel = old.TimingLabel;
        }

        if (!incoming.HasCountIn && old.HasCountIn)
        {
            incoming.HasCountIn = true;
            incoming.CountInRemaining = old.CountInRemaining;
        }

        incoming.InputSilent ??= old.InputSilent;
        incoming.InputStarved ??= old.InputStarved;
        incoming.TuningDriftHint ??= old.TuningDriftHint;
        incoming.PassSummary ??= old.PassSummary;
        incoming.SpeedRequest ??= old.SpeedRequest;
        incoming.StringConfidence ??= old.StringConfidence;
        incoming.SectionsChanged |= old.SectionsChanged;

        if (old.CalibrationFinished && !incoming.CalibrationFinished)
        {
            incoming.CalibrationFinished = true;
            incoming.CalibrationResultMs = old.CalibrationResultMs;
        }

        // Lists: an incoming pass reset supersedes everything queued before it;
        // otherwise old items come first (both are chronological).
        if (incoming.MissReset == null)
        {
            incoming.MissReset = old.MissReset;
            if (old.MissLines != null)
            {
                if (incoming.MissLines != null) old.MissLines.AddRange(incoming.MissLines);
                incoming.MissLines = old.MissLines;
            }
        }

        if (old.LogLines != null)
        {
            if (incoming.LogLines != null) old.LogLines.AddRange(incoming.LogLines);
            incoming.LogLines = old.LogLines;
            // Keep only the newest cap's worth: everything beyond it would be
            // trimmed straight back out of the bounded DetectionLog anyway.
            if (incoming.LogLines.Count > DetectionLogCap)
                incoming.LogLines.RemoveRange(0, incoming.LogLines.Count - DetectionLogCap);
        }

        return incoming;
    }

    /// <summary>Applies a worker snapshot on the UI thread. Stale-epoch
    /// payloads (a Start/Stop landed after they were composed) are dropped —
    /// except the calibration outcome, which run boundaries don't invalidate.</summary>
    private void ApplyTickUiState(TickUiState ui)
    {
        if (ui.CalibrationFinished) ApplyCalibrationOutcome(ui.CalibrationResultMs);
        if (ui.Epoch != Volatile.Read(ref _uiEpoch)) return;

        if (ui.HasStats)
        {
            Score = ui.Score;
            Streak = ui.Streak;
            BestStreak = ui.BestStreak;
            Multiplier = ui.Multiplier;
            Hits = ui.Hits;
            Misses = ui.Misses;
            TotalSounds = ui.TotalSounds;
            PerfectCount = ui.PerfectCount;
            GreatCount = ui.GreatCount;
            GoodCount = ui.GoodCount;
            AccuracyPercent = ui.AccuracyPercent;
            if (SectionsVersion != ui.SectionsVersion) SectionsVersion = ui.SectionsVersion;
        }

        if (ui.HasRockMeter)
        {
            Meters.ApplyRockEnergy(ui.RockEnergy);
        }

        if (ui.HasCrowdMeter)
        {
            Meters.ApplyCrowdValue(ui.CrowdValue);
        }

        if (ui.HasVerdict)
        {
            LastWasHit = ui.LastWasHit;
            LastFeedback = ui.LastFeedback;
        }

        if (ui.HasTiming)
        {
            LastTimingIsEarly = ui.TimingEarly;
            LastTimingIsLate = ui.TimingLate;
            LastTimingLabel = ui.TimingLabel;
        }

        if (ui.HasCountIn) CountInRemaining = ui.CountInRemaining;
        if (ui.InputSilent is { } silent && IsInputSilent != silent) IsInputSilent = silent;
        if (ui.InputStarved is { } starved && IsInputStarved != starved) IsInputStarved = starved;
        if (ui.TuningDriftHint is { } hint && TuningDriftHint != hint) TuningDriftHint = hint;
        if (ui.PassSummary is { } pass) PassSummary = pass;

        if (ui.MissReset is { } reset)
        {
            MissedRecently.Clear();
            foreach (var line in reset) MissedRecently.Add(line);
        }

        if (ui.MissLines is { } misses)
        {
            foreach (var line in misses)
            {
                MissedRecently.Insert(0, line);
                while (MissedRecently.Count > MissListCap) MissedRecently.RemoveAt(MissedRecently.Count - 1);
            }
        }

        if (ui.LogLines is { } lines && ShowDetectionMonitor)
        {
            foreach (var entry in lines)
            {
                DetectionLog.Insert(0, entry);
                while (DetectionLog.Count > DetectionLogCap) DetectionLog.RemoveAt(DetectionLog.Count - 1);
            }
        }

        if (ui.StringConfidence is { } readings && ShowDetectionMonitor)
            StringConfidence.Apply(readings, LiveStringConfidenceViewModel.LabelsFor(_projectState.ActiveArrangement));

        if (ui.SectionsChanged) OnPropertyChanged(nameof(Sections));
        if (ui.SpeedRequest is { } speed) RequestSpeed(speed);
    }
}
