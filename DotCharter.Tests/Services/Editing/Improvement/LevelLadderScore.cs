namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     How closely a generated difficulty ladder matches an official one (#295, step 1). Both
///     ladders are DENSE (every level is the whole song at that difficulty, as #323 imports them),
///     so level k of each is compared by the ONSETS it keeps from the hardest level: precision,
///     recall and F1, plus the sound-count ratio. <see cref="LevelScore.RandomF1" /> is what keeping the same
///     number of onsets at random would score, so a result reads as lift over chance.
/// </summary>
internal static class LevelLadderScore
{
    public sealed record LevelScore(int Level, int Official, int Generated, int Shared, int Pool)
    {
        public double Precision => Generated == 0 ? 1 : Shared / (double)Generated;
        public double Recall => Official == 0 ? 1 : Shared / (double)Official;
        public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

        /// <summary>The expected F1 of keeping <see cref="Generated" /> of the <see cref="Pool" />
        /// top-level onsets at random.</summary>
        public double RandomF1 => Pool == 0 || Official + Generated == 0
            ? 0
            : 2.0 * Official * Generated / Pool / (Official + Generated);

        public double CountRatio => Official == 0 ? 0 : Generated / (double)Official;
    }

    public static HashSet<FractionalPosition> Onsets(ChartLevel level)
    {
        var onsets = new HashSet<FractionalPosition>();
        foreach (var note in level.Notes) onsets.Add(note.Position);
        foreach (var chord in level.Chords) onsets.Add(chord.Position);
        return onsets;
    }

    /// <summary>Level k of <paramref name="official" /> against level k of <paramref name="generated" />,
    /// for every level below the top (the top is the source of both, identical by construction).</summary>
    public static List<LevelScore> Compare(IReadOnlyList<ChartLevel> official, IReadOnlyList<ChartLevel> generated)
    {
        var count = Math.Min(official.Count, generated.Count);
        var pool = Onsets(official[^1]).Count;
        var scores = new List<LevelScore>(Math.Max(0, count - 1));
        for (var k = 0; k < count - 1; k++)
        {
            var o = Onsets(official[k]);
            var g = Onsets(generated[k]);
            var shared = o.Count(g.Contains);
            scores.Add(new LevelScore(k, o.Count, g.Count, shared, pool));
        }

        return scores;
    }
}
