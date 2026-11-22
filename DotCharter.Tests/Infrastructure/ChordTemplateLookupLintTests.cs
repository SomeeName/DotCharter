using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Chord-template lookup by id lives in ONE place, <c>Arrangement.FindChordTemplate</c>
///     (Core). This rejects a new inline <c>ChordTemplates.FirstOrDefault(t =&gt; t.Id == …)</c>
///     copy, which skips the negative-id guard and drifts from the rest.
/// </summary>
public sealed class ChordTemplateLookupLintTests
{
    private static readonly Regex InlineLookup = new(
        @"ChordTemplates\s*\.\s*(?:FirstOrDefault|SingleOrDefault|First|Single)\s*\(\s*\w+\s*=>\s*\w+\s*\.\s*Id\s*==",
        RegexOptions.Singleline);

    private static readonly string[] SourceRoots =
        ["DotCharter.Core", "DotCharter.Services", "DotCharter.UI", "DotCharter.Presentation", "DotCharter.Tone", "DotCharter.Analysis"];

    [Fact]
    public void NoProductionCode_ReimplementsTheChordTemplateIdLookup()
    {
        var sources = ProductionSources();
        Assert.True(sources.Count > 1000, $"only {sources.Count} production sources were enumerated: the glob is broken.");

        var callers = sources.Count(f => File.ReadAllText(f).Contains("FindChordTemplate(", StringComparison.Ordinal));
        Assert.True(callers >= 10, $"only {callers} files call FindChordTemplate: the lint's subject is gone.");

        var offenders = sources
            .Where(f => InlineLookup.IsMatch(SourceText.StripCommentLines(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(TestPaths.RepoRoot(), f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "use arrangement.FindChordTemplate(id) (DotCharter.Core) instead of an inline ChordTemplates.FirstOrDefault(t => t.Id == …):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("var t = arr.ChordTemplates.FirstOrDefault(t => t.Id == id);")]
    [InlineData("var t = arr.ChordTemplates\n    .FirstOrDefault(x => x.Id == chord.TemplateId);")]
    [InlineData("var t = a.ChordTemplates.SingleOrDefault(t => t.Id == id);")]
    public void TheLint_MatchesTheInlineShapes(string code) => Assert.Matches(InlineLookup, code);

    [Fact]
    public void TheLint_IgnoresOtherPredicates()
        => Assert.DoesNotMatch(InlineLookup, "arr.ChordTemplates.FirstOrDefault(t => t.Arpeggio && t.HasSameShapeAs(s));");

    private static List<string> ProductionSources() =>
        SourceRoots
            .Select(r => Path.Combine(TestPaths.RepoRoot(), r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
            .Where(f => !Regex.IsMatch(f, @"[\/](obj|bin)[\/]"))
            .ToList();
}
