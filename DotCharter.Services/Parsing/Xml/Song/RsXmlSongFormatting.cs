using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
/// Formatting helpers that mirror rscharter's XML converters.
/// All time values are integer milliseconds → "sec.mmm" string.
/// </summary>
internal static class Fmt
{
    // ──────────────────────── Time ────────────────────────────────────────

    /// <summary>
    /// Format integer ms as seconds with 3 decimal places.
    /// Mirrors rscharter's <c>TimeConverter.toString</c>:
    ///   <c>value/1000 + "." + DecimalFormat("000").format(value%1000)</c>
    /// </summary>
    public static string Time(int ms)
    {
        if (ms < 0)
        {
            var pos = -ms;
            return $"-{pos / 1000}.{pos % 1000:D3}";
        }

        return $"{ms / 1000}.{ms % 1000:D3}";
    }

    /// <summary>
    /// Parse a time string in seconds ("12.345", "12.5", "12", "-0.5") to integer ms.
    /// <para>
    /// Any number of decimals: the fraction is padded to three digits, or rounded
    /// half-up on the fourth. rscharter's <c>TimeConverter.fromString</c> stripped
    /// the dot and read the digits as ms, which is right only for exactly three
    /// decimals — the Custom Song Toolkit writes floats ("12.5", "0"), and those
    /// imported 10× or 1000× off. Integer-only, so "0.1" carries no binary
    /// float error.
    /// </para>
    /// </summary>
    public static int ParseTime(string? s)
    {
        // Tolerant parse: every time attribute in an RS-XML flows through
        // here, and third-party exporters produce oddities ("1e3", overlong
        // digits). One bad value must degrade to 0, not abort the whole
        // arrangement load with a FormatException/OverflowException.
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var t = s.AsSpan().Trim();
        var negative = t[0] == '-';
        if (negative || t[0] == '+') t = t[1..];

        var dot = t.IndexOf('.');
        var whole = dot < 0 ? t : t[..dot];
        var fraction = t[(dot < 0 ? t.Length : dot + 1)..];
        if (whole.IsEmpty && fraction.IsEmpty) return 0;
        if (!IsDigits(whole) || !IsDigits(fraction)) return 0;

        long ms = 0;
        foreach (var c in whole)
        {
            ms = ms * 10 + (c - '0');
            if (ms > int.MaxValue / 1000) return negative ? int.MinValue : int.MaxValue;
        }

        ms *= 1000;
        var place = 100;
        for (var i = 0; i < 3 && i < fraction.Length; i++, place /= 10) ms += (fraction[i] - '0') * place;
        if (fraction.Length > 3 && fraction[3] >= '5') ms++;

        return (int)Math.Clamp(negative ? -ms : ms, int.MinValue, int.MaxValue);
    }

    private static bool IsDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (c is < '0' or > '9') return false;

        return true;
    }

    // ──────────────────────── BigDecimal helpers ───────────────────────────

    /// <summary>
    /// Format a double as a fixed-point decimal with <paramref name="scale"/> digits.
    /// Mimics BigDecimal.setScale(scale, HALF_UP).
    /// </summary>
    public static string BigDecimal(double v, int scale)
    {
        var rounded = Math.Round(v, scale, MidpointRounding.AwayFromZero);
        return rounded.ToString("F" + scale, CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a step value (bend amount) with 3 decimal places (matches EOF / RS toolkit %.3f).</summary>
    public static string Step(double v)
    {
        var rounded = Math.Round(v, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("F3", CultureInfo.InvariantCulture);
    }

    // ──────────────────────── Boolean ─────────────────────────────────────

    public static string Bool(bool b) => b ? "1" : "0";

    // ──────────────────────── DateTime ────────────────────────────────────

    /// <summary>
    /// Mirrors rscharter's <c>DateTimeConverter</c> pattern: "M-d-yy H:mm".
    /// E.g. 15 Jan 2024 09:05 → "1-15-24 9:05"
    /// </summary>
    public static string DateTime(DateTime dt)
        => $"{dt.Month}-{dt.Day}-{dt.Year % 100} {dt.Hour}:{dt.Minute:D2}";

    public static DateTime? ParseDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Pattern "M-d-yy H:mm"
        if (System.DateTime.TryParseExact(s, "M-d-yy H:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt;
        }

        return null;
    }

    // ──────────────────────── CountedList ─────────────────────────────────

    /// <summary>
    /// Writes a CountedList element: <c>&lt;name count="N"&gt;…&lt;/name&gt;</c>
    /// Self-closing when the list is empty.
    /// </summary>
    public static XElement CountedList<T>(string elementName, IReadOnlyList<T> items, Func<T, XObject> mapper)
    {
        var el = new XElement(elementName);
        el.Add(new XAttribute(E.Count, items.Count));
        foreach (var item in items)
            el.Add(mapper(item));
        return el;
    }

    /// <summary>Overload that maps to XElement.</summary>
    public static XElement CountedList<T>(string elementName, IReadOnlyList<T> items, Func<T, XElement> mapper)
        => CountedList(elementName, items, item => (XObject)mapper(item));

    /// <summary>Overload for pre-built element sequences.</summary>
    public static XElement CountedList(string elementName, IEnumerable<XElement> items)
    {
        var list = items.ToList();
        var el = new XElement(elementName);
        el.Add(new XAttribute(E.Count, list.Count));
        foreach (var item in list) el.Add(item);
        return el;
    }

    // ──────────────────────── Parse helpers ───────────────────────────────

    public static int AttrInt(XElement el, string name, int def = 0)
    {
        var val = el.Attribute(name)?.Value;
        return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    public static int? AttrIntNullable(XElement el, string name)
    {
        var val = el.Attribute(name)?.Value;
        if (val == null) return null;
        return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static double AttrDouble(XElement el, string name, double def = 0.0)
    {
        var val = el.Attribute(name)?.Value;
        return double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    public static string AttrStr(XElement el, string name, string def = "")
        => el.Attribute(name)?.Value ?? def;

    public static bool AttrBool(XElement el, string name)
        => AttrInt(el, name) != 0;

    public static XmlWriterSettings GetWriterSettings() => new()
    {
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        Encoding = new UTF8Encoding(false),
        OmitXmlDeclaration = false
    };
}
