using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Product code must not read <c>Levels[0]</c>, <c>Levels.First()</c> or <c>Levels.FirstOrDefault()</c>
///     as "the chart": level 0 is the EASIEST difficulty of a levelled arrangement, and the chart
///     is <c>ChartLevels.Hardest</c> (#80). A predicate form (<c>Levels.FirstOrDefault(l =&gt; ...)</c>)
///     selects a level by what it is and is fine; only the bare positional read is the bug.
///     <para>
///         The allow-list is deliberately tiny and every entry carries the reason it is KEPT.
///     </para>
/// </summary>
public sealed class FirstLevelReadLintTests
{
    /// <summary>The bare positional read, as a member access (so <c>EffortLevels[0]</c> and a
    /// bare <c>Levels[0]</c> of some other type do not match).</summary>
    private static readonly Regex FirstLevelRead = new(
        @"\.Levels(?:\[0\]|\.First(?:OrDefault)?\(\s*\))", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["DotCharter.Services/Export/DynamicDifficulty/DdImprover.cs"] =
            "Levels is the RS-XML DdArrangement's own list, not ChartLevel; ProcessCustomEvents documents " +
            "that it supports only arrangements with no DD levels (one level), and FirstNoteTime guards its read with Count == 1."
    };

    [Fact]
    public void NoProductCode_ReadsTheFirstLevelAsTheChart()
    {
        var root = TestPaths.RepoRoot();
        var files = ProductSources(root).ToList();
        Assert.True(files.Count > 500, $"only {files.Count} product sources were scanned — the glob is broken, so this lint would pass vacuously.");

        var offenders = new List<string>();
        var allowedHits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = SourceText.StripCommentsKeepingLines(File.ReadAllText(file));
            foreach (Match match in FirstLevelRead.Matches(text))
            {
                if (Allowed.ContainsKey(relative)) allowedHits.Add(relative);
                else offenders.Add($"{relative}:{SourceText.LineAt(text, match.Index)}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, LintReport.Of(
            "Product code reads the EASIEST level of an arrangement as if it were the chart:",
            offenders,
            "use ChartLevels.Hardest(arrangement) (DotCharter.Core/Utilities/ChartLevels.cs), or select by DifficultyIndex."));
        Assert.True(Allowed.Keys.All(allowedHits.Contains),
            "an allow-list entry no longer matches anything — delete it: " +
            string.Join(", ", Allowed.Keys.Where(k => !allowedHits.Contains(k))));
    }

    [Theory]
    [InlineData("var l = arrangement.Levels[0];", true)]
    [InlineData("var l = arrangement.Levels.FirstOrDefault();", true)]
    [InlineData("var l = arrangement.Levels.First();", true)]
    [InlineData("var l = arrangement.Levels.FirstOrDefault(x => x.DifficultyIndex == 2);", false)]
    [InlineData("_selected = EffortLevels[0];", false)]
    [InlineData("var l = ChartLevels.Hardest(arrangement);", false)]
    public void TheMatcherSeparatesTheBareReadFromASelection(string code, bool matches) =>
        Assert.Equal(matches, FirstLevelRead.IsMatch(code));

    private static IEnumerable<string> ProductSources(string root) =>
        Directory.EnumerateDirectories(root, "DotCharter.*")
            .Where(d => !Path.GetFileName(d).StartsWith("DotCharter.Tests", StringComparison.Ordinal))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
