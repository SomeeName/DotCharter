using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     An imported Guitar Pro pick scrape (#583, #586, #599) is never an expected
///     sound: a multi-string scrape is ONE chord split into notes and ignored as a
///     whole, and the matcher skips <c>Ignore</c>. Only the CHORD's own flag counts
///     there — ignored chord notes under an unignored chord would still be judged —
///     so this pins the import and the matcher together, on the real gpif import of
///     the Shinbatsu-shaped score.
/// </summary>
public sealed class LiveNoteMatcherPickScrapeTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats()
        => [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    [Fact]
    public void ImportedScrapes_AreNeverExpected()
    {
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu()));
        var level = Assert.Single(arrangement.Levels);
        // The scrapes really are chords here: four split chords, two strings each.
        Assert.Equal(4, level.Chords.Count(c => c is { SplitIntoNotes: true, ChordNotes.Count: 2 }));
        var matcher = new LiveNoteMatcher();

        matcher.Load(arrangement, level, Beats());

        // Only the four lick notes of bar 1 (beats 0-3); the scrapes sit on
        // beats 4, 5, 6 (two strings each) and 8 (two strings).
        Assert.Equal(4, matcher.Stats.TotalSounds);
        Assert.Equal(new[] { 0.0, 500.0, 1000.0, 1500.0 }, matcher.Expected.Select(e => e.TimeMs).ToArray());

        matcher.AdvanceTo(playheadMs: 7000);

        Assert.Equal(4, matcher.Stats.Misses);
        Assert.Equal(4, matcher.Stats.Judged);
    }
}
