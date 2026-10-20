namespace DotCharter.Tone.Audio.Tuning;

/// <summary>
///     A tuning the rig's tuner can tune TO: per-string semitone steps against
///     standard (low string first, the same convention as a chart's
///     <c>TuningPitches</c>), for a 6-string guitar or a 4-string bass.
///     <see cref="Name" /> comes from <see cref="TuningNameResolver" />, the one
///     table that names a chart's tuning too, so "Drop C" here and "Drop C" on
///     an arrangement cannot disagree.
/// </summary>
public sealed record TunerTuning(string Id, bool IsBass, IReadOnlyList<int> Steps)
{
    public string Name => TuningNameResolver.ResolveFromOffsets(Steps.Count, Steps) ?? Id;
}

/// <summary>The tunings the rig's tuner offers, commonest first. Pure.</summary>
public static class TunerTunings
{
    /// <summary>No target: the tuner names whatever note it hears.</summary>
    public const string ChromaticId = "chromatic";

    public static IReadOnlyList<TunerTuning> All { get; } =
    [
        Guitar(0, 0, 0, 0, 0, 0),         // E Standard
        Guitar(-2, 0, 0, 0, 0, 0),        // Drop D
        Guitar(-1, -1, -1, -1, -1, -1),   // Eb Standard
        Guitar(-2, -2, -2, -2, -2, -2),   // D Standard
        Guitar(-3, -1, -1, -1, -1, -1),   // Drop C#
        Guitar(-4, -2, -2, -2, -2, -2),   // Drop C
        Guitar(-3, -3, -3, -3, -3, -3),   // Db Standard
        Guitar(-4, -4, -4, -4, -4, -4),   // C Standard
        Guitar(-5, -3, -3, -3, -3, -3),   // Drop B
        Guitar(-5, -5, -5, -5, -5, -5),   // B Standard
        Guitar(-7, -5, -5, -5, -5, -5),   // Drop A
        Guitar(-2, 0, 0, 0, -2, -2),      // DADGAD
        Guitar(-2, 0, 0, 0, 0, -2),       // Double Drop D
        Guitar(-2, 0, 0, -1, -2, -2),     // Open D
        Guitar(-2, -2, 0, 0, 0, -2),      // Open G
        Guitar(0, 2, 2, 1, 0, 0),         // Open E
        Guitar(0, 0, 2, 2, 2, 0),         // Open A
        Guitar(-4, -2, -2, 0, 1, 0),      // Open C
        Bass(0, 0, 0, 0),                 // E Standard
        Bass(-2, 0, 0, 0),                // Drop D
        Bass(-1, -1, -1, -1),             // Eb Standard
        Bass(-2, -2, -2, -2),             // D Standard
        Bass(-4, -2, -2, -2)              // Drop C
    ];

    /// <summary>The tuning with <paramref name="id" />, or null for chromatic or
    /// an id this build does not know (a hand-edited config).</summary>
    public static TunerTuning? Find(string? id) =>
        string.IsNullOrEmpty(id) || id == ChromaticId ? null : All.FirstOrDefault(t => t.Id == id);

    private static TunerTuning Guitar(params int[] steps) => new(IdOf(false, steps), false, steps);
    private static TunerTuning Bass(params int[] steps) => new(IdOf(true, steps), true, steps);

    /// <summary>A stable id off the steps themselves ("guitar:-2,0,0,0,0,0"), so a
    /// persisted choice survives a rename or a reorder of this list.</summary>
    private static string IdOf(bool bass, int[] steps) => $"{(bass ? "bass" : "guitar")}:{string.Join(',', steps)}";
}
