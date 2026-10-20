using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The dialog WIDTH LADDER (2026-08-11): every <c>Border.DialogCard</c>
///     wears exactly one of <c>Sm/Md/Lg/Xl</c> and NO ad-hoc size. The
///     catalogue had grown seventeen different card widths (420…760, 1120,
///     1160), which is seventeen answers to a four-answer question — and two of
///     them (1120, 1160) were wider than a laptop screen, which is what the
///     shared clamp in <c>ShellAndHead.axaml</c> exists to absorb.
/// </summary>
public sealed class DialogWidthLadderTests
{
    private static readonly string[] Steps = ["Sm", "Md", "Lg", "Xl"];

    [Fact]
    public void EveryDialogCard_WearsExactlyOneLadderStep_AndNoAdHocSize()
    {
        var dialogDirs = new[]
        {
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs")),
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Diagnostics"))
        };

        var offenders = new List<string>();
        var cards = 0;

        foreach (var file in dialogDirs.Where(Directory.Exists)
                     .SelectMany(d => UiSourceTree.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories)))
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file),
                     "<Border [^>]*Classes=\"DialogCard[^\"]*\"[^>]*>", RegexOptions.Singleline))
            {
                cards++;
                var tag = m.Value;
                var classes = Regex.Match(tag, "Classes=\"([^\"]*)\"").Groups[1].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var steps = classes.Count(c => Steps.Contains(c, StringComparer.Ordinal));
                if (steps != 1)
                {
                    offenders.Add($"{Path.GetFileName(file)}: a DialogCard with {steps} ladder steps " +
                              $"(Classes=\"{string.Join(' ', classes)}\") — exactly one of Sm/Md/Lg/Xl");
                }

                if (Regex.IsMatch(tag, @"\b(?:Width|MinWidth|MaxWidth|MaxHeight)=""[0-9]"))
                {
                    offenders.Add($"{Path.GetFileName(file)}: a DialogCard carries an ad-hoc size — " +
                              "the ladder step is the one width decision a dialog makes, and the " +
                              "shared clamp owns Max*");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DialogCard(s) off the width ladder (docs/agents/ui/dialogs.md):\n  " + string.Join("\n  ", offenders));
        Assert.True(cards >= 34, $"only {cards} DialogCards were found — the scan is broken");
    }

    /// <summary>The four tokens exist on the Fire ladder, ascend, and the
    ///     grammar's step classes reach them — a renamed token leaves a card
    ///     width-less with no warning.</summary>
    [Fact]
    public void TheLadderTokens_ExistAscending_AndTheGrammarReachesThem()
    {
        var ladders = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens", "Ladders.axaml"));

        var values = Steps.Select(s =>
        {
            var m = Regex.Match(ladders, $"x:Key=\"FireDialogWidth{s}\">([0-9.]+)<");
            Assert.True(m.Success, $"FireDialogWidth{s} is not declared in Ladders.axaml");
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }).ToList();
        Assert.True(values.SequenceEqual(values.OrderBy(v => v)) && values.Distinct().Count() == 4,
            $"the width ladder does not ascend: {string.Join(", ", values)}");

        var grammar = StyleFileText.ReadWithIncludes(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "DialogStyles.axaml")));
        foreach (var s in Steps)
        {
            // the steps are rules of the card's ControlTheme, DialogCardBorder (#428)
            Assert.Contains($"^.DialogCard.{s}", grammar, StringComparison.Ordinal);
            Assert.Contains($"FireDialogWidth{s}", grammar, StringComparison.Ordinal);
        }
    }
}
