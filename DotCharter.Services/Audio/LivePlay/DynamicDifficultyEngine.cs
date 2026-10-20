using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     One phrase's session outcome for persistence: the level the ladder
///     ended on plus the phrase's judged tallies, so the practice store can
///     resume the ladder and fold the accuracy into a decayed average.
/// <paramref name="LadderSize" /> is how many levels the phrase had, so a level can
///     be compared across songs: level 5 of 8 is not level 5 of 25. Zero when unknown.
/// </summary>
public readonly record struct PhraseMasteryOutcome(string PhraseName, int Level, int Hits, int Misses, int LadderSize = 0)
{
    public int Judged => Hits + Misses;

    /// <summary>Zero judged is 0 — see <see cref="AccuracyMath" />. Nothing acts
    /// on it there: <c>PracticeProfileService.FoldPhraseMastery</c> skips an
    /// outcome with <c>Judged &lt;= 0</c> before it ever reads this.</summary>
    public double Accuracy => AccuracyMath.FromCounts(Hits, Misses);
}

/// <summary>
///     Rocksmith-style dynamic difficulty: each PHRASE carries a level that
///     moves up on accurate play and down on poor play. Levels are keyed by
///     phrase name so every later iteration of the same riff benefits from
///     (or is relieved by) how its earlier iterations went. Pure — the
///     session feeds phrase results in and uses the engine as the matcher's
///     <see cref="LiveDifficultySelector"/>. <see cref="Seed"/> resumes a
///     persisted ladder; <see cref="SnapshotOutcomes"/> hands the session's
///     per-phrase results back for persistence.
/// </summary>
public sealed class DynamicDifficultyEngine
{
    /// <summary>Phrase accuracy at or above which the phrase levels up.</summary>
    public double LevelUpAccuracy { get; init; } = 0.9;

    /// <summary>Phrase accuracy below which the phrase levels down.</summary>
    public double LevelDownAccuracy { get; init; } = 0.55;

    /// <summary>Level a phrase starts at the first time it is played.</summary>
    public int InitialLevel { get; init; }

    /// <summary>
    ///     Where an unseen phrase starts on its own ladder, 0 (bottom) to 1 (top), instead of
    ///     <see cref="InitialLevel" />: the player's depth from their history on other songs
    /// (<see cref="PlayerLadderDepth" />). Null starts at <see cref="InitialLevel" />.
    /// </summary>
    public double? StartingDepth { get; set; }

    private readonly Dictionary<string, int> _levels = [];
    private readonly Dictionary<string, int> _ladderSizes = [];
    private readonly Dictionary<string, (int Hits, int Misses)> _tallies = [];

    /// <summary>
    ///     Whether a ladder can select anything on this arrangement. The matcher
    ///     clamps every phrase to the levels that EXIST, so on a one-level chart
    ///     (most CDLC; an arrangement squashed in the editor) the engine would
    ///     still move and persist a per-phrase level that loads the same notes —
    ///     a capability that looks live and is not. Live Play shows the toggle
    /// disabled with its reason instead.
    /// </summary>
    public static bool CanLevel(Arrangement? arrangement) => arrangement is { Levels.Count: > 1 };

    /// <summary>Current level for a phrase, clamped to its own ceiling.</summary>
    public int LevelFor(LivePhraseSpan phrase)
    {
        var top = Math.Max(0, phrase.MaxDifficulty);
        var level = _levels.TryGetValue(phrase.PhraseName, out var l) ? l : StartLevel(top);
        return Math.Clamp(level, 0, top);
    }

    /// <summary>An unseen phrase's level on a ladder whose top level is <paramref name="top" />.</summary>
    private int StartLevel(int top) =>
        StartingDepth is { } depth ? (int)Math.Round(Math.Clamp(depth, 0, 1) * top, MidpointRounding.AwayFromZero) : InitialLevel;

    /// <summary>
    /// "Start at 0": drops the starting depth and every level this run has moved, so
    ///     the ladder starts over at <see cref="InitialLevel" />. The judged tallies stay.
    /// </summary>
    public void StartOverAtInitialLevel()
    {
        StartingDepth = null;
        _levels.Clear();
    }

    /// <summary>Resumes a persisted ladder: each phrase starts at its stored
    /// level instead of <see cref="InitialLevel"/> (still clamped to the
    /// phrase ceiling by <see cref="LevelFor"/>). Call before the first
    /// <see cref="CompletePhrase"/>; existing entries are overwritten.</summary>
    public void Seed(IEnumerable<KeyValuePair<string, int>> phraseLevels)
    {
        foreach (var (name, level) in phraseLevels)
            _levels[name] = Math.Max(0, level);
    }

    /// <summary>
    ///     Registers one finished phrase iteration. Returns true when the
    ///     phrase's level changed (the caller should reload the matcher so
    ///     later iterations pick the new level up). Iterations with nothing
    ///     judged (rest phrases, seeked-over regions) never move the level.
    /// </summary>
    public bool CompletePhrase(LivePhraseSpan phrase, int hits, int misses)
    {
        var judged = hits + misses;
        if (judged <= 0) return false;

        var tally = _tallies.TryGetValue(phrase.PhraseName, out var t) ? t : (Hits: 0, Misses: 0);
        _tallies[phrase.PhraseName] = (tally.Hits + hits, tally.Misses + misses);

        _ladderSizes[phrase.PhraseName] = Math.Max(0, phrase.MaxDifficulty) + 1;

        var accuracy = hits / (double)judged;
        var current = LevelFor(phrase);
        var next = current;
        if (accuracy >= LevelUpAccuracy) next = current + 1;
        else if (accuracy < LevelDownAccuracy) next = current - 1;

        next = Math.Clamp(next, 0, Math.Max(0, phrase.MaxDifficulty));
        // Kept even when unchanged: a phrase that started from the depth stays where it was
        // played, whatever happens to the depth later.
        _levels[phrase.PhraseName] = next;
        return next != current;
    }

    /// <summary>Average current level across the given spans, for the HUD.</summary>
    public double AverageLevel(IReadOnlyList<LivePhraseSpan> spans)
    {
        if (spans.Count == 0) return InitialLevel;
        var sum = 0.0;
        foreach (var span in spans) sum += LevelFor(span);
        return sum / spans.Count;
    }

    /// <summary>Per-phrase session outcomes for persistence: every phrase
    /// that had at least one judged iteration this session, with the level
    /// the ladder ended on. Sorted by phrase name — deterministic order for
    /// stores and tests. Allocates; call at session end, not on the tick path.</summary>
    public List<PhraseMasteryOutcome> SnapshotOutcomes()
    {
        var outcomes = new List<PhraseMasteryOutcome>(_tallies.Count);
        foreach (var (name, tally) in _tallies)
        {
            var size = _ladderSizes.TryGetValue(name, out var s) ? s : 0;
            var level = _levels.TryGetValue(name, out var l) ? l : StartLevel(Math.Max(0, size - 1));
            outcomes.Add(new PhraseMasteryOutcome(name, Math.Max(0, level), tally.Hits, tally.Misses, size));
        }

        outcomes.Sort(static (a, b) => string.CompareOrdinal(a.PhraseName, b.PhraseName));
        return outcomes;
    }

    public void Reset()
    {
        _levels.Clear();
        _ladderSizes.Clear();
        _tallies.Clear();
    }
}
