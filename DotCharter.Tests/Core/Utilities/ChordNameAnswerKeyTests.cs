using System.Text.RegularExpressions;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     The two #493 answer keys, as default-gate FLOORS. Both fixtures are chord
///     data only — tuning, capo, frets, key, name and a count — stripped from the
///     owner's material by the measurement harness; neither names a song.
///     <list type="bullet">
///         <item>
///             <c>chord-names-musical.txt</c>: 258 chord diagrams typed in Guitar Pro
///             tabs, judged in <see cref="ChordNamingStyle.Musical" /> with the key
///             <see cref="ChordKeyEstimator" /> resolved for the diagram's track.
///         </item>
///         <item>
///             <c>chord-names-eof.txt</c>: 1,688 named chord templates of finished
///             charts (Editor on Fire's automatic names), one representative per
///             transposition class, judged in <see cref="ChordNamingStyle.Eof" />.
///         </item>
///     </list>
///     The floors are the measured counts. Raise them when the namer improves;
///     never lower one to turn the gate green.
/// </summary>
public class ChordNameAnswerKeyTests
{
    /// <summary>First pick right, Musical style, of 258 (was 114 before #493, sharps first).</summary>
    private const int MusicalFloor = 230;

    /// <summary>First pick exactly EOF's name, of 1,688 — the pre-#493 count, which must not drop.</summary>
    private const int EofFloor = 1529;

    [Fact]
    public void MusicalStyle_MatchesTheTypedTabNames_AtLeastAsOftenAsMeasured()
    {
        var rows = Read("chord-names-musical.txt");
        int total = 0, right = 0;
        var misses = new List<string>();
        foreach (var row in rows)
        {
            // tuning | frets | key | typed name | count
            var count = int.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture);
            var naming = new ChordNamingOptions(ChordNamingStyle.Musical, ParseKey(row[2]));
            var names = ChordNameSuggester.SuggestChordNames(row[0].Split(' '), ParseFrets(row[1]), 0, naming);
            total += count;
            if (names.Count > 0 && Canon(names[0]) == Canon(row[3])) right += count;
            else misses.Add($"{row[1]} {row[2]}: typed {row[3]}, got {(names.Count > 0 ? names[0] : "(none)")}");
        }

        Assert.Equal(258, total);
        Assert.True(right >= MusicalFloor,
            $"Musical first pick {right}/{total}, floor {MusicalFloor}. Misses:\n{string.Join("\n", misses)}");
    }

    [Fact]
    public void EofStyle_MatchesTheFinishedChartsNames_AtLeastAsOftenAsBefore()
    {
        var rows = Read("chord-names-eof.txt");
        int total = 0, right = 0;
        var misses = new List<string>();
        foreach (var row in rows)
        {
            // tuning | capo | frets | chart name | count
            var count = int.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture);
            var capo = int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
            var names = ChordNameSuggester.SuggestChordNames(row[0].Split(' '), ParseFrets(row[2]), capo);
            total += count;
            if (names.Count > 0 && names[0] == row[3]) right += count;
            else misses.Add($"{row[2]} capo {capo}: chart {row[3]}, got {(names.Count > 0 ? names[0] : "(none)")} x{count}");
        }

        Assert.Equal(1688, total);
        Assert.True(right >= EofFloor,
            $"EOF first pick {right}/{total}, floor {EofFloor}. Misses:\n{string.Join("\n", misses)}");
    }

    internal static Dictionary<int, int> ParseFrets(string frets)
    {
        var result = new Dictionary<int, int>();
        var parts = frets.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out var fret))
                result[i] = fret;
        }

        return result;
    }

    private static ChordKeySignature? ParseKey(string key)
    {
        if (key == "none") return null;
        var minor = key.EndsWith('m');
        return new ChordKeySignature(
            int.Parse(minor ? key[..^1] : key, System.Globalization.CultureInfo.InvariantCulture), minor);
    }

    private static List<string[]> Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Core", "Utilities", "Fixtures", name);
        return [.. File.ReadAllLines(path)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('|'))];
    }

    // The canonicalisation the 2026-10-08 measurement in #493 used: min/m,
    // Maj7/maj7/7M and Ø/m7b5 are the same name, parentheses do not matter.
    // Sharps and flats are NOT folded — spelling is what the key is for.
    private static string Canon(string name)
    {
        var s = name.Replace(" ", "", StringComparison.Ordinal)
            .Replace("Ø", "m7b5", StringComparison.Ordinal).Replace("ø", "m7b5", StringComparison.Ordinal);
        s = Regex.Replace(s, "7M|Maj7|maj7|M7", "maj7");
        s = s.Replace("min", "m", StringComparison.Ordinal);
        return s.Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal);
    }
}
