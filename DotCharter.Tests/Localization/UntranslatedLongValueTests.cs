using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     A German or Japanese value left on its English string is invisible: it
///     resolves, it renders, nothing is logged, and the only person who can
///     tell is a reader of that language. The default gate already ratchets the
///     TOTAL — <c>LocalizationSweepTests.UntranslatedValues_DoNotGrow</c> caps
///     it at 245 (de) / 25 (ja) — but a ceiling is a budget, not a list: translate
///     one short leftover, add one long English sentence in the same change, and
///     the count is unchanged while a whole sentence ships untranslated.
///     <para>
///         This is the complementary half. Values longer than
///         <see cref="MinimumLength" /> characters that carry a real word are
///         enumerated EXACTLY, and every one of them has to be named in
///         <see cref="Allowed" /> with the reason it is identical on purpose.
///         Short shared labels ("Solo", "EQ", "Reverb", a tuning name) stay with
///         the ratchet, which is the right instrument for them — there are
///         hundreds and each is a judgement call rather than a bug.
///     </para>
///     <para>
///         Written 2026-09-04 while auditing the resx delta of
///         <c>v0.49.6..main</c>. That range added six keys and changed no
///         English value, so nothing here was failing when it was written; the
///         point is the next wave. Proved non-vacuous twice: renaming the
///         then <c>GearRail_Cat_Time</c> row of <see cref="Allowed" /> failed BOTH
///         cases (the de-DE theory reported it as an offender, the stale-row
///         Fact reported the row), and copying <c>PhraseCatalog_RenameFailed</c>'s
///         English value into de-DE failed the de-DE theory alone, naming it.
///     </para>
/// </summary>
public sealed class UntranslatedLongValueTests
{
    /// <summary>
    ///     Above this many characters an identical value is a whole label or
    ///     sentence rather than a shared word, and needs a stated reason. Below
    ///     it, <c>UntranslatedValues_DoNotGrow</c>'s ratchet is the guard.
    /// </summary>
    private const int MinimumLength = 12;

    /// <summary>
    ///     English values that are deliberately identical in a translated table,
    ///     with the reason. Exhaustive on purpose: a new entry is a decision
    ///     somebody made, not a number somebody raised.
    /// </summary>
    private static readonly (string File, string Key, string Reason)[] Allowed =
    [
        ("Resources.de-DE.resx", "RigLab_Title", "The product name — the standalone rig's window title."),
        ("Resources.de-DE.resx", "ChartSynth_Model_PrecisionBass", "A Fender model name (#50); German players use the brand name as is."),
        ("Resources.de-DE.resx", "ChartSynth_Model_StratHss", "A Fender model name and its pickup code (#50), used as is in German."),
        ("Resources.ja-JP.resx", "RigLab_Title", "The product name — the standalone rig's window title."),
        ("Resources.de-DE.resx", "About_RenderingBody",
            "Product names only — Silk.NET / SkiaSharp / AlphaTab / AlphaSkia."),
        ("Resources.de-DE.resx", "About_Runtime",
            "Product names only — '.NET 10 · Avalonia'."),
        ("Resources.de-DE.resx", "ChartDiff_PositionArrangement",
            "'Arrangement' IS the German word; the de table uses it ~105 times."),
        ("Resources.de-DE.resx", "Diagnostics_FrameLineFormat",
            "Placeholders and units — 'fps' and 'ms' are the German units too."),
        ("Resources.de-DE.resx", "Inspector_EqBandTitle",
            "'Band' is the German EQ term; the rest is placeholders."),
        ("Resources.de-DE.resx", "Panel_Bend_BendRelease",
            "The Panel_Bend_* family keeps the English bend technique names, as the "
            + "shorter siblings Panel_Bend_Bend / _Release / _Prebend already do."),
        ("Resources.de-DE.resx", "Panel_Bend_PrebendBend",
            "Same family rule as Panel_Bend_BendRelease."),
        ("Resources.de-DE.resx", "Panel_Bend_PrebendRelease",
            "Same family rule as Panel_Bend_BendRelease."),

        ("Resources.ja-JP.resx", "About_RenderingBody",
            "Product names only — Silk.NET / SkiaSharp / AlphaTab / AlphaSkia."),
        ("Resources.ja-JP.resx", "About_Runtime",
            "Product names only — '.NET 10 · Avalonia'."),
        ("Resources.ja-JP.resx", "Diagnostics_FrameLineFormat",
            "Placeholders and units; 'fps' and 'ms' are written in Latin in Japanese too.")
    ];

    /// <summary>Only values carrying a real word can be "untranslated" at all.</summary>
    private static readonly Regex HasWord = new("[A-Za-z]{3,}", RegexOptions.Compiled);

    [Theory]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void LongEnglishValues_AreNotCopiedIntoATranslation(string fileName)
    {
        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        var english = ReadResxValues(Path.Combine(locDir, "Resources.resx"));
        var translated = ReadResxValues(Path.Combine(locDir, fileName));

        Assert.True(english.Count > 3000, $"only {english.Count} English keys parsed — the reader is broken");
        Assert.True(translated.Count > 3000, $"only {translated.Count} keys parsed from {fileName}");

        var allowed = Allowed
            .Where(row => row.File == fileName)
            .Select(row => row.Key)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = LongIdenticalValues(english, translated)
            .Where(key => !allowed.Contains(key))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{fileName} leaves these long values on their English string. Nothing throws and nothing "
            + "is logged — the string simply renders in English for that language's users. Translate "
            + $"them, or add a row to {nameof(Allowed)} saying why the value is identical on purpose:"
            + Environment.NewLine
            + string.Join(Environment.NewLine,
                offenders.Select(key => $"  {key} = {english[key]}")));
    }

    /// <summary>
    ///     An allow-list entry that no longer describes a real identical value is
    ///     a licence nobody is using, and it will quietly cover the NEXT string
    ///     that lands on that key. Assert every row still applies.
    /// </summary>
    [Fact]
    public void EveryAllowListRow_StillDescribesAnIdenticalValue()
    {
        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        var english = ReadResxValues(Path.Combine(locDir, "Resources.resx"));

        var stale = new List<string>();
        foreach (var group in Allowed.GroupBy(row => row.File, StringComparer.Ordinal))
        {
            var translated = ReadResxValues(Path.Combine(locDir, group.Key));
            var identical = LongIdenticalValues(english, translated).ToHashSet(StringComparer.Ordinal);
            foreach (var row in group.Where(row => !identical.Contains(row.Key)))
                stale.Add($"  {row.File} / {row.Key}");
        }

        Assert.True(stale.Count == 0,
            "These allow-list rows no longer name a long value identical to English — the string was "
            + "translated, renamed or deleted. Remove the row rather than leaving the exemption open:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));

        Assert.NotEmpty(Allowed);
    }

    private static IEnumerable<string> LongIdenticalValues(
        Dictionary<string, string> english,
        Dictionary<string, string> translated)
        => english
            .Where(pair => translated.TryGetValue(pair.Key, out var value)
                           && value.Trim() == pair.Value.Trim()
                           && pair.Value.Trim().Length > MinimumLength
                           && HasWord.IsMatch(pair.Value))
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal);

    private static Dictionary<string, string> ReadResxValues(string path)
    {
        Assert.True(File.Exists(path), $"resx not found at {path}");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            if (name != null) map[name] = data.Element("value")?.Value ?? "";
        }

        return map;
    }
}
