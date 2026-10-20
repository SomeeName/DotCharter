using System.Net;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Localization;

/// <summary>
///     Graphite's copy rule is sentence case (docs/agents/ui/fire-design-system.md,
///     "Graphite rules"). Two sweeps on 2026-09-22 took 362 all-caps and 280
///     Title Case strings out of the tables; these lints keep the capitals from
///     coming back by the two doors they used: an all-caps value in the table,
///     and a view that upper-cases a sentence-case value on the way to the screen.
/// </summary>
public sealed class CopyCaseLintTests
{
    /// <summary>Values that ARE acronyms, and so are all capitals by right.
    /// Shrink-only in spirit: a new entry is a word nobody should shout.</summary>
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "BPM", "HOPO", "GUI", "CDLC", "FHP", "GPU", "CPU"
    };

    [Fact]
    public void NoEnglishValue_IsAllCapitals_UnlessItIsAnAcronym()
    {
        var resx = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        var offenders = new List<string>();
        foreach (Match m in Regex.Matches(resx,
                     @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>",
                     RegexOptions.Singleline))
        {
            var value = WebUtility.HtmlDecode(m.Groups["v"].Value);
            var letters = value.Where(char.IsLetter).ToList();
            if (letters.Count < 3 || !letters.All(char.IsUpper)) continue;
            if (Acronyms.Contains(value.Trim())) continue;
            offenders.Add($"{m.Groups["k"].Value} = \"{value}\"");
        }

        Assert.True(offenders.Count == 0,
            "an English value is set in capitals — write it in sentence case (an acronym goes in " +
            "Acronyms):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoView_UpperCasesItsText()
    {
        var offenders = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            var text = UiSourceTree.ReadAllText(file);
            if (Regex.IsMatch(text, @"Upper\s*=\s*True") || text.Contains("TextConverters.ToUpper", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "a view upper-cases text on the way to the screen, which undoes the sentence-case copy: "
            + string.Join(", ", offenders));
    }
}
