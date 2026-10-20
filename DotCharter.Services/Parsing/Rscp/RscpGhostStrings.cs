using System.Globalization;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
/// The <c>ghost</c> attribute of an <c>.rscp</c> <c>chordTemplate</c>: the string
///     indices of <see cref="ChordTemplate.GhostMask" />, ascending and comma-separated
///     (<c>ghost="1,4"</c>), the way the <c>frets</c> attribute names strings. Indices, not a
///     raw number, so the file reads like the rest of the template.
/// </summary>
internal static class RscpGhostStrings
{
    public static string Format(int mask)
    {
        var strings = new List<string>();
        for (var s = 0; s < 31; s++)
        {
            if ((mask & (1 << s)) != 0) strings.Add(s.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", strings);
    }

    /// <summary>The mask the attribute names; 0 for an absent or empty one. An entry that is
    /// not a string index in range is skipped rather than failing the load.</summary>
    public static int Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var mask = 0;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is >= 0 and < 31)
                mask |= 1 << s;
        }

        return mask;
    }
}
