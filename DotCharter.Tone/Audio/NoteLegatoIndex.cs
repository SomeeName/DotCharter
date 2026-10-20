namespace DotCharter.Tone.Audio;

/// <summary>
/// Per-string sorted index of upcoming note START times in milliseconds. Used
/// by the MIDI scheduler to extend each NoteOff so the next attack on the
/// same string (or inside the same handshape) is held legato instead of
/// cutting off with the chart-authored sustain alone.
/// </summary>
internal sealed class NoteLegatoIndex
{
    private const int StringCount = 6;
    private readonly List<double>[] _startsByString = new List<double>[StringCount];
    // Parallel to _startsByString once sorted: the note is a hammer-on, pull-off or tap.
    private readonly List<bool>[] _hopoByString = new List<bool>[StringCount];

    private NoteLegatoIndex()
    {
        for (var i = 0; i < StringCount; i++)
        {
            _startsByString[i] = new List<double>(64);
            _hopoByString[i] = new List<bool>(64);
        }
    }

    public static NoteLegatoIndex Build(IList<GuitarNote>? notes, IList<Chord>? chords, IList<BeatMarker>? beats)
    {
        var idx = new NoteLegatoIndex();
        if (beats == null || beats.Count == 0) return idx;

        var entries = new List<(double Start, bool Hopo)>[StringCount];
        for (var i = 0; i < StringCount; i++)
            entries[i] = new List<(double, bool)>(64);
        if (notes != null)
        {
            foreach (var n in notes)
            {
                var s = ((n.StringIndex % StringCount) + StringCount) % StringCount;
                entries[s].Add((n.GetTimeMs(beats), n.Hopo != HopoType.None));
            }
        }
        if (chords != null)
        {
            foreach (var c in chords)
            {
                var startMs = c.Position.GetPositionMs(beats);
                foreach (var cn in c.ChordNotes)
                {
                    var s = ((cn.StringIndex % StringCount) + StringCount) % StringCount;
                    entries[s].Add((startMs, cn.Hopo != HopoType.None));
                }
            }
        }
        for (var i = 0; i < StringCount; i++)
        {
            entries[i].Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var (start, hopo) in entries[i])
            {
                idx._startsByString[i].Add(start);
                idx._hopoByString[i].Add(hopo);
            }
        }

        return idx;
    }

    /// <summary>
    /// Returns the next note start time on <paramref name="stringIndex"/> strictly
    /// after <paramref name="afterMs"/>, or <c>null</c> if no such note exists.
    /// </summary>
    public double? NextStartOnString(int stringIndex, double afterMs) => NextOnString(stringIndex, afterMs)?.Start;

    /// <summary>
    /// The next note on <paramref name="stringIndex"/> strictly after
    /// <paramref name="afterMs"/>, and whether it is a hammer-on, pull-off or tap
    /// (sounded by the fretting hand, so the string before it must still be ringing),
    /// or <c>null</c> if no such note exists.
    /// </summary>
    public (double Start, bool Hopo)? NextOnString(int stringIndex, double afterMs)
    {
        var s = ((stringIndex % StringCount) + StringCount) % StringCount;
        var list = _startsByString[s];
        if (list.Count == 0) return null;

        // Binary search for the first element strictly > afterMs.
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (list[mid] <= afterMs + 1e-3) lo = mid + 1;
            else hi = mid;
        }
        return lo < list.Count ? (list[lo], _hopoByString[s][lo]) : null;
    }
}
