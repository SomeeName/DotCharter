using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     Lyric auto-sync never places a zero-length syllable or one that starts at the previous
///     syllable's start. The aligner can report such a pair where it cannot clearly hear
///     a short syllable; the fold spreads it with the export's own rule
///     (<see cref="VocalTiming.EnforceMinimum" />), leaves every other syllable where it was and
///     keeps the pair flagged <see cref="LyricReviewReason.Compressed" />. Made-up words only.
/// </summary>
public sealed class LyricMinimumTimingTests
{
    /// <summary>120 BPM: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<LyricPhrase> Build(string text, params (double Start, double End)[] times)
    {
        var sheet = LyricSheetParser.Parse(text);
        Assert.Equal(times.Length, sheet.Syllables.Count);
        var aligned = times
            .Select((t, i) => new AlignedSyllable(i, t.Start, t.End, 0.9, IsInterpolated: false))
            .ToList();
        return LyricAutoSyncService.BuildPhrases(sheet, aligned, Beats(), LyricAutoSyncOptions.Default);
    }

    private static double Ms(FractionalPosition position) => position.GetPositionMs(Beats());

    [Fact]
    public void A_zero_length_syllable_sharing_the_next_start_inside_a_line_is_spread()
    {
        const string text = "zo ka pim ru";
        var phrases = Build(text, (1.0, 1.3), (1.5, 1.5), (1.5, 1.8), (2.5, 2.8));
        var control = Build(text, (1.0, 1.3), (1.5, 1.6), (1.7, 1.8), (2.5, 2.8));

        var lyrics = phrases.SelectMany(p => p.Lyrics).ToList();
        AssertSpreadPair(lyrics, 1);
        AssertNeighboursUntouched(lyrics, control.SelectMany(p => p.Lyrics).ToList(), 0, 3);
    }

    [Fact]
    public void A_zero_length_line_end_sharing_the_next_line_start_is_spread()
    {
        const string text = "zo ka\npim ru";
        var phrases = Build(text, (1.0, 1.3), (1.5, 1.5), (1.5, 1.8), (2.5, 2.8));
        var control = Build(text, (1.0, 1.3), (1.5, 1.6), (1.7, 1.8), (2.5, 2.8));

        var lyrics = phrases.SelectMany(p => p.Lyrics).ToList();
        AssertSpreadPair(lyrics, 1);
        AssertNeighboursUntouched(lyrics, control.SelectMany(p => p.Lyrics).ToList(), 0, 3);

        Assert.Equal(2, phrases.Count);
        Assert.Equal(phrases[0].Lyrics[^1].EndPosition, phrases[0].EndPosition);
        Assert.Equal(phrases[1].Lyrics[0].Position, phrases[1].Position);
        Assert.True(phrases[0].EndPosition <= phrases[1].Position);
    }

    /// <summary>
    ///     Two onsets a millisecond apart land on one 1/192-beat tick, so the fold itself, not
    ///     the aligner, would collapse them: the rule has to run after the fold.
    /// </summary>
    [Fact]
    public void Onsets_the_beat_grid_folds_onto_one_tick_are_kept_apart()
    {
        var lyrics = Build("zo ka pim ru", (1.0, 1.3), (1.500, 1.501), (1.501, 1.8), (2.5, 2.8))
            .SelectMany(p => p.Lyrics).ToList();

        AssertSpreadPair(lyrics, 1);
    }

    [Fact]
    public void Every_placed_syllable_is_at_least_the_minimum_or_the_room_to_the_next_start()
    {
        var lyrics = Build("zo ka pim ru bel",
                (1.0, 1.0), (1.010, 1.010), (1.010, 1.4), (1.5, 1.5), (1.5, 1.5))
            .SelectMany(p => p.Lyrics).ToList();

        for (var i = 0; i < lyrics.Count; i++)
        {
            var length = Ms(lyrics[i].EndPosition) - Ms(lyrics[i].Position);
            var room = i + 1 < lyrics.Count ? Ms(lyrics[i + 1].Position) - Ms(lyrics[i].Position) : double.MaxValue;
            Assert.True(length > 0, $"syllable {i} has length 0");
            Assert.True(length >= Math.Min(VocalTiming.MinLengthMs, room) - 1e-6,
                $"syllable {i} is {length} ms with {room} ms of room");
            if (i > 0) Assert.True(lyrics[i].Position > lyrics[i - 1].Position, $"syllable {i} does not start later");
            if (i + 1 < lyrics.Count) Assert.True(lyrics[i].EndPosition <= lyrics[i + 1].Position);
        }
    }

    [Fact]
    public void The_placement_rule_lengthens_short_entries_but_never_shortens_one()
    {
        double[] starts = [0, 100, 100, 200, 210];
        double[] lengths = [300, 0, 50, 5, 40];

        VocalTiming.EnforceMinimum(starts, lengths);

        Assert.Equal([0, 100, 125, 200, 210], starts);
        Assert.Equal([300, 25, 25, 10, 40], lengths);
    }

    private static void AssertSpreadPair(List<LyricEvent> lyrics, int first)
    {
        for (var i = first; i <= first + 1; i++)
        {
            var length = Ms(lyrics[i].EndPosition) - Ms(lyrics[i].Position);
            Assert.True(length >= VocalTiming.MinLengthMs, $"syllable {i} is {length} ms long");
            Assert.True(lyrics[i].Position > lyrics[i - 1].Position, $"syllable {i} does not start later");
            Assert.True(lyrics[i].EndPosition <= lyrics[i + 1].Position, $"syllable {i} runs into the next");
            Assert.True(lyrics[i].ReviewReasons.HasFlag(LyricReviewReason.Compressed), $"syllable {i} lost Compressed");
        }

        Assert.True(Ms(lyrics[first + 1].Position) - Ms(lyrics[first].Position) <= VocalTiming.MinLengthMs + 3,
            "the second syllable moved further than the minimum");
    }

    private static void AssertNeighboursUntouched(List<LyricEvent> lyrics, List<LyricEvent> control, params int[] indices)
    {
        foreach (var i in indices)
        {
            Assert.Equal(control[i].Position, lyrics[i].Position);
            Assert.Equal(control[i].EndPosition, lyrics[i].EndPosition);
        }
    }
}
