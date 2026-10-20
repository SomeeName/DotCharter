namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
/// How far up a dynamic-difficulty ladder the player usually plays, from their history:
///     the mean of <c>level / (ladder size − 1)</c> over every phrase played at least
///     <see cref="MinTimesPlayed" /> times, 0 at the bottom of a ladder and 1 at its top. A
///     never-played song starts its phrases there instead of at level 0. Records without a ladder
///     size (written before it was stored) count for nothing until replayed.
/// </summary>
public static class PlayerLadderDepth
{
    /// <summary>A phrase played once says little about the player; twice is the floor.</summary>
    public const int MinTimesPlayed = 2;

    /// <summary>The depth, or null when no phrase qualifies (or every one sits at the bottom).</summary>
    public static double? From(IEnumerable<PracticeArrangementProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var sum = 0.0;
        var count = 0;
        foreach (var profile in profiles)
        {
            foreach (var record in profile.PhraseMastery.Values)
            {
                if (record.TimesPlayed < MinTimesPlayed || record.LadderSize < 2) continue;
                sum += Math.Clamp(record.Level / (double)(record.LadderSize - 1), 0, 1);
                count++;
            }
        }

        if (count == 0) return null;
        var depth = sum / count;
        return depth > 0 ? depth : null;
    }
}
