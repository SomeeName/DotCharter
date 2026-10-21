namespace DotCharter.Services.Export.GuitarPro;

/// <summary>
///     Spells a length in export ticks as a chain of tied note values. The note values of
///     a bar (plain, dotted, triplet, whole down to 128th) are a fixed set of tick lengths; a
///     length is WRITABLE when some chain of them sums to it exactly.
///     <para>
///         Taking the longest value that fits, over and over, is not enough: 7 ticks is a
///         32nd (6) and then 1 tick no value spells, though a triplet 32nd plus a 64th (4 + 3)
///         is exact. Dropping that last tick would make the bar come out short and
///         move every later onset in it. <see cref="TrySpell" /> keeps the longest-first
///         spelling when it is exact and otherwise finds the shortest exact chain.
///         <see cref="Fit" /> picks the written length of a sound: the writable length closest
///         to the one it wants whose rest up to the next onset is writable too.
///     </para>
/// </summary>
internal sealed class GpTickChain
{
    private readonly int[] _values;
    private readonly List<int?> _shortest = [0];
    private readonly List<int> _last = [-1];

    /// <param name="values">The note values' tick lengths, longest first.</param>
    public GpTickChain(IReadOnlyList<int> values) => _values = [.. values];

    /// <summary>The indexes (into the values) of a chain summing to exactly <paramref name="ticks" />,
    /// longest first; false when no chain does.</summary>
    public bool TrySpell(int ticks, out List<int> chain)
    {
        chain = [];
        if (ticks <= 0) return ticks == 0;

        var left = ticks;
        for (var i = 0; i < _values.Length && left > 0; i++)
        {
            while (_values[i] <= left)
            {
                chain.Add(i);
                left -= _values[i];
            }
        }

        if (left == 0) return true;

        Grow(ticks);
        if (_shortest[ticks] == null) return false;
        chain.Clear();
        for (var t = ticks; t > 0; t -= _values[_last[t]]) chain.Add(_last[t]);
        chain.Sort();
        return true;
    }

    public bool IsWritable(int ticks) => ticks >= 0 && TrySpell(ticks, out _);

    /// <summary>
    ///     The written length of a sound that wants <paramref name="wanted" /> ticks with
    ///     <paramref name="gap" /> ticks to the next onset: the writable length nearest to the
    ///     wanted one (the longer on a tie) whose remainder to the onset is a writable rest. Falls
    ///     back to <c>min(wanted, gap)</c> when nothing fits (a gap no chain can fill).
    /// </summary>
    public int Fit(int wanted, int gap)
    {
        var target = Math.Clamp(wanted, 1, Math.Max(1, gap));
        for (var d = 0; d <= gap; d++)
        {
            if (Fits(target + d, gap)) return target + d;
            if (Fits(target - d, gap)) return target - d;
        }

        return Math.Min(wanted, gap);
    }

    private bool Fits(int ticks, int gap) => ticks >= 1 && ticks <= gap && IsWritable(ticks) && IsWritable(gap - ticks);

    private void Grow(int ticks)
    {
        for (var t = _shortest.Count; t <= ticks; t++)
        {
            int? best = null;
            var bestValue = -1;
            for (var i = 0; i < _values.Length; i++)
            {
                if (_values[i] > t || _shortest[t - _values[i]] is not { } rest) continue;
                if (best != null && rest + 1 >= best) continue;
                best = rest + 1;
                bestValue = i;
            }

            _shortest.Add(best);
            _last.Add(bestValue);
        }
    }
}
