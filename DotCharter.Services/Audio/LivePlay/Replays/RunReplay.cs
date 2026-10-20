namespace DotCharter.Services.Audio.LivePlay.Replays;

/// <summary>One judged sound of a recorded run. Verdict/tier are stored as
/// STRINGS (the <c>TechniquePlayRecord.Technique</c> convention) so a newer
/// build's unknown value still loads — consumers parse tolerantly.</summary>
public sealed class RunReplayEvent
{
    public double TimeMs { get; set; }
    public int StringIndex { get; set; } = -1;
    public int Fret { get; set; }
    public string Verdict { get; set; } = "";

    /// <summary>Timing tier name; null for untimed judgments (misses,
    /// CQT-verified hits) — never "None" noise in the store.</summary>
    public string? Tier { get; set; }

    public bool IsChord { get; set; }
}

/// <summary>
///     v0.32 run replay: the judged event stream of one Play Mode run — the
///     matcher's resolved sounds ARE the replay (chart time + verdict + tier
///     per sound; the take recorder already captures the audio). Tiny (KBs),
///     schema-tolerant, versioned additively like the practice store.
/// </summary>
public sealed class RunReplay
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string SongKey { get; set; } = "";
    public string ArrangementName { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public double SpeedPercent { get; set; } = 100;
    public long Score { get; set; }
    public List<RunReplayEvent> Events { get; set; } = [];
}

/// <summary>Pure projection from the live matcher's post-run state.</summary>
public static class RunReplayCapture
{
    /// <summary>Builds the replay from the matcher's expected sounds —
    /// RESOLVED sounds only (an aborted run's unjudged tail carries no
    /// information). Pure; call under the session gate BEFORE the session
    /// stops, then persist off-thread.</summary>
    public static RunReplay FromExpected(IReadOnlyList<ExpectedSound> expected,
        string songKey, string arrangementName, double speedPercent, long score, DateTime recordedUtc)
    {
        var replay = new RunReplay
        {
            SongKey = songKey,
            ArrangementName = arrangementName,
            RecordedUtc = recordedUtc,
            SpeedPercent = speedPercent,
            Score = score
        };

        foreach (var sound in expected)
        {
            if (sound.Verdict is not { } verdict) continue;
            replay.Events.Add(new RunReplayEvent
            {
                TimeMs = sound.TimeMs,
                StringIndex = sound.StringIndex,
                Fret = sound.Fret,
                Verdict = verdict.ToString(),
                Tier = sound.TimingTier == LiveTimingTier.None ? null : sound.TimingTier.ToString(),
                IsChord = sound.IsChord
            });
        }

        return replay;
    }
}
