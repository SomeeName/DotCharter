namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     One row of the export's resolved iteration table: the phrase, the start the SNG
///     writes (the owning start clipped into the song), where that ends, the owning start
///     (the whole millisecond the XML writes, at least zero) and the highest exported level
///     holding a note or chord it owns (0 when it owns none).
/// </summary>
internal sealed record RsResolvedIteration(int PhraseId, int StartMs, int EndMs, int OwnerStartMs,
    int ContentMaxDifficulty);

/// <summary>
///     The phrases of one export, resolved ONCE and read by the XML writer, the SNG mapper
///     and the level ladder: both phrase stores, the iteration table (time, phrase, owned
///     content, maximum) and the levels in file shape. Ownership is the XML's: a sound
///     belongs to the last iteration WRITTEN at or before its whole millisecond, content
///     before the first iteration to the first one. The SNG clips its iteration times into
///     the song but assigns its notes by the same rule, so an END written after the song
///     length (END on the last beat) neither takes the notes before it nor drops any.
/// </summary>
internal sealed record RsPhraseExportData(
    IReadOnlyList<string> Phrases,
    IReadOnlyList<PhraseIteration> Iterations,
    Dictionary<string, PhraseInfo> PhraseInfos)
{
    /// <summary>The table, in start-time order (source order within one millisecond).</summary>
    public IReadOnlyList<RsResolvedIteration> Resolved { get; private set; } = [];

    /// <summary>The start each entry of <see cref="Iterations" /> is written with.</summary>
    public IReadOnlyList<int> IterationStartMs { get; private set; } = [];

    /// <summary>The levels in file shape, cut along the table's own boundaries.</summary>
    public RsLevelLadderExport Ladder { get; private set; } = new([], new Dictionary<string, int>());

    public static RsPhraseExportData ForExport(Arrangement arrangement, IList<BeatMarker> beats, int songLengthMs)
    {
        var result = Unresolved(arrangement, beats);
        result.Resolve(result.Phrases, result.Iterations.Select((iteration, index) =>
            (iteration.PhraseId, StartMs: result.IterationStartMs[index])), arrangement, beats, songLengthMs);
        return result;
    }

    /// <summary>Both phrase stores and the start each iteration is written with, before
    /// <see cref="Resolve" /> cuts the ladder: what the dynamic-difficulty check reads.</summary>
    internal static RsPhraseExportData Unresolved(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var (phrases, iterations) = PhraseMarkers.Iterations(arrangement, () =>
            beats.Count == 0 ? FractionalPosition.Zero :
                EndNoGuitarSection.BeatAfterContent(arrangement, beats) ?? new FractionalPosition(beats.Count - 1));
        var infos = new Dictionary<string, PhraseInfo>(StringComparer.Ordinal);
        foreach (var phrase in phrases)
        {
            infos[phrase] = arrangement.PhraseInfos.TryGetValue(phrase, out var existing)
                ? new PhraseInfo(existing.MaxDifficulty, existing.Solo)
                : new PhraseInfo();
        }

        var starts = iterations
            .Select(iteration => TimelineMath.ToWholeMs(iteration.Position.GetPositionMs(beats)))
            .ToList();
        return new RsPhraseExportData(phrases, iterations, infos) { IterationStartMs = starts };
    }

    /// <summary>
    ///     The iterations in the order the files own content by, and the ladder spans cut along
    ///     them: only the song's start clips ownership (ties before it keep source order); a
    ///     start past the song length owns from where the XML writes it.
    /// </summary>
    internal static (List<(int PhraseId, int StartMs)> Ordered, RsLadderSpans Spans) Order(
        IReadOnlyList<string> phrases, IEnumerable<(int PhraseId, int StartMs)> iterations, IList<BeatMarker> beats)
    {
        var ordered = iterations
            .Where(iteration => iteration.PhraseId >= 0 && iteration.PhraseId < phrases.Count)
            .Select(iteration => (iteration.PhraseId, StartMs: Math.Max(0, iteration.StartMs)))
            .OrderBy(iteration => iteration.StartMs)
            .ToList();
        var starts = ordered.Select(iteration => iteration.StartMs).ToArray();
        var spans = new RsLadderSpans([.. ordered.Select(iteration => phrases[iteration.PhraseId])],
            position => OwnerOf(starts, TimelineMath.ToWholeMs(position.GetPositionMs(beats))));
        return (ordered, spans);
    }

    /// <summary>
    ///     Builds the table from the iteration starts the XML writes, cuts the level ladder
    ///     along them and settles every phrase's maximum: the authored one, the ladder's
    ///     raise, and never less than the content its iterations own.
    /// </summary>
    public void Resolve(IReadOnlyList<string> phrases, IEnumerable<(int PhraseId, int StartMs)> iterations,
        Arrangement arrangement, IList<BeatMarker> beats, int songLengthMs)
    {
        var (ordered, spans) = Order(phrases, iterations, beats);
        var starts = ordered.Select(iteration => iteration.StartMs).ToArray();
        Ladder = ordered.Count == 0 ? RsLevelLadder.ForExport(arrangement) : RsLevelLadder.ForExport(arrangement, spans);

        var contentMax = new int[ordered.Count];
        var sounds = ordered.Count == 0 ? [] : Ladder.Levels.SelectMany(level => level.Notes.Select(note => note.Position)
            .Concat(level.Chords.Select(chord => chord.Position)).Select(position => (level.DifficultyIndex, position)));
        foreach (var (difficulty, position) in sounds)
        {
            // Early content stays under iteration zero, as in the SNG's note assignment.
            var owner = Math.Max(0, OwnerOf(starts, TimelineMath.ToWholeMs(position.GetPositionMs(beats))));
            contentMax[owner] = Math.Max(contentMax[owner], difficulty);
        }

        var rows = new List<RsResolvedIteration>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var name = phrases[ordered[index].PhraseId];
            if (!PhraseInfos.TryGetValue(name, out var info))
                PhraseInfos[name] = info = new PhraseInfo();
            var shipped = Ladder.ShippedMaxDifficulty.GetValueOrDefault(name, info.MaxDifficulty);
            info.MaxDifficulty = Math.Max(info.MaxDifficulty, Math.Max(shipped, contentMax[index]));
            var end = index + 1 < ordered.Count ? starts[index + 1] : songLengthMs;
            rows.Add(new RsResolvedIteration(ordered[index].PhraseId, Math.Min(starts[index], songLengthMs),
                Math.Min(end, songLengthMs), starts[index], contentMax[index]));
        }

        Resolved = rows;
    }

    /// <summary>The last iteration starting at or before <paramref name="timeMs" />
    /// (the last of several on one millisecond); -1 before the first.</summary>
    private static int OwnerOf(int[] starts, int timeMs)
    {
        int lo = 0, hi = starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (starts[mid] <= timeMs)
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
}
