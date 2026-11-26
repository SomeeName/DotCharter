using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     How the charter clears D110's spots once they have listened: "Checked" clears the first unchecked spot (the
///     one the finding points at, so the finding moves on to the next), "All checked" clears every one. Each is one
///     undo step over the live arrangement; neither is a default fix, so "Fix all" never checks a spot unheard.
/// </summary>
public static class AudioCheckMarks
{
    /// <summary>The earliest spot, or null when none is left.</summary>
    public static AudioCheckSpot? First(IReadOnlyList<AudioCheckSpot> spots) =>
        spots.Count == 0 ? null : spots.MinBy(s => s.Position);

    /// <summary>Marks the first unchecked spot checked, or null when <paramref name="arrangement" /> has none.</summary>
    public static IChartAction? CheckFirst(Arrangement arrangement)
    {
        if (First(arrangement.AudioCheckSpots) is not { } first) return null;
        List<AudioCheckSpot> rest = [.. arrangement.AudioCheckSpots];
        rest.Remove(first);
        return new SetAudioCheckSpotsAction(arrangement, rest);
    }

    /// <summary>Marks every spot checked, or null when <paramref name="arrangement" /> has none.</summary>
    public static IChartAction? CheckAll(Arrangement arrangement) =>
        arrangement.AudioCheckSpots.Count == 0 ? null : new SetAudioCheckSpotsAction(arrangement, []);
}
