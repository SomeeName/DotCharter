using DotCharter.Services.Editing.Improvement;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
/// The ladder a dynamic-difficulty run judges (step 2). Most custom charts ship one
///     maxed level, which gave dynamic difficulty nothing to ride; such a chart now gets a ladder
///     generated IN MEMORY from that level, by the same generator Generate Levels uses (thinned
/// the way official ladders thin). The chart itself is never touched.
/// </summary>
public static class DynamicDifficultyLadder
{
    /// <summary>
    ///     How many levels a generated ladder has. <see cref="DynamicDifficultyEngine" /> starts every
    ///     phrase at level 0 and moves one step per pass, so the official median of ~20 levels would
    ///     take a phrase 19 clean passes to reach the full chart; 8 reaches it within a typical
    ///     song's repeats while each step still drops a noticeable share of the notes.
    /// </summary>
    public const int GeneratedLevels = 8;

    /// <summary>The arrangement has something to level: notes or chords on some level.</summary>
    public static bool CanLadder(Arrangement? arrangement) =>
        arrangement != null && arrangement.Levels.Any(l => l.Notes.Count + l.Chords.Count > 0);

    /// <summary>True when a run would judge a generated ladder rather than the chart's own levels.</summary>
    public static bool IsGenerated(Arrangement? arrangement) =>
        CanLadder(arrangement) && !DynamicDifficultyEngine.CanLevel(arrangement);

    /// <summary>The chart's own arrangement when it has levels; otherwise a clone carrying a
    /// generated ladder (or the arrangement itself, should generation refuse it).</summary>
    public static Arrangement For(Arrangement arrangement, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (!IsGenerated(arrangement)) return arrangement;

        var ladder = ChartProjectCloner.CloneArrangement(arrangement);
        return new LevelGenerationService().TryGenerate(ladder, GeneratedLevels, beats) ? ladder : arrangement;
    }
}
