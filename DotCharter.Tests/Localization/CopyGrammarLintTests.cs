using System.Net;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Localization;

/// <summary>
///     The copy grammar the tables keep (each fact below says what it guards and why). Menu,
///     keybind and palette labels follow Graphite's copy rule (sentence case) and the menu's own grammar: an
///     ellipsis is one "…", and a label never spells its shortcut — the gesture column already
///     prints it, so "Screenshot highway (F12)" showed F12 twice. Found reading the Tools menu
///     live (2026-09-24), with "Showlights Editor", "Merge Notes into Chord" and five more Title
///     Case labels the 2026-09-22 sweep missed. Tooltips (…Tip) are prose and exempt.
/// </summary>
public sealed class CopyGrammarLintTests
{
    /// <summary>Words that are names, so capitalised mid-label by right.</summary>
    private static readonly HashSet<string> ProperNouns = new(StringComparer.Ordinal)
    {
        "DotCharter", "Rocksmith", "YouTube", "Demucs", "Guitar", "Pro", "Editor", "Fire", "File", "Explorer",
        // The toolbar's panels, named as the toolbar names them.
        "Mix", "Tools", "Tracks"
    };

    [Fact]
    public void MenuLabels_AreSentenceCase_UseOneEllipsis_AndNeverSpellTheirShortcut()
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (Match m in Regex.Matches(resx, @"<data name=""(?<k>(?:Menu|Keybind|Palette)_[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>",
                     RegexOptions.Singleline))
        {
            var key = m.Groups["k"].Value;
            if (key.EndsWith("Tip", StringComparison.Ordinal) || key.EndsWith("_Tooltip", StringComparison.Ordinal)) continue;
            checkedCount++;
            var value = WebUtility.HtmlDecode(m.Groups["v"].Value);

            if (value.EndsWith("...", StringComparison.Ordinal)) offenders.Add($"{key} = \"{value}\": use \"…\"");
            if (Regex.IsMatch(value, @"\((F\d+|Ctrl\+|Alt\+|Shift\+)")) offenders.Add($"{key} = \"{value}\": the gesture column prints the shortcut");

            var words = Regex.Matches(value, @"[A-Za-z][A-Za-z0-9']*").Select(w => w.Value).Skip(1);
            var titled = words.Where(w => char.IsUpper(w[0]) && !w.All(char.IsUpper) && !w.Any(char.IsDigit) && !ProperNouns.Contains(w)).ToList();
            if (titled.Count > 0) offenders.Add($"{key} = \"{value}\": sentence case ({string.Join(", ", titled)})");
        }

        Assert.True(checkedCount > 100, $"only {checkedCount} menu labels found — the pattern no longer matches the table");
        Assert.True(offenders.Count == 0, "menu, keybind or palette labels break the copy rule:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The whole table, in every language: a trailing ellipsis is one "…" (it had 137 of
    /// those and 12 of "..." in English, status lines and placeholders mostly).</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".de-DE")]
    [InlineData(".ja-JP")]
    public void NoValue_EndsInThreeDots(string culture)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"));
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Where(m => WebUtility.HtmlDecode(m.Groups["v"].Value).TrimEnd().EndsWith("...", StringComparison.Ordinal))
            .Select(m => m.Groups["k"].Value)
            .ToList();
        Assert.True(offenders.Count == 0, $"values end in \"...\" instead of \"…\": {string.Join(", ", offenders)}");
    }

    /// <summary>A dash between words is " — " (about 240 strings); " - " crept into 16 English and
    /// 17 German ones, mostly Live Play status lines. A hyphen with spaces round it reads as a
    /// minus or a typo, and a spaced en dash ("not running – configure") is the same slip.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".de-DE")]
    public void ADashBetweenWords_IsAnEmDash(string culture)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"));
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Where(m => Regex.IsMatch(WebUtility.HtmlDecode(m.Groups["v"].Value), @"\p{L} [-–] \p{L}"))
            .Select(m => m.Groups["k"].Value)
            .ToList();
        Assert.True(offenders.Count == 0, $"\" - \" or \" – \" between words instead of \" — \": {string.Join(", ", offenders)}");
    }

    /// <summary>Names the app has retired: the song map was the "minimap", the Library the "song
    /// browser", the Rig the "pedalboard" (owner, 2026-09-22); the Play Mode main menu went on 2026-09-22 (Home is the player's hub); couch co-op on 2026-09-19 and the scoreboard server in 0.60. A string that still says the old word
    /// sends the reader looking for something that is not on screen.</summary>
    [Theory]
    [InlineData("", "minimap|song browser|pedalboard|amp (?:and|&) fx|main menu|co-op|scoreboard")]
    [InlineData(".de-DE", "minimap|song-browser|pedalboard|amp (?:und|&) fx|hauptmenü|koop|couch-koop|scoreboard")]
    public void NoString_UsesARetiredName(string culture, string retired)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"));
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Where(m => Regex.IsMatch(WebUtility.HtmlDecode(m.Groups["v"].Value), @"\b(" + retired + @")\b", RegexOptions.IgnoreCase))
            .Select(m => m.Groups["k"].Value)
            .ToList();
        Assert.True(offenders.Count == 0, $"strings still use a retired name ({retired}): {string.Join(", ", offenders)}");
    }

    /// <summary>A feature is spelled the way its own button and window spell it: "Session health"
    /// (menu, window, toolbar button), "Live play" (the 3D preview's toggle). Prose said "Session
    /// Health" and "Live Play", so the tooltip and the button beside it disagreed.</summary>
    [Theory]
    [InlineData("Session Health")]
    [InlineData("Live Play")]
    public void EnglishStrings_SpellAFeatureAsItsButtonDoes(string capitalised)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Where(m => WebUtility.HtmlDecode(m.Groups["v"].Value).Contains(capitalised, StringComparison.Ordinal))
            .Select(m => m.Groups["k"].Value)
            .ToList();
        Assert.True(offenders.Count == 0, $"\"{capitalised}\" where the UI says it in sentence case: {string.Join(", ", offenders)}");
    }

    /// <summary>A count picks its own form: "1 iteration", "3 iterations". "(s)" hedged it in 45
    /// English strings and "(e)"/"(en)" in as many German ones; a single count goes through
    /// <c>Plural()</c> (see PluralStringTests), several read as "label: count".</summary>
    [Theory]
    [InlineData("", @"\w\((?:s|es)\)")]
    [InlineData(".de-DE", @"\w\((?:e|en|n|s|r|er|es)\)")]
    public void NoString_HedgesAPlural(string culture, string hedge)
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"));
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Where(m => Regex.IsMatch(WebUtility.HtmlDecode(m.Groups["v"].Value), hedge))
            .Select(m => m.Groups["k"].Value)
            .ToList();
        Assert.True(offenders.Count == 0, $"strings hedge a plural ({hedge}): {string.Join(", ", offenders)}");
    }

    /// <summary>English is US English: the table said "license" four times and "licence" twice
    /// (About had one of each), "color" 7 and "colour" 12, "canceled" 4 and "cancelled" 8. It
    /// went with the majority and the words that only ever had one spelling (analyze, dialog).</summary>
    [Fact]
    public void EnglishStrings_UseUsSpelling()
    {
        var resx = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        const string british = @"\b(licence|colour\w*|behaviour|centre|centred|practis\w*|favourite\w*|cancelled|cancelling|neighbour\w*|honour\w*|catalogue\w*|"
                               + @"(?:recogn|organ|optim|minim|maxim|custom|normal|synchron)is(?:e|ed|es|ing)|analys(?:e|ed|ing))\b";
        var offenders = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .Select(m => (Key: m.Groups["k"].Value, Hit: Regex.Match(WebUtility.HtmlDecode(m.Groups["v"].Value), british, RegexOptions.IgnoreCase)))
            .Where(x => x.Hit.Success)
            .Select(x => $"{x.Key} ({x.Hit.Value})")
            .ToList();
        Assert.True(offenders.Count == 0, $"British spellings in English strings: {string.Join(", ", offenders)}");
    }
}
