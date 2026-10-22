namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     One <see cref="HandShapeSuggester.Suggest" /> call: the merged sound list, the sorted bar and
///     anchor starts, the grip being grown, and a memo of which declared template each grip reuses.
///     Nothing is allocated per sound or per join: the grip is two reused arrays; a grip the hand can
///     hold comes from <see cref="HandShapeGripMemo" /> (kept across runs) instead of a fingered probe
///     template, and the template a grip reuses from this run's memo (the same arpeggio repeated
///     through a song is looked up once).
/// </summary>
internal sealed class HandShapeSuggestPass : IDisposable
{
    /// <summary>How far a bar reaches past a tail-less last sound with nothing after it.</summary>
    private static readonly FractionalPosition OneBeat = new(1);

    private readonly Arrangement _arrangement;
    private readonly HandShapeSuggestOptions _options;
    private readonly HandShapeSounds _sounds;
    private readonly HandShapeCoverageIndex _coverage;
    private readonly List<FractionalPosition> _barStarts;
    private readonly List<FractionalPosition> _anchors;
    private readonly FractionalPosition _maxGap;
    private readonly int _capo;
    private readonly int _stringCount;
    private readonly GripBuffer _grip = new();
    private readonly ChordTemplate _probe = new();
    private readonly Dictionary<ulong, ChordTemplate?> _existingMemo = [];

    public HandShapeSuggestPass(Arrangement arrangement, ChartLevel level, HandShapeSuggestOptions options)
    {
        _arrangement = arrangement;
        _options = options;
        _sounds = HandShapeSounds.Build(level, ChordTemplateResolver.IndexById(arrangement.ChordTemplates));
        _coverage = new HandShapeCoverageIndex(level.HandShapes);
        _barStarts = SortedStarts(level.HandShapes, static h => h.Position);
        _anchors = SortedStarts(level.Anchors, static a => a.Position);
        _maxGap = new FractionalPosition(new Fraction((long)Math.Round(options.MaxGap * 48), 48));
        _capo = arrangement.Capo;
        _stringCount = arrangement.StringCount > 0 ? arrangement.StringCount : 6;
    }

    public void Dispose() => _sounds.Dispose();

    public List<SuggestedHandShape> Run()
    {
        var found = new List<SuggestedHandShape>();
        var i = 0;
        while (i < _sounds.Count)
        {
            if (_coverage.Covers(_sounds.Position(i)) || _sounds.FretCount(i) == 0)
            {
                i++;
                continue;
            }

            var (runEnd, reach) = ScanRun(i);
            if (Propose(i, runEnd, reach) is { } suggestion) found.Add(suggestion);
            i = runEnd + 1;
        }

        return found;
    }

    /// <summary>The last sound of the run that starts at <paramref name="first" />, and how far its
    /// sounds reach; the grip it holds is left in the buffer.</summary>
    private (int RunEnd, FractionalPosition Reach) ScanRun(int first)
    {
        _grip.Load(_sounds, first);
        var chordShape = _sounds.IsChord(first) ? first : -1;
        var runEnd = first;
        var reach = _sounds.End(first);
        var limit = reach + _maxGap;
        for (var j = first + 1; j < _sounds.Count; j++)
        {
            if (!Continues(first, j, runEnd, limit, ref chordShape)) break;

            runEnd = j;
            if (_sounds.End(j).CompareTo(reach) <= 0) continue;
            reach = _sounds.End(j);
            limit = reach + _maxGap;
        }

        return (runEnd, reach);
    }

    /// <summary>True when sound <paramref name="next" /> joins the run: nothing covers it, the run's
    /// last sound does not slide, the gap and the bar and anchor starts allow it, a chord shape stays
    /// ONE shape and one hand can hold the grown grip (which is then grown).</summary>
    private bool Continues(int first, int next, int runEnd, FractionalPosition limit, ref int chordShape)
    {
        var position = _sounds.Position(next);
        if (_coverage.Covers(position) || _sounds.Slides(runEnd)) return false;
        if (position.CompareTo(limit) > 0) return false;
        if (StartsIn(_barStarts, _sounds.Position(first), position)) return false;
        if (StartsIn(_anchors, _sounds.Position(first), position)) return false;
        if (_sounds.IsChord(next) && _sounds.FretCount(next) > 0)
        {
            if (chordShape < 0) chordShape = next;
            else if (_options.OneChordShape && !Nested(chordShape, next)) return false;
        }

        return TryJoin(next);
    }

    private SuggestedHandShape? Propose(int first, int runEnd, FractionalPosition reach)
    {
        var (sounds, notes) = Count(first, runEnd);
        if (sounds < _options.MinSounds || notes == 0 || _grip.Count < _options.MinStrings) return null;

        var existing = ExistingFor();
        if (existing == null && _grip.Count < _options.MinStringsAlone) return null;

        return new SuggestedHandShape(_sounds.Position(first), BarEnd(runEnd, reach), _grip.ToDictionary(), existing, sounds,
            notes);
    }

    /// <summary>The template the run's grip would reuse, asked of the memo first: the same figure
    /// repeated through a song is looked up once.</summary>
    private ChordTemplate? ExistingFor()
    {
        var packed = _grip.TryPack(out var key);
        if (packed && _existingMemo.TryGetValue(key, out var known)) return known;

        var existing = FindExisting();
        if (packed) _existingMemo[key] = existing;
        return existing;
    }

    /// <summary>
    ///     <see cref="HandShapeSuggester.ExistingTemplate" /> for the grip in the buffer, read straight
    ///     off the arrays: a PLAIN template with exactly these frets, else the closest PLAIN one that
    ///     contains them (fewest added strings, then lowest id). The dictionary version is the
    ///     contract; the equivalence tests hold the two together.
    /// </summary>
    private ChordTemplate? FindExisting()
    {
        var templates = _arrangement.ChordTemplates;
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            if (t.IsArpeggio || t.GhostMask != 0 || t.Frets.Count != _grip.Count) continue;
            if (_grip.IsHeldBy(t.Frets)) return t;
        }

        if (_grip.Count == 0) return null;

        ChordTemplate? closest = null;
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            if (t.IsArpeggio || t.Frets.Count <= _grip.Count || !_grip.IsHeldBy(t.Frets)) continue;
            if (closest == null || t.Frets.Count < closest.Frets.Count
                || (t.Frets.Count == closest.Frets.Count && t.Id < closest.Id))
            {
                closest = t;
            }
        }

        return closest;
    }

    private (int Sounds, int Notes) Count(int first, int last)
    {
        int held = 0, notes = 0;
        for (var k = first; k <= last; k++)
        {
            if (_sounds.FretCount(k) == 0) continue;
            held++;
            if (!_sounds.IsChord(k)) notes++;
        }

        return (held, notes);
    }

    /// <summary>One shape contains the other: a full strum and a partial strum of it are the same chord.</summary>
    private bool Nested(int a, int b) => Within(a, b) || Within(b, a);

    private bool Within(int inner, int outer)
    {
        for (var k = 0; k < _sounds.FretCount(inner); k++)
        {
            if (_sounds.FretOn(outer, _sounds.StringAt(inner, k)) != _sounds.FretAt(inner, k)) return false;
        }

        return true;
    }

    /// <summary>Adds sound <paramref name="next" />'s frets to the grip when one hand can still hold
    /// the result; leaves the grip untouched otherwise.</summary>
    private bool TryJoin(int next)
    {
        var grows = false;
        for (var k = 0; k < _sounds.FretCount(next); k++)
        {
            var at = _grip.IndexOf(_sounds.StringAt(next, k));
            if (at < 0) grows = true;
            else if (_grip.FretAt(at) != _sounds.FretAt(next, k)) return false;
        }

        if (!grows) return true;

        var before = _grip.Count;
        for (var k = 0; k < _sounds.FretCount(next); k++)
        {
            if (_grip.IndexOf(_sounds.StringAt(next, k)) < 0) _grip.Add(_sounds.StringAt(next, k), _sounds.FretAt(next, k));
        }

        if (CanHold()) return true;

        _grip.Truncate(before);
        return false;
    }

    private bool CanHold()
    {
        var packed = _grip.TryPack(out var key);
        if (packed && HandShapeGripMemo.Shared.TryGet(key, _capo, _stringCount, _options.MaxFretSpread, out var known)) return known;

        var held = HandShapeSuggester.CanHold(_grip.Strings, _grip.Frets, _grip.Count, _capo, _stringCount,
            _options.MaxFretSpread, _probe);
        if (packed) HandShapeGripMemo.Shared.Store(key, _capo, _stringCount, _options.MaxFretSpread, held);
        return held;
    }

    /// <summary>The bar ends at its last sound's tail — or, for a tail-less last sound, where the
    /// next sound starts (one beat at most) — and never past the next sound, bar or anchor.</summary>
    private FractionalPosition BarEnd(int last, FractionalPosition reach)
    {
        var lastPosition = _sounds.Position(last);
        FractionalPosition? limit = last + 1 < _sounds.Count ? _sounds.Position(last + 1) : null;
        limit = Earliest(limit, FirstAfter(_barStarts, lastPosition));
        limit = Earliest(limit, FirstAfter(_anchors, lastPosition));

        var end = reach.CompareTo(lastPosition) > 0 ? reach : lastPosition + OneBeat;
        if (limit is { } l && l.CompareTo(end) < 0) end = l;
        return end;
    }

    private static List<FractionalPosition> SortedStarts<T>(IList<T> items, Func<T, FractionalPosition> position)
    {
        var starts = new List<FractionalPosition>(items.Count);
        var sorted = true;
        for (var i = 0; i < items.Count; i++)
        {
            var at = position(items[i]);
            if (i > 0 && starts[i - 1].CompareTo(at) > 0) sorted = false;
            starts.Add(at);
        }

        if (!sorted) starts.Sort();
        return starts;
    }

    /// <summary>True when a sorted list holds a position in (<paramref name="after" />,
    /// <paramref name="through" />].</summary>
    private static bool StartsIn(List<FractionalPosition> sorted, FractionalPosition after, FractionalPosition through) =>
        FirstAfter(sorted, after) is { } p && p.CompareTo(through) <= 0;

    private static FractionalPosition? FirstAfter(List<FractionalPosition> sorted, FractionalPosition after)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sorted.Count ? sorted[lo] : null;
    }

    private static FractionalPosition? Earliest(FractionalPosition? a, FractionalPosition? b)
    {
        if (a is not { } x) return b;
        if (b is not { } y) return a;
        return x.CompareTo(y) <= 0 ? x : y;
    }

    /// <summary>The string → fret grip a run holds, in the order its frets were first played.</summary>
    private sealed class GripBuffer
    {
        private const int MaxPackedString = 7;
        private const int MaxPackedFret = 254;

        private int[] _strings = new int[8];
        private int[] _frets = new int[8];

        public int[] Strings => _strings;

        public int[] Frets => _frets;

        public int Count { get; private set; }

        public int FretAt(int slot) => _frets[slot];

        public int IndexOf(int stringIndex)
        {
            for (var k = 0; k < Count; k++)
            {
                if (_strings[k] == stringIndex) return k;
            }

            return -1;
        }

        public void Add(int stringIndex, int fret)
        {
            if (Count == _strings.Length)
            {
                Array.Resize(ref _strings, Count * 2);
                Array.Resize(ref _frets, Count * 2);
            }

            _strings[Count] = stringIndex;
            _frets[Count++] = fret;
        }

        public void Truncate(int count) => Count = count;

        /// <summary>True when every string of the grip is in <paramref name="frets" /> at the same fret.</summary>
        public bool IsHeldBy(Dictionary<int, int> frets)
        {
            for (var k = 0; k < Count; k++)
            {
                if (!frets.TryGetValue(_strings[k], out var fret) || fret != _frets[k]) return false;
            }

            return true;
        }

        public void Load(HandShapeSounds sounds, int sound)
        {
            Count = 0;
            for (var k = 0; k < sounds.FretCount(sound); k++) Add(sounds.StringAt(sound, k), sounds.FretAt(sound, k));
        }

        /// <summary>The grip as one number — a fret plus one per string byte — when every string is
        /// 0-7 and every fret 0-254, else false. The same shape in another order is the same number.</summary>
        public bool TryPack(out ulong key)
        {
            key = 0;
            for (var k = 0; k < Count; k++)
            {
                if ((uint)_strings[k] > MaxPackedString || (uint)_frets[k] > MaxPackedFret) return false;
                key |= (ulong)(_frets[k] + 1) << (8 * _strings[k]);
            }

            return true;
        }

        public Dictionary<int, int> ToDictionary()
        {
            var grip = new Dictionary<int, int>(Count);
            for (var k = 0; k < Count; k++) grip[_strings[k]] = _frets[k];
            return grip;
        }
    }
}
