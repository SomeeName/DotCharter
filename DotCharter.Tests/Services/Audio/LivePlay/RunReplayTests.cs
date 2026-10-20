using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Replays;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     v0.32 run replays: the capture projection (resolved sounds only,
///     tier None folds to null), the schema-tolerant store round-trip, and
///     the newest-session attach mutator.
/// </summary>
public sealed class RunReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private ReplayStore NewStore() => new(_dir);

    // ---- capture ------------------------------------------------------------

    [Fact]
    public void Capture_TakesResolvedSoundsOnly_AndFoldsNoneTier()
    {
        var sounds = new List<ExpectedSound>
        {
            new() { TimeMs = 1000, StringIndex = 0, Fret = 5 },   // unjudged — excluded
            new() { TimeMs = 2000, StringIndex = 1, Fret = 3, IsChord = false },
            new() { TimeMs = 3000, StringIndex = 2, Fret = 7 }
        };
        sounds[1].Verdict = LivePlayVerdict.Hit;
        sounds[1].TimingTier = LiveTimingTier.Perfect;
        sounds[2].Verdict = LivePlayVerdict.Miss;
        sounds[2].TimingTier = LiveTimingTier.None;

        var replay = RunReplayCapture.FromExpected(sounds, "song", "Lead:lead", 90, 4200,
            new DateTime(2026, 7, 18, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, replay.Events.Count);
        Assert.Equal("Hit", replay.Events[0].Verdict);
        Assert.Equal("Perfect", replay.Events[0].Tier);
        Assert.Equal("Miss", replay.Events[1].Verdict);
        Assert.Null(replay.Events[1].Tier); // None never persists as noise
        Assert.Equal(90, replay.SpeedPercent, 9);
        Assert.Equal(4200, replay.Score);
    }

    // ---- store --------------------------------------------------------------

    [Fact]
    public void Store_RoundTrips()
    {
        var replay = RunReplayCapture.FromExpected(
            [Judged(1500, LivePlayVerdict.Hit, LiveTimingTier.Great)],
            "song", "Lead:lead", 100, 100, DateTime.UtcNow);

        var name = NewStore().Save(replay);
        Assert.NotNull(name);

        var loaded = NewStore().TryLoad(name);
        Assert.NotNull(loaded);
        Assert.Equal(RunReplay.CurrentVersion, loaded.Version);
        var evt = Assert.Single(loaded.Events);
        Assert.Equal(1500, evt.TimeMs, 3);
        Assert.Equal("Hit", evt.Verdict);
        Assert.Equal("Great", evt.Tier);
    }

    [Fact]
    public void Load_IsTolerant_UnknownFieldsAndVerdicts_AndGarbage()
    {
        Directory.CreateDirectory(_dir);
        // A FUTURE build's replay: higher version, unknown verdict, extra fields.
        File.WriteAllText(Path.Combine(_dir, "future.json"), """
            { "Version": 99, "SongKey": "s", "ArrangementName": "a",
              "SpeedPercent": 100, "Score": 10, "FutureField": { "x": 1 },
              "Events": [ { "TimeMs": 500, "Verdict": "HoloHit", "FutureFlag": true } ] }
            """);
        File.WriteAllText(Path.Combine(_dir, "garbage.json"), "{ not json");

        var future = NewStore().TryLoad("future.json");
        Assert.NotNull(future);
        Assert.Equal("HoloHit", Assert.Single(future.Events).Verdict); // kept as string

        Assert.Null(NewStore().TryLoad("garbage.json"));
        Assert.Null(NewStore().TryLoad("missing.json"));
    }

    // ---- attach -------------------------------------------------------------

    [Fact]
    public void AttachReplayFile_TargetsTheNewestSession_Once()
    {
        var storePath = Path.Combine(_dir, "practice-profiles.json");
        Directory.CreateDirectory(_dir);
        var profiles = new PracticeProfileService(logger: null, filePath: storePath);
        profiles.Record("song", "lead", new LivePlaySessionResult(1000, 0.8, 8, 2, 20, 5, 60, 100, 0.5));

        Assert.True(profiles.AttachReplayFile("song", "lead", "replay-1.json"));
        Assert.False(profiles.AttachReplayFile("song", "lead", "replay-2.json")); // already carries one
        Assert.False(profiles.AttachReplayFile("song", "bass", "replay-3.json")); // unknown arrangement

        var reloaded = new PracticeProfileService(logger: null, filePath: storePath);
        Assert.Equal("replay-1.json", reloaded.GetProfile("song", "lead").Sessions[^1].ReplayFile);
    }

    private static ExpectedSound Judged(double timeMs, LivePlayVerdict verdict, LiveTimingTier tier)
    {
        var sound = new ExpectedSound
        {
            TimeMs = timeMs, StringIndex = 0, Fret = 0, Verdict = verdict,
            TimingTier = tier
        };
        return sound;
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
