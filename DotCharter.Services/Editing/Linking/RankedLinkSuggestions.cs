using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.Linking;

internal sealed class RankedLinkSuggestions
{
    private const int MinimumWholeSectionLength = 4;
    private const int MaxSplitRounds = 6;

    private readonly IReadOnlyList<MeasureSpan> _measures;
    private readonly IReadOnlyList<int>? _source;
    private readonly IReadOnlyList<GpSourceBar> _provenance;
    private readonly int[] _distinctSourceRun;
    private readonly Part[] _parts;
    private readonly RepeatWindowIndex _windows;
    private readonly RepeatWindowIndex _exactWindows;
    private readonly Dictionary<int, List<Copy>> _exactStarts;
    private readonly int[] _repeatBars;
    private readonly ChartProject _project;
    private readonly LinkDetectionScope _scope;
    private readonly RepeatWindowIndex? _writtenWindows;
    private readonly HashSet<int> _replayedWrittenBars;
    private readonly Dictionary<int, Dictionary<Copy, (MeasureContentKey Rule, MeasureContentKey Exact)>> _runKeys = [];
    private LinkDetectionWork _work = new(CancellationToken.None);

    internal int RunKeyBuilds { get; private set; }

    public RankedLinkSuggestions(ChartProject project, IReadOnlyList<MeasureSpan> measures,
        IReadOnlyList<Arrangement> arrangements, IReadOnlyList<int>? source, IReadOnlyList<GpSourceBar> provenance,
        MeasureContentOptions rule, LinkDetectionScope scope)
    {
        _measures = measures;
        _project = project;
        _scope = scope;
        _source = source;
        _provenance = provenance;
        _distinctSourceRun = SourceRunLengths(measures.Count, source);
        _parts = [.. arrangements.Select(a => new Part(a, project.Beats, measures, rule, PreferredEdges()))];
        _windows = new RepeatWindowIndex(BarIds(p => p.BarKeys));
        var exactIds = BarIds(p => p.ExactBarKeys);
        _exactWindows = new RepeatWindowIndex(exactIds);
        _repeatBars = [.. exactIds.Select((id, at) => _parts[at / measures.Count].BarKeys[at % measures.Count].IsEmpty ? -1 - at : id)];
        _exactStarts = Enumerable.Range(0, _parts.Length)
            .SelectMany(p => Enumerable.Range(0, _measures.Count).Select(m => new Copy(p, m)))
            .GroupBy(c => exactIds[Flat(c)]).ToDictionary(g => g.Key, g => g.ToList());
        _replayedWrittenBars = source?.Where(b => b >= 0).GroupBy(b => b).Where(g => g.Skip(1).Any())
            .Select(g => g.Key).ToHashSet() ?? [];
        if (_replayedWrittenBars.Count > 0)
            _writtenWindows = new RepeatWindowIndex([.. source!.Select((bar, i) => bar >= 0 ? bar : -i - 1)]);
    }

    /// <summary>
    ///     The ranked proposals. The longest runs are taken first; a run that holds a shorter
    ///     repeat found elsewhere too is then cut where that repeat starts and ends and the search
    ///     runs again, and short repeats inside the groups are linked nested. When the work
    ///     limit is reached, the last complete search is returned and the limit is rethrown.
    /// </summary>
    public IEnumerable<LinkSuggestion> Find(LinkDetectionWork? work = null)
    {
        if (work != null) _work = work;
        var proposals = new List<Proposal>();
        var limited = !TryPass(proposals);
        for (var round = 0; !limited && round < MaxSplitRounds && AddCuts(proposals); round++)
        {
            var next = new List<Proposal>();
            if (!TryPass(next)) limited = true;
            else proposals = next;
        }

        if (!limited) limited = !TryNest(proposals);
        foreach (var proposal in proposals) yield return Propose(proposal.Copies, proposal.Length, proposal.Review);
        if (limited) throw new LinkDetectionWork.LimitReached();
    }

    private bool TryPass(List<Proposal> proposals)
    {
        foreach (var part in _parts) part.Reset();
        try
        {
            if (_scope == LinkDetectionScope.UnlinkedOnly) ClaimExisting(_project);
            Pass(proposals);
            return true;
        }
        catch (LinkDetectionWork.LimitReached)
        {
            return false;
        }
    }

    private bool AddCuts(List<Proposal> proposals)
    {
        var groups = proposals.Where(p => !p.Review)
            .Select(p => ((IReadOnlyList<(int, int)>)[.. p.Copies.Select(c => (c.Part, c.First))], p.Length));
        try
        {
            var added = false;
            foreach (var (part, boundary) in RepeatSplits.Find(_repeatBars, _measures.Count, groups, _work.Charge))
                added |= _parts[part].Cut(boundary);
            return added;
        }
        catch (LinkDetectionWork.LimitReached)
        {
            return false;
        }
    }

    /// <summary>Adds the nested one-bar groups; a plain one-bar group they hold whole is folded into them.</summary>
    private bool TryNest(List<Proposal> proposals)
    {
        var count = _measures.Count;
        var groups = proposals.Where(p => !p.Review)
            .Select(p => ((IReadOnlyList<int>)[.. p.Copies.Select(Flat)], p.Length)).ToList();
        var proposed = groups.Count;
        var exactSingles = new HashSet<int>();
        var byArrangement = Enumerable.Range(0, _parts.Length).ToDictionary(i => _parts[i].Arrangement);
        foreach (var group in _project.LinkGroups)
        {
            var firsts = new List<int>();
            var length = 0;
            foreach (var placement in group.Placements)
            {
                if (placement.Arrangement is not { } a || !byArrangement.TryGetValue(a, out var part) ||
                    !LinkRuns.TryResolve(_measures, placement, out var first, out var span))
                {
                    continue;
                }

                firsts.Add(part * count + first);
                length = span;
                if (span == 1) exactSingles.Add(part * count + first);
            }

            if (firsts.Count > 1) groups.Add((firsts, length));
        }

        try
        {
            var nested = NestedRepeats.Find(_repeatBars, groups,
                at => !exactSingles.Contains(at) && _parts[at / count].CanNest(at % count),
                at => RunKey(new Copy(at / count, at % count), 1));
            var merged = NestedRepeats.Merge(nested, count, groups, proposed, (members, length) =>
                members.Select(at => RunKey(new Copy(at / count, at % count), length).Rule).Distinct().Count() == 1);
            foreach (var (members, length) in merged)
            {
                var copies = members.Select(at => new Copy(at / count, at % count)).ToList();
                proposals.RemoveAll(p => !p.Review && p.Length <= length && Inside(p.Copies, p.Length, copies, length));
                proposals.Add(new Proposal(copies, length, false));
            }

            return true;
        }
        catch (LinkDetectionWork.LimitReached)
        {
            return false;
        }
    }

    /// <summary>Whether every copy of a group sits at one offset inside a copy of the nested run, so the run makes it redundant.</summary>
    private static bool Inside(List<Copy> copies, int length, List<Copy> runs, int runLength)
    {
        for (var offset = 0; offset + length <= runLength; offset++)
        {
            if (copies.All(c => runs.Contains(c with { First = c.First - offset }))) return true;
        }

        return false;
    }

    private void Pass(List<Proposal> proposals)
    {
        var maxLength = _distinctSourceRun.Max();
        if (_parts.Length == 1) maxLength = Math.Min(maxLength, _measures.Count / 2);
        for (var length = maxLength; length > 0; length--)
        {
            var buckets = Collect(length)
                .OrderBy(c => c.RequiresReview)
                .ThenByDescending(c => c.AlignedEnds)
                .ThenByDescending(c => c.Copies.Count)
                .ThenByDescending(c => c.FromRepeats)
                .ThenBy(c => c.Copies[0].Part).ThenBy(c => c.Copies[0].First);
            foreach (var candidate in buckets)
            {
                var free = Disjoint(candidate.Copies, length, candidate.RequiresReview);
                if (free.Count < 2) continue;
                if (candidate.RequiresReview && !Differ(free, length)) continue;
                var selectedLength = length;
                if (!candidate.RequiresReview && free.Count == 2 && MoreCopiesWithinPair(free, length) is { } shared)
                    (selectedLength, free) = shared;
                foreach (var copy in free)
                {
                    var part = _parts[copy.Part];
                    if (candidate.RequiresReview) part.MarkReviewed(copy.First, selectedLength);
                    else part.Claim(copy.First, selectedLength);
                }

                proposals.Add(new Proposal(free, selectedLength, candidate.RequiresReview));
            }

            if (length > 1) _runKeys.Remove(length);
        }
    }

    /// <summary>
    ///     Before taking a pair, check whether it would block a section that overlaps both copies
    ///     and covers more measures with three copies alone. Use the longest such section.
    /// </summary>
    private (int Length, List<Copy> Copies)? MoreCopiesWithinPair(List<Copy> pair, int length)
    {
        if (length <= MinimumWholeSectionLength) return null;
        var longestPossible = Math.Min(length - 1, _parts.Sum(p => p.FreeMeasureCount) / 3);
        for (var shorter = longestPossible; shorter >= MinimumWholeSectionLength && shorter * 3 > length * 2; shorter--)
        {
            var rival = BestSharedWindow(pair, length, shorter);
            if (rival != null) return (shorter, rival);
        }

        return null;
    }

    private List<Copy>? BestSharedWindow(List<Copy> pair, int length, int shorter)
    {
        List<Copy>? best = null;
        (int Aligned, int Copies) bestRank = default;
        for (var offset = 0; offset <= length - shorter; offset++)
        {
            _work.Charge();
            var first = pair[0] with { First = pair[0].First + offset };
            var second = pair[1] with { First = pair[1].First + offset };
            if (!CanStart(first, shorter) || !CanStart(second, shorter)) continue;
            var copies = SharedCopies(first, shorter);
            if (copies.Count < 3 || !pair.All(p => copies.Any(c => Overlaps(p, length, c, shorter)))) continue;
            var rank = (Alignment(copies, shorter), copies.Count);
            if (rank.CompareTo(bestRank) <= 0) continue;
            (best, bestRank) = (copies, rank);
        }

        return best;
    }

    private List<Copy> SharedCopies(Copy first, int length)
    {
        var startId = _exactWindows.Key(Flat(first), 1).Head;
        var starts = _exactStarts[startId];
        if (starts.Count < 3) return [];
        _work.Charge(starts.Count);
        var exact = _exactWindows.Key(Flat(first), length);
        var matching = starts.Where(c => CanStart(c, length) && _exactWindows.Key(Flat(c), length) == exact).ToList();
        if (Disjoint(matching, length).Count < 3) return [];
        var verified = RunKey(first, length);
        return Disjoint(matching.Where(c => RunKey(c, length).Equals(verified)), length);
    }

    private bool CanStart(Copy copy, int length) =>
        copy.First <= _measures.Count - length && _parts[copy.Part].CanStart(copy.First, length, _distinctSourceRun[copy.First]);

    private static bool Overlaps(Copy a, int lengthA, Copy b, int lengthB) =>
        a.Part == b.Part && a.First < b.First + lengthB && b.First < a.First + lengthA;

    private IEnumerable<Candidate> Collect(int length)
    {
        var buckets = new Dictionary<(int Head, int Tail), List<Copy>>();
        var written = new Dictionary<(int Head, int Tail), List<Copy>>();
        for (var p = 0; p < _parts.Length; p++)
        {
            var part = _parts[p];
            for (var m = 0; m <= _measures.Count - length; m++)
            {
                _work.Charge();
                if (!part.CanStart(m, length, _distinctSourceRun[m])) continue;
                var copy = new Copy(p, m);
                Add(buckets, _windows.Key(Flat(copy), length), copy);
                if (_writtenWindows is { } source && _replayedWrittenBars.Contains(_source![m]))
                    Add(written, source.Key(m, length), copy);
            }
        }

        var replays = written.Values.Where(m => m.Any(c => c.First != m[0].First));
        foreach (var matches in buckets.Values.Concat(replays).Where(m => m.Count > 1))
        {
            if (Disjoint(matches, length).Count < 2) continue;
            foreach (var candidate in Verify(matches, length)) yield return candidate;
        }
    }

    private IEnumerable<Candidate> Verify(List<Copy> matches, int length)
    {
        var exact = matches.GroupBy(c => _exactWindows.Key(Flat(c), length)).ToList();
        var differs = exact.Count > 1;
        foreach (var bucket in exact)
        {
            if (Disjoint(bucket, length).Count < 2) continue;
            var verified = bucket.GroupBy(c => RunKey(c, length)).ToList();
            differs |= verified.Count > 1;
            foreach (var group in verified)
            {
                var copies = Disjoint(group, length);
                if (copies.Count < 2) continue;
                yield return new Candidate(group.ToList(), FromRepeats(copies, length), Alignment(copies, length), false);
            }
        }

        if (differs && Disjoint(matches, length, review: true).Count >= 2)
            yield return new Candidate(matches, FromRepeats(matches, length), Alignment(matches, length), true);
    }

    private int Flat(Copy copy) => copy.Part * _measures.Count + copy.First;

    private static void Add(Dictionary<(int Head, int Tail), List<Copy>> buckets, (int Head, int Tail) key, Copy copy)
    {
        if (!buckets.TryGetValue(key, out var matches)) buckets[key] = matches = [];
        matches.Add(copy);
    }

    private bool Differ(List<Copy> copies, int length)
    {
        var first = copies[0];
        var key = RunKey(first, length);
        return copies.Skip(1).Any(c => !RunKey(c, length).Equals(key));
    }

    private (MeasureContentKey Rule, MeasureContentKey Exact) RunKey(Copy copy, int length)
    {
        if (!_runKeys.TryGetValue(length, out var keys)) _runKeys[length] = keys = [];
        if (keys.TryGetValue(copy, out var key)) return key;
        _work.Charge(_parts[copy.Part].VerificationWork(copy.First, length));
        RunKeyBuilds++;
        return keys[copy] = _parts[copy.Part].Verification.KeyFor(copy.First, length);
    }

    private List<Copy> Disjoint(IEnumerable<Copy> copies, int length, bool review = false)
    {
        var result = new List<Copy>();
        var last = new Copy(-1, -length);
        foreach (var copy in copies)
        {
            _work.Charge();
            if (copy.Part == last.Part && copy.First < last.First + length) continue;
            var part = _parts[copy.Part];
            if (review ? !part.IsUnreviewed(copy.First, length) : !part.IsFree(copy.First, length)) continue;
            result.Add(copy);
            last = copy;
        }

        return result;
    }

    private LinkSuggestion Propose(List<Copy> copies, int length, bool review)
    {
        var runs = copies.Select(c => new LinkRunRequest(_parts[c.Part].Arrangement, _measures[c.First].Start,
            _measures[c.First + length - 1].End)).ToList();
        var name = copies[0].First < _provenance.Count ? _provenance[copies[0].First].Marker : "";
        if (string.IsNullOrWhiteSpace(name)) name = SectionMarkers.At(runs[0].Arrangement, runs[0].Start);
        return new LinkSuggestion(name, length, runs, [.. copies.Select(c => c.First)], FromRepeats(copies, length))
        {
            AlignedEdges = Alignment(copies, length),
            RequiresReview = review
        };
    }

    private int Alignment(List<Copy> copies, int length) =>
        copies.Sum(c => _parts[c.Part].Alignment(c.First, length));

    private IEnumerable<FractionalPosition> PreferredEdges()
    {
        for (var i = 0; i < _provenance.Count; i++)
        {
            var bar = _provenance[i];
            if (bar.StartsBlock || bar.Marker.Length > 0) yield return _measures[i].Start;
            if (bar.EndsBlock) yield return _measures[i].End;
        }
    }

    private bool FromRepeats(List<Copy> copies, int length)
    {
        if (_writtenWindows == null || _source == null) return false;
        return copies.Select(c => c.First).Distinct().Where(m => _source[m] >= 0)
            .GroupBy(m => _writtenWindows.Key(m, length)).Any(g => g.Skip(1).Any());
    }

    private void ClaimExisting(ChartProject project)
    {
        var byArrangement = _parts.ToDictionary(p => p.Arrangement);
        foreach (var placement in project.LinkGroups.SelectMany(g => g.Placements))
        {
            if (placement.Arrangement is { } a && byArrangement.TryGetValue(a, out var part) &&
                LinkRuns.TryResolve(_measures, placement, out var first, out var count))
            {
                part.Claim(first, count);
            }
        }
    }

    private int[] BarIds(Func<Part, MeasureContentKey[]> keys)
    {
        var ids = new Dictionary<MeasureContentKey, int>();
        var bars = new int[_parts.Length * _measures.Count];
        var at = 0;
        foreach (var part in _parts)
        {
            foreach (var key in keys(part))
            {
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                bars[at++] = id;
            }
        }

        return bars;
    }

    private static int[] SourceRunLengths(int count, IReadOnlyList<int>? source)
    {
        var run = new int[count];
        var next = new Dictionary<int, int>();
        var end = count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (source != null && source[i] >= 0)
            {
                var id = source[i];
                if (next.TryGetValue(id, out var at)) end = Math.Min(end, at);
                next[id] = i;
            }

            run[i] = end - i;
        }

        return run;
    }

    private readonly record struct Copy(int Part, int First);

    private sealed record Proposal(List<Copy> Copies, int Length, bool Review);

    private sealed record Candidate(List<Copy> Copies, bool FromRepeats, int AlignedEnds, bool RequiresReview);

    private sealed class Part
    {
        private readonly IReadOnlyList<MeasureSpan> _measures;
        private readonly AutoLinkEdges _edges;
        private readonly HashSet<FractionalPosition> _markers;
        private readonly bool[] _claimed;
        private readonly bool[] _cut;
        private readonly int[] _unreviewed;
        private readonly int[] _free;
        private readonly int[] _soundCounts;

        public Part(Arrangement arrangement, IList<BeatMarker> beats, IReadOnlyList<MeasureSpan> measures,
            MeasureContentOptions rule, IEnumerable<FractionalPosition> preferred)
        {
            Arrangement = arrangement;
            _measures = measures;
            var candidate = MeasureContentIndex.ForHardestLevel(arrangement, beats,
                rule with { IncludeAnchors = false, IncludeHandShapes = false });
            Verification = new AutoLinkVerification(arrangement, beats, rule);
            BarKeys = [.. Enumerable.Range(0, measures.Count).Select(m => candidate.KeyFor(m))];
            ExactBarKeys = [.. Enumerable.Range(0, measures.Count).Select(Verification.ExactBarKey)];
            _soundCounts = new int[measures.Count + 1];
            for (var m = 0; m < measures.Count; m++)
                _soundCounts[m + 1] = _soundCounts[m] + ExactBarKeys[m].SoundCount;
            _edges = new AutoLinkEdges(arrangement, measures);
            _markers = arrangement.EventPoints.Where(p => p.Section.Length > 0 || p.Phrase.Length > 0)
                .Select(p => p.Position).Concat(arrangement.PhraseIterations.Select(p => p.Position)).Concat(preferred).ToHashSet();
            _claimed = new bool[measures.Count];
            _cut = new bool[measures.Count + 1];
            _free = new int[measures.Count + 1];
            _unreviewed = new int[measures.Count + 1];
            Reset();
        }

        /// <summary>Frees every measure again (a new search round), keeping the cuts.</summary>
        public void Reset()
        {
            Array.Clear(_claimed);
            for (var m = _measures.Count - 1; m >= 0; m--) _free[m] = _unreviewed[m] = Next(_free, m);
        }

        /// <summary>No run may cross <paramref name="boundary" /> from now on; false when it already was a cut.</summary>
        public bool Cut(int boundary)
        {
            if (boundary <= 0 || boundary >= _measures.Count || _cut[boundary]) return false;
            return _cut[boundary] = true;
        }

        /// <summary>Whether the bar may be a one-bar placement nested inside a longer one.</summary>
        public bool CanNest(int measure) =>
            !BarKeys[measure].IsEmpty && _edges.IsSafe(_measures[measure].Start) && _edges.IsSafe(_measures[measure].End);

        private int Next(int[] run, int m) => _cut[m + 1] ? 1 : run[m + 1] + 1;

        public Arrangement Arrangement { get; }
        public AutoLinkVerification Verification { get; }
        public MeasureContentKey[] BarKeys { get; }
        public MeasureContentKey[] ExactBarKeys { get; }
        public int FreeMeasureCount => _claimed.Count(taken => !taken);

        public bool CanStart(int first, int length, int sourceLimit) =>
            length <= sourceLimit && IsFree(first, length) && !BarKeys[first].IsEmpty && HasEndContent(first, length) &&
            _edges.IsSafe(_measures[first].Start) && _edges.IsSafe(_measures[first + length - 1].End);

        private bool HasEndContent(int first, int length) =>
            !BarKeys[first + length - 1].IsEmpty || !_edges.IsSafe(_measures[first + length - 1].Start);

        public bool IsFree(int first, int length) => _free[first] >= length;

        public bool IsUnreviewed(int first, int length) => _unreviewed[first] >= length;

        public int VerificationWork(int first, int length) => length + _soundCounts[first + length] - _soundCounts[first];

        public void MarkReviewed(int first, int length)
        {
            Array.Clear(_unreviewed, first, length);
            for (var m = first - 1; m >= 0 && _unreviewed[m] != 0; m--)
                _unreviewed[m] = Next(_unreviewed, m);
        }

        public void Claim(int first, int length)
        {
            Array.Fill(_claimed, true, first, length);
            for (var m = first + length - 1; m >= 0; m--)
            {
                var free = _claimed[m] ? 0 : Next(_free, m);
                if (free == _free[m] && m < first) break;
                _free[m] = free;
            }
        }

        public int Alignment(int first, int length) =>
            (_markers.Contains(_measures[first].Start) ? 1 : 0) + (_markers.Contains(_measures[first + length - 1].End) ? 1 : 0);
    }
}
