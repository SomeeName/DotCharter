using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Dev-machine wall-clock PROFILE of what a Play Mode screen ENTRY costs —
///     the numbers behind <c>docs/agents/ui/rendering-perf/editor-latency.md</c>'s
///     "what a Play Mode ENTRY costs" section. Env-gated exactly like
///     <see cref="EditorLatencyProfileTests" /> (absolute timings are meaningless
///     on a hosted runner) and asserting nothing but sanity: the printed
///     <c>[PerfHarness]</c> lines are the deliverable.
///     <para>
///         The structural guards that keep these numbers from regressing live
///         beside the code they cover — <c>UI/ViewModels/PlayMode/FrontDoorLastSessionReadTests</c>.
///         This class exists only to MEASURE.
///     </para>
///     Run:
///     <code>
///     $env:DOTCHARTER_PERF_GATE="1"
///     dotnet test DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter PlayModeEntryProfile
///     </code>
/// </summary>
public sealed class PlayModeEntryProfileTests
{
    private readonly ITestOutputHelper _out;

    public PlayModeEntryProfileTests(ITestOutputHelper output) => _out = output;

    private static void RequirePerfGate()
        => Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock profile — set DOTCHARTER_PERF_GATE=1 to run.");

    /// <summary>A practice store the size a long-running player has: profiles ×
    /// sessions, each session carrying the per-section detail a real run
    /// persists.</summary>
    private static Dictionary<string, PracticeArrangementProfile> Store(
        int arrangements, int sessionsEach, int sectionsEach)
    {
        var store = new Dictionary<string, PracticeArrangementProfile>(StringComparer.Ordinal);
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var a = 0; a < arrangements; a++)
        {
            var profile = new PracticeArrangementProfile
            {
                DisplayName = $"Artist {a} - Song {a}",
                BestScore = 100_000 + a,
                BestAccuracy = 0.5 + a % 40 / 100.0,
                MasteryPercent = 40 + a % 50,
                ChartDifficulty = 1 + a % 10
            };
            for (var s = 0; s < sessionsEach; s++)
            {
                var sections = new List<SectionPlayRecord>(sectionsEach);
                for (var k = 0; k < sectionsEach; k++)
                {
                    sections.Add(new SectionPlayRecord
                    {
                        Name = $"Section {k}",
                        StartMs = k * 10_000,
                        EndMs = (k + 1) * 10_000,
                        Hits = 40 + k,
                        Misses = 3 + (k + s) % 7
                    });
                }

                profile.Sessions.Add(new PracticeSessionRecord
                {
                    EndedUtc = start.AddHours(a * sessionsEach + s),
                    Score = 50_000 + s,
                    Accuracy = 0.6 + (s % 30) / 100.0,
                    BestStreak = 40 + s,
                    Hits = 400,
                    Misses = 40,
                    TotalSounds = 440,
                    DurationSeconds = 240,
                    Sections = sections
                });
            }

            store[$"C:\\Songs\\song{a}.rscp|Lead"] = profile;
        }

        return store;
    }

    private double Time(string label, int iterations, Action body)
    {
        body();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) body();
        sw.Stop();
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        var ms = sw.Elapsed.TotalMilliseconds / iterations;
        _out.WriteLine($"[PerfHarness] {label}: {ms:0.000} ms · {bytes:N0} B");
        return ms;
    }

    [Fact]
    public void MenuEntry_Profile()
    {
        RequirePerfGate();

        var store = Store(200, 6, 14);
        _out.WriteLine($"[PerfHarness] store: {store.Count} arrangements × 6 sessions × 14 sections");

        // The session-history projection this used to time beside the walk
        // was removed with the History screen on 2026-09-19; the front door's
        // newest-run read is the walk alone.
        var dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, PracticeProfileService.StoreFileName);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(store));
            var stats = new GameplayStatsService(new PracticeProfileService(logger: null, filePath: path));
            Assert.NotNull(stats.GetMostRecentSession());

            Time("GameplayStatsService.GetMostRecentSession (RefreshFrontDoor's newest-run read)", 20,
                () => stats.GetMostRecentSession());
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }
}
