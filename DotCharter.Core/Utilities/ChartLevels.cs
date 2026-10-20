using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Which level of an arrangement is "the chart" when a caller has no
///     active level to go by.
///     <para>
///         Every <see cref="ChartLevel" /> is the whole song at one difficulty —
///         an imported Rocksmith ladder is filled to that shape on import
///         (<c>RsLevelLadder</c>) and <c>LevelGenerationService</c> writes it —
///         so the HARDEST level is the whole song at its hardest. Level 0 is
///         the thinnest beginner reduction. Reading <c>Levels[0]</c> as "the
///         chart" was harmless only while a psarc import arrived as one level.
///     </para>
/// </summary>
public static class ChartLevels
{
    /// <summary>
    ///     The level with the highest <see cref="ChartLevel.DifficultyIndex" />
    ///     (the first of them on a tie), or null when there is none. Indexed
    ///     and allocation-free, because the 2D timeline calls it per frame.
    /// </summary>
    public static ChartLevel? Hardest(Arrangement? arrangement)
    {
        if (arrangement is not { Levels.Count: > 0 }) return null;

        var levels = arrangement.Levels;
        var hardest = levels[0];
        for (var i = 1; i < levels.Count; i++)
        {
            if (levels[i].DifficultyIndex > hardest.DifficultyIndex) hardest = levels[i];
        }

        return hardest;
    }
}
