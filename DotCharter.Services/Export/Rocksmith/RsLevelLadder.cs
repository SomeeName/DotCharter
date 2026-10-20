using DotCharter.Services.State;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Rocksmith's dynamic-difficulty ladder, phrase by phrase: the one place
///     that converts between the FILE shape of it and the CHART shape.
///     <para>
///         <b>The file shape is sparse.</b> In an RS2014 XML or SNG, level N
///         carries only the phrases whose <c>maxDifficulty</c> reaches N; a
///         phrase with max 5 has nothing at all in level 10, and the game plays
///         its level 5 there. Measured on the EOF dynamic-difficulty exports in
///         <c>TestData/EOF</c> (four arrangements, 23–29 levels): not one note,
///         chord, anchor or hand shape sits in a level above its phrase's max,
///         and the TOP level of a 29-level Lead holds one phrase of 32.
///     </para>
///     <para>
///         <b>The chart shape is dense: every <see cref="ChartLevel" /> is the
///         whole song at that difficulty</b>, so the hardest level IS the whole
///         song at its hardest. Everything downstream already reads it that way:
///         <c>ProjectState</c> opens the editor on the hardest level, Play Mode
///         draws <c>Levels[^1]</c>, Live Play without dynamic difficulty judges
///         one level whole, <c>LevelGenerationService</c> writes dense levels and
///         reads the hardest one as its source, and the difficulty estimator rates
///         it. Kept sparse, an imported official chart opened on a skeleton.
///     </para>
///     <para>
///         <see cref="FillInheritedPhrases" /> turns the file shape into the chart
///         shape on IMPORT (both readers, so the RS-XML and the psarc paths
///         agree); <see cref="ForExport" /> turns it back on EXPORT (both
///         writers), so an imported chart re-exports with the ladder it came
///         with instead of every phrase raised to the top.
///     </para>
/// </summary>
public static class RsLevelLadder
{
    /// <summary>
    ///     Fills every level above a phrase's <see cref="PhraseInfo.MaxDifficulty" />
    ///     with CLONES of that phrase's content at its max level (the highest
    ///     existing level at or below it) — what the game plays there. A level
    ///     that already has content of a kind inside the phrase is left alone, so
    ///     running this on a dense chart changes nothing. A phrase without a
    ///     <see cref="PhraseInfo" /> is treated as fully levelled (the
    ///     convention <c>LiveNoteMatcher</c> and <c>LevelSquashService</c> share).
    ///     Clones, never shared references: the editor mutates sounds in place.
    /// </summary>
    /// <returns>How many elements were cloned in.</returns>
    public static int FillInheritedPhrases(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var ladder = Prepare(arrangement);
        if (ladder == null) return 0;

        var touched = new HashSet<ChartLevel>(ReferenceEqualityComparer.Instance);
        var copied = 0;
        copied += ladder.Fill(static l => l.Notes, static n => n.Position, static n => n.Clone(), touched);
        copied += ladder.Fill(static l => l.Chords, static c => c.Position, static c => c.Clone(), touched);
        copied += ladder.Fill(static l => l.Anchors, static a => a.Position, static a => a.Clone(), touched);
        copied += ladder.Fill(static l => l.HandShapes, static h => h.Position, static h => h.Clone(), touched);
        copied += ladder.Fill(static l => l.FretHandMutes, static m => m.Position,
            static m => new FretHandMute(m.Position), touched);

        foreach (var level in touched)
        {
            SortByPosition(level.Notes, static n => n.Position);
            SortByPosition(level.Chords, static c => c.Position);
            SortByPosition(level.Anchors, static a => a.Position);
            SortByPosition(level.HandShapes, static h => h.Position);
            SortByPosition(level.FretHandMutes, static m => m.Position);
        }

        return copied;
    }

    /// <summary>
    ///     The levels a Rocksmith writer emits, in the arrangement's own level
    ///     order, and the max difficulty each phrase ships with.
    ///     <para>
    ///         A phrase's shipped max is its authored max RAISED to the highest
    ///         level whose content inside the phrase is non-empty AND differs from
    ///         the level below — i.e. content a charter actually edited up there.
    ///         Everything in a level above the shipped max is, by construction, a
    ///         copy of the level below and is left out, which restores the file
    ///         shape. Equality is <see cref="ChartActionStateComparer" />'s, the
    ///         comparer the undo system already keeps in step with every field
    ///         (<c>ChartActionFieldCoverageTests</c>).
    ///     </para>
    ///     <para>
    ///         Only ever drops a level's copy of the level below, and only above
    ///         a phrase's max, so a sparse chart (an EOF import, an older project)
    ///         and an app-generated ladder (every max at the top) export exactly as
    ///         before.
    ///     </para>
    /// </summary>
    public static RsLevelLadderExport ForExport(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var ladder = Prepare(arrangement);
        if (ladder == null) return new RsLevelLadderExport(arrangement.Levels.ToList(), new Dictionary<string, int>());

        var notes = ladder.Buckets(static l => l.Notes, static n => n.Position);
        var chords = ladder.Buckets(static l => l.Chords, static c => c.Position);
        var anchors = ladder.Buckets(static l => l.Anchors, static a => a.Position);
        var handShapes = ladder.Buckets(static l => l.HandShapes, static h => h.Position);
        var mutes = ladder.Buckets(static l => l.FretHandMutes, static m => m.Position);

        // Shipped max per phrase NAME: a phrase repeats, and one max covers every
        // iteration of it, so the raise is the highest over all of them.
        var shipped = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var span = 0; span < ladder.Markers.Count; span++)
        {
            var authored = ladder.AuthoredMax[span];
            if (authored < 0) continue;
            var name = ladder.Markers[span].Name;
            var max = authored;
            for (var j = ladder.Levels.Count - 1; j > 0 && ladder.Levels[j].DifficultyIndex > authored; j--)
            {
                var empty = IsEmpty(notes[j][span]) && IsEmpty(chords[j][span]) && IsEmpty(anchors[j][span]) &&
                            IsEmpty(handShapes[j][span]) && IsEmpty(mutes[j][span]);
                if (empty) continue;

                var same = Same(notes[j][span], notes[j - 1][span], ChartActionStateComparer.GuitarNotesEqual) &&
                           Same(chords[j][span], chords[j - 1][span], ChartActionStateComparer.ChordsEqual) &&
                           Same(anchors[j][span], anchors[j - 1][span], ChartActionStateComparer.AnchorsEqual) &&
                           Same(handShapes[j][span], handShapes[j - 1][span], ChartActionStateComparer.HandShapesEqual) &&
                           Same(mutes[j][span], mutes[j - 1][span], static (a, b) => a.Position == b.Position);
                if (same) continue;

                max = ladder.Levels[j].DifficultyIndex;
                break;
            }

            shipped[name] = shipped.TryGetValue(name, out var earlier) ? Math.Max(earlier, max) : max;
        }

        // Which spans each level must leave out: every span above its phrase's
        // shipped max.
        var substitutes = new Dictionary<ChartLevel, ChartLevel>(ReferenceEqualityComparer.Instance);
        for (var j = 0; j < ladder.Levels.Count; j++)
        {
            var level = ladder.Levels[j];
            var drop = new bool[ladder.Markers.Count];
            var any = false;
            for (var span = 0; span < ladder.Markers.Count; span++)
            {
                if (ladder.AuthoredMax[span] < 0) continue;
                if (shipped[ladder.Markers[span].Name] >= level.DifficultyIndex) continue;
                drop[span] = true;
                any = true;
            }

            if (!any) continue;
            var kept = new ChartLevel
            {
                DifficultyIndex = level.DifficultyIndex,
                Notes = [.. ladder.Keep(level.Notes, static n => n.Position, drop)],
                Chords = [.. ladder.Keep(level.Chords, static c => c.Position, drop)],
                Anchors = [.. ladder.Keep(level.Anchors, static a => a.Position, drop)],
                HandShapes = [.. ladder.Keep(level.HandShapes, static h => h.Position, drop)],
                FretHandMutes = [.. ladder.Keep(level.FretHandMutes, static m => m.Position, drop)]
            };

            // A level that lost nothing (a sparse chart's) is written as itself.
            var lostSomething = kept.Notes.Count != level.Notes.Count || kept.Chords.Count != level.Chords.Count ||
                                kept.Anchors.Count != level.Anchors.Count ||
                                kept.HandShapes.Count != level.HandShapes.Count ||
                                kept.FretHandMutes.Count != level.FretHandMutes.Count;
            if (lostSomething) substitutes[level] = kept;
        }

        var levels = arrangement.Levels.Select(l => substitutes.GetValueOrDefault(l, l)).ToList();
        return new RsLevelLadderExport(levels, shipped);
    }

    private static Ladder? Prepare(Arrangement arrangement)
    {
        if (arrangement.Levels.Count < 2) return null;
        var markers = PhraseMarkers.All(arrangement);
        if (markers.Count == 0) return null;

        var levels = arrangement.Levels.OrderBy(l => l.DifficultyIndex).ToList();
        var top = levels[^1].DifficultyIndex;
        var authoredMax = new int[markers.Count];
        var anyBelowTop = false;
        for (var i = 0; i < markers.Count; i++)
        {
            authoredMax[i] = arrangement.PhraseInfos.TryGetValue(markers[i].Name, out var info) &&
                             info.MaxDifficulty < top
                ? Math.Max(0, info.MaxDifficulty)
                : -1;
            anyBelowTop |= authoredMax[i] >= 0;
        }

        return anyBelowTop ? new Ladder(levels, markers, authoredMax) : null;
    }

    private static bool IsEmpty<T>(List<T>? items) => items is not { Count: > 0 };

    private static bool Same<T>(List<T>? a, List<T>? b, Func<T, T, bool> equal)
    {
        var countA = a?.Count ?? 0;
        if (countA != (b?.Count ?? 0)) return false;
        for (var i = 0; i < countA; i++)
        {
            if (!equal(a![i], b![i])) return false;
        }

        return true;
    }

    private static void SortByPosition<T>(IList<T> items, Func<T, FractionalPosition> position)
    {
        if (items.Count <= 1) return;
        var sorted = items.OrderBy(position).ToList(); // stable: same-position order survives
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(items[i], sorted[i])) items[i] = sorted[i];
        }
    }

    /// <summary>The levels in difficulty order and the phrase spans over them.
    /// A span runs from its marker to the next one; content before the first
    /// marker belongs to no phrase and is never touched.</summary>
    private sealed class Ladder(List<ChartLevel> levels, List<PhraseMarkers.PhraseMarker> markers, int[] authoredMax)
    {
        public List<ChartLevel> Levels { get; } = levels;
        public List<PhraseMarkers.PhraseMarker> Markers { get; } = markers;

        /// <summary>Per span: the authored max when it is below the top level,
        /// else -1 (fully levelled — nothing to fill, nothing to drop).</summary>
        public int[] AuthoredMax { get; } = authoredMax;

        public int SpanOf(FractionalPosition position)
        {
            int lo = 0, hi = Markers.Count - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (Markers[mid].Position <= position)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return found;
        }

        /// <summary>[level][span] → that level's elements inside the span, in
        /// collection order (null when there are none).</summary>
        public List<T>?[][] Buckets<T>(Func<ChartLevel, IList<T>> select, Func<T, FractionalPosition> position)
        {
            var result = new List<T>?[Levels.Count][];
            for (var j = 0; j < Levels.Count; j++)
            {
                var buckets = new List<T>?[Markers.Count];
                foreach (var item in select(Levels[j]))
                {
                    var span = SpanOf(position(item));
                    if (span < 0) continue;
                    (buckets[span] ??= []).Add(item);
                }

                result[j] = buckets;
            }

            return result;
        }

        public int Fill<T>(Func<ChartLevel, IList<T>> select, Func<T, FractionalPosition> position,
            Func<T, T> clone, HashSet<ChartLevel> touched)
        {
            var buckets = Buckets(select, position);
            var copied = 0;
            for (var span = 0; span < Markers.Count; span++)
            {
                var max = AuthoredMax[span];
                if (max < 0) continue;

                // The phrase's max level, or the highest existing one below it.
                var source = -1;
                for (var j = 0; j < Levels.Count && Levels[j].DifficultyIndex <= max; j++) source = j;
                if (source < 0 || buckets[source][span] is not { Count: > 0 } content) continue;

                for (var j = source + 1; j < Levels.Count; j++)
                {
                    if (buckets[j][span] is { Count: > 0 }) continue;
                    var target = select(Levels[j]);
                    foreach (var item in content) target.Add(clone(item));
                    copied += content.Count;
                    touched.Add(Levels[j]);
                }
            }

            return copied;
        }

        public IEnumerable<T> Keep<T>(IEnumerable<T> items, Func<T, FractionalPosition> position, bool[] drop)
        {
            foreach (var item in items)
            {
                var span = SpanOf(position(item));
                if (span >= 0 && drop[span]) continue;
                yield return item;
            }
        }
    }
}

/// <summary>What <see cref="RsLevelLadder.ForExport" /> hands a writer.</summary>
/// <param name="Levels">The levels to write, in the arrangement's own order;
/// a level that lost nothing is the arrangement's own instance.</param>
/// <param name="ShippedMaxDifficulty">The max difficulty each phrase NAME ships
/// with, for every phrase whose authored max is below the top level. A phrase
/// absent from it keeps whatever the writer already resolved.</param>
public sealed record RsLevelLadderExport(
    IReadOnlyList<ChartLevel> Levels,
    IReadOnlyDictionary<string, int> ShippedMaxDifficulty);
