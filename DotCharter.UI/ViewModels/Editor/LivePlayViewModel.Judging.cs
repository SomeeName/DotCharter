// LivePlayViewModel: judging a sound - the verdict handler, the detection-event to transport-time mapping, the detection log's formatting, and the stats publish.

using System.Diagnostics;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Judging a sound and reporting it: the verdict handler, the mapping from a
///     detection event's capture-stream position to transport milliseconds (the
///     one place capture time becomes chart time), the detection monitor's
///     formatting, and the stats publish.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>Worker-side judgment funnel: the lock-free consumers (timing
    /// collector, rock meter, 3D feedback queue) are fed directly; everything
    /// UI-bound (verdict labels, miss list, monitor lines) rides the snapshot.</summary>
    private void OnJudged(LivePlayEvent ev, TickUiState ui)
    {
        // Timing analytics ride the same funnel as feedback. Add() itself drops
        // misses (their DetectedAtMs is window expiry, not a real hit time).
        _timing.Add(ev);

        // Performance stakes ride the same funnel: every judged sound, misses
        // from silent stretches included. Streak is read AFTER the verdict.
        // The crowd HUD's meter rides the identical funnel (0.28 Band night).
        Meters.OnVerdict(ev.Verdict, _session.Stats?.Streak ?? 0, ev.DetectedAtMs);

        EnqueueFeedback(LivePlayFeedbackEvent.ForJudgment(ev));
        _stringConfidence.Record(ev);

        ui.HasVerdict = true;
        ui.LastWasHit = ev.Verdict != LivePlayVerdict.Miss;
        ui.LastFeedback = ev.Verdict switch
        {
            LivePlayVerdict.Hit => _localization["LivePlay_Hit"],
            LivePlayVerdict.HitWrongTechnique => _localization["LivePlay_HitWrongTechnique"],
            _ => _localization["LivePlay_Miss"]
        };

        SetTimingCue(ev.Direction, ui);

        if (!LoopActive && ev.Verdict == LivePlayVerdict.Miss)
        {
            ui.MissLines ??= new List<string>(4);
            ui.MissLines.Add($"{TimeLabel(ev.Sound.TimeMs)}  {ev.Sound.Label}");
        }

        if (ShowDetectionMonitor)
        {
            var tag = ev.Verdict switch
            {
                LivePlayVerdict.Hit => "HIT ",
                LivePlayVerdict.HitWrongTechnique => "HIT~",
                _ => "MISS"
            };
            // A miss shows the evidence the verifier DID find; a hit, its confidence.
            var confidence = ev.Verdict != LivePlayVerdict.Miss ? $"  conf {ev.Confidence:0.00}"
                : ev.MissEvidence is { } evidence ? $"  evid {evidence:0.00}" : string.Empty;
            AddDetectionEntry(ui,
                $"{TimeTag(ev.Sound.TimeMs)} {tag}  {ev.Sound.Label}{confidence}",
                ev.Verdict != LivePlayVerdict.Miss,
                ev.Verdict == LivePlayVerdict.Miss,
                LiveDetectionCertainty.IsUnsureMiss(ev));
        }
    }

    /// <summary>
    ///     Worker-side funnel for the late timing GRADE of a hit already credited and shown
    /// (CQT mode): the hit was counted — streak, meters, marker, "Hit" — by
    ///     <see cref="OnJudged" /> when the CQT confirmed it. This feeds what only the attack
    ///     onset could say: the timing collector (and through it the latency calibrator), the
    ///     tier flash and the EARLY/LATE cue. The tier counters ride the stats snapshot.
    /// A provisional grade is a cue only; a confirming one only collects.
    /// </summary>
    private void OnGraded(in LivePlayGrade grade, TickUiState ui)
    {
        if (grade.Stage != LivePlayGradeStage.Provisional) _timing.Add(grade.AsTimedHit());
        if (grade.ShowsCue)
        {
            EnqueueFeedback(LivePlayFeedbackEvent.ForGrade(grade));
            SetTimingCue(grade.Direction, ui);
        }

        if (ShowDetectionMonitor)
        {
            var delta = grade.AttackAtMs - grade.Sound.TimeMs;
            AddDetectionEntry(ui,
                $"{TimeTag(grade.Sound.TimeMs)} TIME  {grade.Sound.Label}  {grade.Tier} {delta:+0;-0;0} ms {grade.Stage}", isHit: true);
        }
    }

    /// <summary>The contour judge's ruling on a hit already shown. A NotMet is shown the
    /// way a palm mute played open is: the verdict label turns to "Hit (check technique)", the
    /// rock and crowd meters give back the hit's extra over a half hit, and the 3D highway
    /// flashes the wrong-technique marker. It is never a new judgment — the hit and the
    /// streak stand.</summary>
    private void OnTechniqueJudged(in LiveTechniqueJudgment check, double playheadMs, TickUiState ui)
    {
        if (check.Verdict == TechniqueContourVerdict.NotMet)
        {
            Meters.OnTechniqueDowngrade(check.StreakAtCredit, playheadMs);
            EnqueueFeedback(LivePlayFeedbackEvent.ForTechniqueDowngrade(check, playheadMs));
            ui.HasVerdict = true;
            ui.LastWasHit = true;
            ui.LastFeedback = _localization["LivePlay_HitWrongTechnique"];
        }

        if (ShowDetectionMonitor)
            AddDetectionEntry(ui, $"{TimeTag(check.Sound.TimeMs)} TECH  {check.Sound.Label}  {check.Verdict}", isHit: true);
    }

    private void EnqueueFeedback(LivePlayFeedbackEvent feedback)
    {
        // ConcurrentQueue: cross-thread-safe by construction — the render
        // driver drains it on the render thread (verified, Preview3DRenderDriver).
        FeedbackEvents.Enqueue(feedback);
        while (FeedbackEvents.Count > FeedbackQueueCap && FeedbackEvents.TryDequeue(out _)) { }
    }

    /// <summary>Only a hit that landed OUTSIDE the on-time band gets a timing label:
    /// an on-time hit needs no correction, and Direction is None wherever no real
    /// attack timestamp exists, so those stay blank rather than lying.</summary>
    private void SetTimingCue(LiveTimingDirection direction, TickUiState ui)
    {
        ui.HasTiming = true;
        ui.TimingEarly = direction == LiveTimingDirection.Early;
        ui.TimingLate = direction == LiveTimingDirection.Late;
        ui.TimingLabel = direction switch
        {
            LiveTimingDirection.Early => _localization["LivePlay_Early"],
            LiveTimingDirection.Late => _localization["LivePlay_Late"],
            _ => string.Empty
        };
    }

    /// <summary>Maps a detection event's capture-stream position to transport
    /// time using the engine's {captured samples ↔ host ticks} sync pair —
    /// both on the Stopwatch (QPC) timebase — falling back to the current
    /// playhead when no sync is available yet. The calibrated round-trip
    /// offset (hear → play → captured) is subtracted so the judged time is
    /// the chart time the player was actually reacting to.</summary>
    private double MapEventToTransportMs(in LiveDetectionEvent ev, double playheadMs)
    {
        if (TryGetEventHostTicks(ev, out var eventTicks))
        {
            var ageMs = (Stopwatch.GetTimestamp() - eventTicks) * 1000.0 / Stopwatch.Frequency;
            // The playhead we just read is "now" on the same wall clock; the
            // attack happened ageMs earlier. Sanity-cap: a stale sync (device
            // stopped) must not fling events far into the past.
            // ageMs and the latency offset are WALL-CLOCK; the playhead is TRANSPORT.
            // They only tick together at full speed — see LiveTimeMapping.
            if (ageMs is >= 0 and <= LiveTimeMapping.MaxEventAgeMs)
                return LiveTimeMapping.ToTransportMs(playheadMs, ageMs, _latencyOffsetMs, _audio.PlaybackSpeed);
        }

        return LiveTimeMapping.ToTransportMs(playheadMs, _latencyOffsetMs, _audio.PlaybackSpeed);
    }

    /// <summary>Transport time of a raw capture-stream position, using the same
    /// {captured samples ↔ host ticks} sync + latency offset as detection events.
    /// Used once per transport segment to anchor the CQT feed's axis. False
    /// before the first block or when the sync is stale.</summary>
    private bool TryMapStreamPosToTransportMs(ulong streamPos, double playheadMs, out double ms)
    {
        ms = LiveTimeMapping.ToTransportMs(playheadMs, _latencyOffsetMs, _audio.PlaybackSpeed);
        if (_analyzer is not { } analyzer ||
            !_audio.TryGetInputCaptureSync(out var capturedSamples, out var syncTicks) ||
            capturedSamples < streamPos)
        {
            return false;
        }

        var eventTicks = syncTicks - (long)((capturedSamples - streamPos) / (double)analyzer.SampleRate
                                             * Stopwatch.Frequency);
        var ageMs = (Stopwatch.GetTimestamp() - eventTicks) * 1000.0 / Stopwatch.Frequency;
        if (ageMs is >= 0 and <= LiveTimeMapping.MaxEventAgeMs)
        {
            ms = LiveTimeMapping.ToTransportMs(playheadMs, ageMs, _latencyOffsetMs, _audio.PlaybackSpeed);
            return true;
        }
        return false;
    }

    /// <summary>Host (Stopwatch) ticks of a detection event's attack, from
    /// the engine's capture/host clock sync. False before the first block.</summary>
    private bool TryGetEventHostTicks(in LiveDetectionEvent ev, out long eventTicks)
    {
        eventTicks = 0;
        if (_analyzer is not { } analyzer ||
            !_audio.TryGetInputCaptureSync(out var capturedSamples, out var syncTicks) ||
            capturedSamples < ev.StreamSamplePosition)
        {
            return false;
        }

        var deltaTicks = (capturedSamples - ev.StreamSamplePosition) / (double)analyzer.SampleRate
                         * Stopwatch.Frequency;
        eventTicks = syncTicks - (long)deltaTicks;
        return true;
    }

    private void LogEvent(in LiveDetectionEvent ev, double playheadMs, TickUiState ui)
    {
        var kind = ev.IsLegato ? "leg " : "note";
        var pitches = ev.Pitches.Length == 0 ? $"~{TopChroma(ev.Chroma)}" : PitchSet(ev.Pitches);
        AddDetectionEntry(ui,
            $"{TimeTag(playheadMs)} {kind}  {pitches,-15} {ev.TopFrequencyHz,7:0.0} Hz  " +
            $"cent {ev.SpectralCentroidHz,5:0}  rms {ev.Rms:0.0000}  decay {ev.DecayRatio:0.00}  {FlagText(ev.Techniques)}");
    }

    /// <summary>Up to three sufficiently salient candidates as "E3+B3+E4".</summary>
    private static string PitchSet(LivePitch[] pitches)
    {
        var names = new List<string>(3);
        foreach (var p in pitches)
        {
            // Weak candidates confuse more than they inform in the monitor;
            // the matcher ignores them too.
            if (p.Salience < 0.3f && names.Count > 0) continue;
            names.Add(MidiName(p.Midi));
            if (names.Count == 3) break;
        }

        return string.Join('+', names);
    }

    /// <summary>Queues a monitor line into the tick snapshot (chronological
    /// order; the UI apply inserts newest-first). Bounded at the log cap so a
    /// backed-up UI thread can never accumulate an unbounded pending list.</summary>
    private static void AddDetectionEntry(TickUiState ui, string text, bool isHit = false, bool isMiss = false,
        bool isUnsure = false)
    {
        ui.LogLines ??= new List<LiveDetectionEntry>(8);
        ui.LogLines.Add(new LiveDetectionEntry(text, isHit, isMiss, isUnsure));
        if (ui.LogLines.Count > DetectionLogCap) ui.LogLines.RemoveAt(0);
    }

    private static string TimeTag(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}]";
    }

    private static string TimeLabel(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static string MidiName(int midi)
        => $"{NoteNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";

    /// <summary>The strongest pitch classes of a chroma frame ("E A C#").</summary>
    private static string TopChroma(float[] chroma)
    {
        var max = 0f;
        foreach (var c in chroma)
        {
            if (c > max)
                max = c;
        }

        if (max <= 0f) return "-";

        Span<int> top = stackalloc int[3];
        var count = 0;
        for (var rank = 0; rank < 3; rank++)
        {
            var best = -1;
            var bestVal = 0f;
            for (var pc = 0; pc < 12; pc++)
            {
                var already = false;
                for (var t = 0; t < count; t++)
                {
                    if (top[t] == pc)
                        already = true;
                }

                if (already || chroma[pc] <= bestVal) continue;
                best = pc;
                bestVal = chroma[pc];
            }

            if (best < 0 || bestVal < max * 0.35f) break;
            top[count++] = best;
        }

        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = NoteNames[top[i]];
        return string.Join(' ', names);
    }

    private static string FlagText(LiveTechniqueFlags flags)
    {
        if (flags == LiveTechniqueFlags.None) return string.Empty;
        var parts = new List<string>(4);
        if (flags.HasFlag(LiveTechniqueFlags.PalmMute)) parts.Add("mute");
        if (flags.HasFlag(LiveTechniqueFlags.PinchHarmonic)) parts.Add("pinch");
        else if (flags.HasFlag(LiveTechniqueFlags.Harmonic)) parts.Add("harm");
        if (flags.HasFlag(LiveTechniqueFlags.Slap)) parts.Add("slap");
        if (flags.HasFlag(LiveTechniqueFlags.Pop)) parts.Add("pop");
        if (flags.HasFlag(LiveTechniqueFlags.Percussive) &&
            !flags.HasFlag(LiveTechniqueFlags.Slap) && !flags.HasFlag(LiveTechniqueFlags.Pop))
        {
            parts.Add("perc");
        }

        if (flags.HasFlag(LiveTechniqueFlags.Legato)) parts.Add("legato");
        return string.Join(' ', parts);
    }

    /// <summary>UI-THREAD direct stats publish, for the UI-side session
    /// operations that change stats synchronously (Start, loop apply, resume
    /// rewind). The worker never calls this — it uses <see cref="ComposeStats"/>
    /// and the coalesced snapshot.</summary>
    private void PublishStats()
    {
        var stats = _session.Stats;
        if (stats == null) return;
        Score = stats.Score;
        Streak = stats.Streak;
        BestStreak = stats.BestStreak;
        Multiplier = stats.Multiplier;
        Hits = stats.Hits;
        Misses = stats.Misses;
        TotalSounds = stats.TotalSounds;
        PerfectCount = stats.PerfectCount;
        GreatCount = stats.GreatCount;
        GoodCount = stats.GoodCount;
        AccuracyPercent = Math.Round(stats.Accuracy * 100.0, 1);
        Meters.Publish(_projectState.CurrentTimeMs);
        // Outside a resume-rewind replay this horizon always trails the playhead,
        // so the renderer's dead-note predicate is false and no mode state is needed.
        PublishResolvedThrough(_session.Matcher?.ResolvedThroughMs ?? double.NegativeInfinity);
        if (SectionsVersion != _session.SectionsVersion) SectionsVersion = _session.SectionsVersion;
    }

    /// <summary>Worker-side sibling of <see cref="PublishStats"/>: copies the
    /// session stats into the tick snapshot and publishes the render-thread
    /// horizon directly (it is lock-free and never touches bindings).</summary>
    private void ComposeStats(TickUiState ui, double playheadMs)
    {
        var stats = _session.Stats;
        if (stats != null)
        {
            ui.HasStats = true;
            ui.Score = stats.Score;
            ui.Streak = stats.Streak;
            ui.BestStreak = stats.BestStreak;
            ui.Multiplier = stats.Multiplier;
            ui.Hits = stats.Hits;
            ui.Misses = stats.Misses;
            ui.TotalSounds = stats.TotalSounds;
            ui.PerfectCount = stats.PerfectCount;
            ui.GreatCount = stats.GreatCount;
            ui.GoodCount = stats.GoodCount;
            ui.AccuracyPercent = Math.Round(stats.Accuracy * 100.0, 1);
            ui.SectionsVersion = _session.SectionsVersion;
        }

        if (Meters.TrySampleRockEnergy(playheadMs, out var energy))
        {
            ui.HasRockMeter = true;
            ui.RockEnergy = energy;
        }

        if (Meters.TrySampleCrowdValue(playheadMs, out var crowd))
        {
            ui.HasCrowdMeter = true;
            ui.CrowdValue = crowd;
        }

        PublishResolvedThrough(_session.Matcher?.ResolvedThroughMs ?? double.NegativeInfinity);
    }
}
