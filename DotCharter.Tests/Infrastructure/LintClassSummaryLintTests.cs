using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every <c>*LintTests</c> class carries a class-level XML <c>&lt;summary&gt;</c>
///     that says, in at least <see cref="MinWords" /> words, what rule it enforces.
///     A lint is a rule written as a test; the first thing a reader of its failure
///     opens is the class, and a class without a summary leaves them reverse-engineering
///     the rule from a regex. The class list comes from REFLECTION over this assembly and
///     the summaries from a Roslyn parse of the sources, so a lint cannot hide from the
///     check by its file name, and a partial class needs the summary on one part only.
///     See <c>DotCharter.Tests/README.md</c> (adding a lint).
/// </summary>
public sealed partial class LintClassSummaryLintTests
{
    /// <summary>"Checks the thing." is not a rule; a sentence that names the rule and why is about this long.</summary>
    private const int MinWords = 8;

    [Fact]
    public void Every_lint_class_says_what_rule_it_enforces()
    {
        var lintClasses = typeof(LintClassSummaryLintTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsNested: false } && t.Name.EndsWith("LintTests", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(lintClasses.Count >= 40,
            $"reflection found only {lintClasses.Count} *LintTests classes — the type scan is broken, so this lint would pass vacuously.");

        var summaries = SummariesByClass(TestPaths.RepoRoot());
        var unscanned = lintClasses.Where(c => !summaries.ContainsKey(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unscanned.Count == 0, LintReport.Of(
            "These *LintTests classes are compiled but no source file under DotCharter.Tests declares them — the source scan is broken:",
            unscanned,
            "fix SummariesByClass (it walks DotCharter.Tests/**/*.cs, skipping bin/ and obj/)."));

        var offenders = summaries
            .Where(s => lintClasses.Contains(s.Key) && WordCount(s.Value.Summary) < MinWords)
            .OrderBy(s => s.Value.Where, StringComparer.Ordinal)
            .Select(s => $"{s.Value.Where}: class {s.Key} — " +
                         (s.Value.Summary.Length == 0 ? "no <summary>" : $"summary has {WordCount(s.Value.Summary)} word(s): \"{s.Value.Summary}\""))
            .ToList();
        Assert.True(offenders.Count == 0, LintReport.Of(
            $"Every *LintTests class needs a class-level /// <summary> of at least {MinWords} words saying what rule it enforces and why:",
            offenders,
            "add the summary above the class: the rule, what breaks without it, and the guide that explains it.",
            "DotCharter.Tests/README.md (Adding a lint)"));
    }

    [Fact]
    public void A_summary_is_read_from_any_part_of_a_partial_class_and_a_short_one_does_not_count()
    {
        const string source = """
            namespace N;

            [Collection("X")]
            public sealed partial class ALintTests { }

            /// <summary>Every widget is round, because square widgets clip their neighbours on resize.</summary>
            public sealed partial class ALintTests { }

            /// <summary>Checks things.</summary>
            public sealed class BLintTests { }

            public sealed class CLintTests { }
            """;

        var found = Summaries("probe.cs", source);

        Assert.True(WordCount(found["ALintTests"].Summary) >= MinWords);
        Assert.True(WordCount(found["BLintTests"].Summary) < MinWords);
        Assert.Equal(string.Empty, found["CLintTests"].Summary);
        Assert.Equal("probe.cs:12", found["CLintTests"].Where);
    }

    private static Dictionary<string, (string Summary, string Where)> SummariesByClass(string root)
    {
        var all = new Dictionary<string, (string Summary, string Where)>(StringComparer.Ordinal);
        var tests = Path.Combine(root, "DotCharter.Tests");
        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            // A class named ...LintTests needs that word in its own file, so a file without
            // it is not parsed.
            var source = File.ReadAllText(file);
            if (!source.Contains("LintTests", StringComparison.Ordinal)) continue;
            foreach (var (name, found) in Summaries(relative, source))
            {
                if (!all.TryGetValue(name, out var existing) || WordCount(found.Summary) > WordCount(existing.Summary))
                    all[name] = found;
            }
        }

        return all;
    }

    private static Dictionary<string, (string Summary, string Where)> Summaries(string relative, string source)
    {
        var result = new Dictionary<string, (string Summary, string Where)>(StringComparer.Ordinal);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                     .Where(c => c.Identifier.Text.EndsWith("LintTests", StringComparison.Ordinal) && c.Parent is not TypeDeclarationSyntax))
        {
            var summary = string.Join(' ', type.GetLeadingTrivia()
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .SelectMany(d => d.Content.OfType<XmlElementSyntax>())
                .Where(e => e.StartTag.Name.LocalName.Text == "summary")
                .Select(e => Whitespace().Replace(DocDecoration().Replace(e.Content.ToFullString(), " "), " ").Trim()));
            var where = $"{relative}:{type.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            var name = type.Identifier.Text;
            if (!result.TryGetValue(name, out var existing) || WordCount(summary) > WordCount(existing.Summary))
                result[name] = (summary, where);
        }

        return result;
    }

    private static int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // The `///` exterior of each doc line and any XML tag (<c>, <see cref=…/>, <para>).
    [GeneratedRegex(@"///|<[^>]*>")]
    private static partial Regex DocDecoration();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
