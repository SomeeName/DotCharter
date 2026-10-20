using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     No product file keeps a private copy of the standard open-string table.
///     <para>
///         Five copies of <c>[0, 5, 10, 15, 19, 24, 29]</c> read a 7-string guitar's
///         extra string as a HIGH A4 while the tuning names, the tab importers and
///         exporters and the RS7 layout read it as a LOW B1 (#742): the gutter named a
///         7-string's lanes E A D G B E A and live judging graded every string a fourth
///         high. <c>Core/Utilities/StandardOpenStrings</c> is the one table now; this
///         fails on a new copy of it — the relative row (<c>0, 5, 10, 15, 19</c> or the
///         low-extension <c>-5, 0, 5, 10</c>) or an absolute 7/8-string row
///         (<c>35, 40, 45, 50</c> / <c>30, 35, 40, 45</c>). Comments are stripped, so a
///         doc line may still spell a row out.
///     </para>
///     <para>
///         The absolute SIX-string row <c>40, 45, 50, 55, 59, 64</c> is not linted: it
///         has one meaning, several analysis and SNG paths spell it, and none of them
///         can disagree about a 7th string.
///     </para>
/// </summary>
public sealed class OpenStringTableLintTests
{
    private const string TheTable = "DotCharter.Core/Utilities/StandardOpenStrings.cs";

    private static readonly Regex[] Copies =
    [
        new(@"(?<![\d.-])0\s*,\s*5\s*,\s*10\s*,\s*15\s*,\s*19\b", RegexOptions.Compiled),
        new(@"-\s*5\s*,\s*0\s*,\s*5\s*,\s*10\b", RegexOptions.Compiled),
        new(@"(?<![\d.])35\s*,\s*40\s*,\s*45\s*,\s*50\b", RegexOptions.Compiled),
        new(@"(?<![\d.])30\s*,\s*35\s*,\s*40\s*,\s*45\b", RegexOptions.Compiled)
    ];

    [Theory]
    [InlineData("private static readonly int[] StandardOpenSemitones = [0, 5, 10, 15, 19, 24, 29];")]
    [InlineData("int[] seven = { -5, 0, 5, 10, 15, 19, 24 };")]
    [InlineData("7 => [35, 40, 45, 50, 55, 59, 64],")]
    [InlineData(">= 8 => [30,35,40,45,50,55,59,64],")]
    public void ThePatterns_CatchTheShapesTheCopiesHad(string copy) =>
        Assert.Contains(Copies, pattern => pattern.IsMatch(copy));

    [Theory]
    [InlineData("var heights = [0, 5, 10, 0];")]
    [InlineData("int[] ticks = [10, 5, 10, 15, 19];")]
    [InlineData("[40, 45, 50, 55, 59, 64, 35, 30]")]
    public void ThePatterns_LeaveOtherNumbersAlone(string other) =>
        Assert.DoesNotContain(Copies, pattern => pattern.IsMatch(other));

    [Fact]
    public void NoProductFile_KeepsAPrivateOpenStringTable()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative == TheTable) continue;
                scanned++;

                var text = SourceText.StripCommentsKeepingLines(File.ReadAllText(file));
                foreach (var pattern in Copies)
                {
                    foreach (Match match in pattern.Matches(text))
                        offenders.Add($"{relative}:{SourceText.LineAt(text, match.Index)}: '{match.Value}'");
                }
            }
        }

        Assert.True(scanned > 1000, $"only scanned {scanned} product files — the glob is broken");
        Assert.True(Copies.Any(p => p.IsMatch(File.ReadAllText(Path.Combine(root, TheTable)))),
            $"{TheTable} no longer holds the rows these patterns describe — the lint is blind.");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A product file keeps its own copy of the standard open-string table (#742): a 7-string's extra string " +
            "reads as a high A4 there and a low B1 everywhere else.",
            offenders,
            $"read the row from StandardOpenStrings ({TheTable}) instead of spelling it out.",
            "docs/agents/core/domain-model.md (StandardOpenStrings)"));
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
