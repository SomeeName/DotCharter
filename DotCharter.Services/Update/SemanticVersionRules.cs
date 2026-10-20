using System.Text.RegularExpressions;

namespace DotCharter.Services.Update;

/// <summary>Pure SemVer validation and precedence, independent of assembly version numbers.</summary>
internal static class SemanticVersionRules
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var match = Pattern.Match(text);
        if (!match.Success) return null;
        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(p => IsNumeric(p) && p.Length > 1 && p[0] == '0')) return null;
        return text.Split('+')[0];
    }

    internal static int Compare(string a, string b)
    {
        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumeric(left.Core[i], right.Core[i]);
            if (comparison != 0) return comparison;
        }
        if (left.Pre.Length == 0 || right.Pre.Length == 0)
            return (left.Pre.Length == 0).CompareTo(right.Pre.Length == 0);
        for (var i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            var comparison = CompareIdentifier(left.Pre[i], right.Pre[i]);
            if (comparison != 0) return comparison;
        }
        return left.Pre.Length.CompareTo(right.Pre.Length);
    }

    private static (string[] Core, string[] Pre) Parts(string version)
    {
        var parsed = ParseTag(version) ?? throw new ArgumentException("Expected a SemVer version.", nameof(version));
        var split = parsed.Split('-', 2);
        return (split[0].Split('.'), split.Length == 1 ? [] : split[1].Split('.'));
    }

    private static bool IsNumeric(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    private static int CompareNumeric(string a, string b) =>
        a.Length == b.Length ? string.CompareOrdinal(a, b) : a.Length.CompareTo(b.Length);

    private static int CompareIdentifier(string a, string b)
    {
        var leftNumeric = IsNumeric(a);
        var rightNumeric = IsNumeric(b);
        if (leftNumeric && rightNumeric) return CompareNumeric(a, b);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }
}
