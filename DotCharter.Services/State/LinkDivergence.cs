using System.Globalization;

namespace DotCharter.Services.State;

/// <summary>
///     Names WHAT differs between copies of a link group, for the log line written when the copies
///     disagree. It reads the canonical text of <see cref="DotCharter.Core.Linking.MeasureContentKey" />
///     (tokens split by <c>|</c>), so it is only ever run on the mismatch path.
/// </summary>
internal static class LinkDivergence
{
    private const int MaxArrangements = 3;

    /// <summary>One short line: the arrangements holding a differing copy, the bar inside the group and the first differing item.</summary>
    public static string Describe(IReadOnlyList<(string Arrangement, string Canonical)> copies)
    {
        var reference = copies.GroupBy(c => c.Canonical, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First().Key;
        var differing = copies.Where(c => !string.Equals(c.Canonical, reference, StringComparison.Ordinal)).ToList();
        var names = differing.Select(c => c.Arrangement).Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", names.Take(MaxArrangements)) + (names.Count > MaxArrangements ? $" +{names.Count - MaxArrangements} more" : "");
        return $"Differs in {shown}: {FirstDifference(reference.Split('|'), differing[0].Canonical.Split('|'))}.";
    }

    private static string FirstDifference(string[] a, string[] b)
    {
        var bars = new List<int>();
        var beats = 0;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : null;
            var y = i < b.Length ? b[i] : null;
            if (x != null && x.StartsWith('t') && x.Length > 1 && char.IsDigit(x[1]))
            {
                bars.Add(beats);
                beats += BeatCount(x);
            }

            if (string.Equals(x, y, StringComparison.Ordinal)) continue;
            return x == null || y == null
                ? $"an item is in only one copy ({Clip(x ?? y!)})"
                : $"bar +{BarOf(bars, x, y)}: {Item(x, y)}";
        }

        return "no token differs";
    }

    private static int BeatCount(string measureToken)
    {
        var at = measureToken.LastIndexOf('x');
        return at >= 0 && int.TryParse(measureToken.AsSpan(at + 1), CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static int BarOf(List<int> barStarts, string x, string y)
    {
        var beat = BeatOf(x) ?? BeatOf(y);
        if (beat == null || barStarts.Count == 0) return 0;
        var bar = 0;
        for (var i = 0; i < barStarts.Count; i++)
        {
            if (barStarts[i] <= beat.Value) bar = i;
        }

        return bar;
    }

    private static int? BeatOf(string token)
    {
        var s = token.TrimStart('V', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (s.Length < 2 || s[0] is not ('N' or 'C' or 'M' or 'A' or 'H')) return null;
        var end = 1;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        return int.TryParse(s.AsSpan(1, end - 1), CultureInfo.InvariantCulture, out var beat) ? beat : null;
    }

    private static string Item(string x, string y)
    {
        var kind = Kind(x);
        return kind == Kind(y) ? $"{kind} {Field(x, y)}" : $"{kind} vs {Kind(y)}";
    }

    private static string Kind(string token)
    {
        var s = token.TrimStart('V', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return s.Length == 0 ? "item" : s[0] switch
        {
            'N' => "note",
            'C' => "chord",
            'M' => "mute",
            'A' => "anchor",
            'H' => "hand shape",
            't' => "time signature",
            _ => "item"
        };
    }

    private static string Field(string x, string y)
    {
        var chord = Kind(x) == "chord";
        if (Kind(x) is "anchor" or "hand shape" or "mute" or "time signature") return $"({Clip(x)} vs {Clip(y)})";
        var (hx, sx) = Split(x, chord);
        var (hy, sy) = Split(y, chord);
        if (hx != hy) return chord ? $"template/flags/fingers ({Clip(hx)} vs {Clip(hy)})" : HeadField(hx, hy);
        return sx != sy ? $"sustain/bends/technique ({Clip(sx)} vs {Clip(sy)})" : "content";
    }

    private static (string Head, string Tail) Split(string token, bool chord)
    {
        var at = token.IndexOf(chord ? '[' : '~');
        return at < 0 ? (token, "") : (token[..at], token[at..]);
    }

    private static string HeadField(string x, string y)
    {
        var px = x.Split(':');
        var py = y.Split(':');
        var names = new[] { "time", "string", "fret" };
        for (var i = 0; i < Math.Min(3, Math.Min(px.Length, py.Length)); i++)
        {
            var vx = i == 2 ? px[i].Split(',')[0] : px[i];
            var vy = i == 2 ? py[i].Split(',')[0] : py[i];
            if (vx != vy) return $"{names[i]} ({vx} vs {vy})";
        }

        return $"technique ({Clip(x)} vs {Clip(y)})";
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";
}
