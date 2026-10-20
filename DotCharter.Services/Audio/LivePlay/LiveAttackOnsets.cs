namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
/// CQT mode's timing evidence: the attack onsets the input analyzer reported, kept
///     while a CQT confirmation can still be timed from one. The CQT chart verifier says WHAT
///     was played; the attack detector — whose events still reach <see cref="LiveNoteMatcher.OfferPitches" />
///     in CQT mode — says WHEN, with the same sample-accurate, latency-compensated time the
///     classic path grades from.
///     <para>
///         <b>An onset is REPORTED long after it happened</b> (<c>JuceGuitarAnalyzer.LatencySamples</c>,
///         ~190 ms, plus a tick), while the CQT confirms a note ~100 ms after its chart time. So
///         at confirmation the onset that times the note is usually not here yet: the matcher
///         credits the hit untimed right then and the onset GRADES it when it arrives (see
///         <see cref="LiveNoteMatcher.AwaitAttackOnsets" />). Both directions go through the same
///         cost: the onset nearest the chart time, where an onset whose pitch candidates do not
///         name the sound counts <see cref="PitchMismatchPenaltyMs" /> further away.
///     </para>
///     <para>
///         An onset times ONE attack: it is claimed by the chart time it timed, and only sounds at
///         that same chart time (a double stop spelled as two notes) may share it. Bounded and
///         allocation-free after construction; single-threaded, like the matcher.
///     </para>
///     <para>
/// The matcher keeps a SECOND instance for the detector's provisional onsets: those
///         carry no pitch (every sound pays the penalty alike, so the nearest wins) and a key — the
///         detector position — that the finalized event, or a withdrawal, removes them by
///         (<see cref="RemoveKey" />). The two sets never mix: only a finalized onset grades.
///     </para>
/// </summary>
internal sealed class LiveAttackOnsets
{
    /// <summary>Onsets kept at most — a few seconds of fast playing; the oldest goes first.</summary>
    public const int Capacity = 64;

    /// <summary>
    ///     How much further away an onset counts when none of its pitch candidates names the
    ///     sound. Enough to keep a player who is consistently late in a 16th-note run at 120 bpm
    ///     (125 ms apart) on their own notes, where nearest-alone hands each onset to the NEXT
    ///     note and reads "early"; not enough to beat a much nearer onset whose pitch the
    ///     detector merely misread. An onset with no candidates (a percussive attack) pays it for
    ///     every sound alike, so it falls back to nearest.
    /// </summary>
    public const double PitchMismatchPenaltyMs = 100;

    private const int PitchesPerOnset = 3;

    private readonly Onset[] _items = new Onset[Capacity];
    private int _count;

    public int Count => _count;

    /// <summary>Records one reported onset with its strongest pitch candidates; returns its
    /// index (valid until the next <see cref="Add" />, <see cref="PruneBefore" />,
    /// <see cref="RemoveKey" /> or <see cref="Clear" />). <paramref name="key" /> (-1 = none)
    /// is what <see cref="RemoveKey" /> finds it by.</summary>
    public int Add(double timeMs, ReadOnlySpan<LivePitch> pitches, long key = -1)
    {
        if (_count == Capacity) RemoveFront(1);
        var onset = new Onset
        {
            TimeMs = timeMs,
            Key = key,
            ClaimedByMs = double.NaN,
            P0 = pitches.Length > 0 ? pitches[0].Midi : -1,
            P1 = pitches.Length > 1 ? pitches[1].Midi : -1,
            P2 = pitches.Length > 2 ? pitches[2].Midi : -1,
        };

        // Kept sorted by time; the analyzer reports in order, so this is an append.
        var i = _count;
        while (i > 0 && _items[i - 1].TimeMs > timeMs)
        {
            _items[i] = _items[i - 1];
            i--;
        }

        _items[i] = onset;
        _count++;
        return i;
    }

    /// <summary>Marks the onset as the attack of the sound(s) at <paramref name="chartTimeMs" />.</summary>
    public void Claim(int index, double chartTimeMs) => _items[index].ClaimedByMs = chartTimeMs;

    /// <summary>Drops every onset before <paramref name="timeMs" /> — no open sound's window reaches it.</summary>
    public void PruneBefore(double timeMs)
    {
        var n = 0;
        while (n < _count && _items[n].TimeMs < timeMs) n++;
        if (n > 0) RemoveFront(n);
    }

    public void Clear() => _count = 0;

    /// <summary>Drops the onset added with <paramref name="key" />; false when none was (already
    /// pruned, or never added).</summary>
    public bool RemoveKey(long key)
    {
        if (key < 0) return false;
        for (var i = 0; i < _count; i++)
        {
            if (_items[i].Key != key) continue;
            Array.Copy(_items, i + 1, _items, i, _count - i - 1);
            _count--;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     How well onset <paramref name="index" /> would time <paramref name="sound" />, or
    ///     <see cref="double.PositiveInfinity" /> when it may not: outside
    ///     [chart − <paramref name="earlyMs" />, chart + <paramref name="lateMs" />], or already
    ///     claimed by another chart time.
    /// </summary>
    public double CostFor(int index, ExpectedSound sound, double earlyMs, double lateMs)
    {
        ref readonly var onset = ref _items[index];
        var delta = onset.TimeMs - sound.TimeMs;
        if (delta < -earlyMs || delta > lateMs) return double.PositiveInfinity;
        if (!double.IsNaN(onset.ClaimedByMs) &&
            Math.Abs(onset.ClaimedByMs - sound.TimeMs) > LiveNoteMatcher.SimultaneousToleranceMs)
        {
            return double.PositiveInfinity;
        }

        return Math.Abs(delta) + (NamesPitchOf(onset, sound) ? 0 : PitchMismatchPenaltyMs);
    }

    /// <summary>
    ///     A confirmation being credited: claims the onset that best times
    ///     <paramref name="sound" /> and returns its time; false when none may.
    /// </summary>
    public bool TryClaimBest(ExpectedSound sound, double earlyMs, double lateMs, out double attackMs)
    {
        var best = BestFor(sound, earlyMs, lateMs);
        attackMs = best < 0 ? double.NaN : _items[best].TimeMs;
        if (best >= 0) Claim(best, sound.TimeMs);
        return best >= 0;
    }

    /// <summary>
    ///     An onset just reported: the hit ALREADY credited and awaiting its grade
    ///     (<see cref="ExpectedSound.AwaitingGrade" />) that onset <paramref name="index" /> times
    ///     best, from <paramref name="cursor" /> on in the chart-ordered <paramref name="expected" />
    ///     — the onset is claimed by it. Null when it times none. <paramref name="untimedOnly" />
    /// (the provisional set) skips a hit a provisional onset already times.
    /// </summary>
    public ExpectedSound? ClaimForAwaitingHit(int index, IReadOnlyList<ExpectedSound> expected, int cursor,
        double earlyMs, double lateMs, bool untimedOnly = false)
    {
        ExpectedSound? best = null;
        var bestCost = double.PositiveInfinity;
        for (var i = cursor; i < expected.Count; i++)
        {
            var sound = expected[i];
            if (sound.TimeMs - earlyMs > _items[index].TimeMs) break;
            if (!sound.AwaitingGrade || (untimedOnly && !double.IsNaN(sound.ProvisionalAttackMs))) continue;
            var cost = CostFor(index, sound, earlyMs, lateMs);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = sound;
            }
        }

        if (best != null) Claim(index, best.TimeMs);
        return best;
    }

    /// <summary>The onset that best times <paramref name="sound" />; -1 when none may.</summary>
    public int BestFor(ExpectedSound sound, double earlyMs, double lateMs)
    {
        var best = -1;
        var bestCost = double.PositiveInfinity;
        for (var i = 0; i < _count; i++)
        {
            var cost = CostFor(i, sound, earlyMs, lateMs);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = i;
            }
        }

        return best;
    }

    private static bool NamesPitchOf(in Onset onset, ExpectedSound sound)
    {
        foreach (var m in sound.MidiNotes)
        {
            if (Names(onset.P0, sound, m) || Names(onset.P1, sound, m) || Names(onset.P2, sound, m))
                return true;
        }

        return false;
    }

    private static bool Names(int played, ExpectedSound sound, int m) =>
        played >= 0 && LivePitchAcceptance.Accepts(sound, m, played);

    private void RemoveFront(int n)
    {
        Array.Copy(_items, n, _items, 0, _count - n);
        _count -= n;
    }

    private struct Onset
    {
        public double TimeMs;
        public long Key; // -1 = none
        public double ClaimedByMs; // NaN = free
        public int P0;
        public int P1;
        public int P2;
    }
}
