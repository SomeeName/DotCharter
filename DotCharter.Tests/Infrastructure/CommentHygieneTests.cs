using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Comment hygiene (#835): a comment states the current fact and the reason, not the
///     story of how the code got there. Issue numbers and "used to" narration go stale and
///     belong in the commit message, so each product project has a CEILING on the comment
///     lines that carry one, and the ceiling may only go down: a new comment that cites an
///     issue or narrates history fails this, and lowering the number is how a cleanup
///     is banked. The Presentation projects hold the stricter zero rule in
///     <c>PresentationSourceRulesTests</c>.
/// </summary>
public sealed class CommentHygieneTests
{
    /// <summary>Comment lines naming an issue (<c>#123</c>) per project. Lower it, never raise it.</summary>
    private static readonly Dictionary<string, int> IssueRefCeilings = new(StringComparer.Ordinal)
    {
        ["DotCharter.Core"] = 0,
        ["DotCharter.Launcher"] = 0,
        ["DotCharter.Analysis"] = 0,
        ["DotCharter.Rendering"] = 0,
        ["DotCharter.Localization"] = 0,
        ["DotCharter.Tone"] = 0,
        ["DotCharter.Services"] = 0,
        ["DotCharter.Presentation"] = 0,
        ["DotCharter.Presentation.Fakes"] = 0,
        ["DotCharter.Tone.UI"] = 0,
        ["DotCharter.UI"] = 0,
        ["DotCharter.RigLab"] = 0,
        ["DotCharter.UI.Gallery"] = 0
    };

    /// <summary>Comment lines that narrate history ("used to", "no longer", a date, "since 0.5x") per project.</summary>
    private static readonly Dictionary<string, int> HistoryCeilings = new(StringComparer.Ordinal)
    {
        ["DotCharter.Core"] = 0,
        ["DotCharter.Launcher"] = 0,
        ["DotCharter.Analysis"] = 0,
        ["DotCharter.Rendering"] = 0,
        ["DotCharter.Localization"] = 0,
        ["DotCharter.Tone"] = 0,
        ["DotCharter.Services"] = 0,
        ["DotCharter.Presentation"] = 0,
        ["DotCharter.Presentation.Fakes"] = 0,
        ["DotCharter.Tone.UI"] = 0,
        ["DotCharter.UI"] = 0,
        ["DotCharter.RigLab"] = 0,
        ["DotCharter.UI.Gallery"] = 0
    };

    private static readonly Regex IssueRefPattern = new(
        @"(?<![A-Za-z0-9&])#\d{2,4}(?![0-9A-Za-z])", RegexOptions.CultureInvariant);

    private static readonly Regex HistoryPattern = new(
        @"\bused to\b|\bno longer\b|\bformerly\b|\bpreviously\b|\b(?:since|until|before|from|in) v?0\.\d\d\b|\b20\d\d-\d\d(?:-\d\d)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Comments_cite_no_more_issue_numbers_than_the_ceiling()
    {
        var offenders = Measure(IssueRefPattern, IssueRefCeilings);
        Assert.True(offenders.Count == 0,
            "comment lines naming an issue (#NNN) are over the ceiling — say the reason, not the number " +
            "(the commit message holds the history; lower the ceiling when you remove some):\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void Comments_narrate_no_more_history_than_the_ceiling()
    {
        var offenders = Measure(HistoryPattern, HistoryCeilings);
        Assert.True(offenders.Count == 0,
            "comment lines that narrate history (used to / no longer / a date / since 0.NN) are over the ceiling — " +
            "state the current fact; git holds the history (lower the ceiling when you remove some):\n  " +
            string.Join("\n  ", offenders));
    }

    // ── probes: each rule bites on a known-bad input ─────────────────────

    [Fact]
    public void Probe_the_history_pattern_finds_narration_in_comments_only()
    {
        const string source = """
            namespace P;
            // This used to live in the shell.
            /// <summary>No longer shown since 0.52.</summary>
            public sealed class C
            {
                public string S => "used to be 2026-10-01";
                // Holds the state the door opens in.
                // Wait for the scan, then continue.
            }
            """;
        Assert.Equal(2, CountCommentLines(source, HistoryPattern));
    }

    [Fact]
    public void Probe_the_issue_pattern_finds_numbers_in_comments_only()
    {
        const string source = """
            namespace P;
            // The retry (#506) is held back.
            public sealed class C
            {
                public string S => "#506";
                // Colour #FF0000 and C# 12 are not issues, nor is the entity &#123;.
                public int N => 1; // #77
            }
            """;
        Assert.Equal(2, CountCommentLines(source, IssueRefPattern));
    }

    [Fact]
    public void Probe_the_ceilings_cover_every_product_project()
    {
        var root = TestPaths.RepoRoot();
        var projects = Directory.EnumerateDirectories(root, "DotCharter.*")
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => File.Exists(Path.Combine(root, n, n + ".csproj")))
            .Where(n => n != "DotCharter.Tests")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(IssueRefCeilings.Keys.Order(StringComparer.Ordinal).ToList(), projects);
        Assert.Equal(HistoryCeilings.Keys.Order(StringComparer.Ordinal).ToList(), projects);
    }

    // ── the rules ─────────────────────────────────────────────────────────

    private static List<string> Measure(Regex pattern, Dictionary<string, int> ceilings)
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var (project, ceiling) in ceilings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dir = Path.Combine(root, project);
            var files = UiSourceTree.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(Path.GetRelativePath(dir, f)))
                .ToList();
            Assert.True(files.Count > 0, $"no sources found for {project}");
            var count = files.Sum(f => CountCommentLines(UiSourceTree.ReadAllText(f), pattern));
            if (count > ceiling) offenders.Add($"{project}: {count} comment lines, ceiling {ceiling}");
            else if (count < ceiling) offenders.Add($"{project}: {count} comment lines, ceiling {ceiling} - lower the ceiling to {count} to bank the cleanup");
        }

        return offenders;
    }

    private static bool IsBuildOutput(string relative)
    {
        var rel = relative.Replace('\\', '/');
        return rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal);
    }

    internal static int CountCommentLines(string source, Regex pattern) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTrivia(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .SelectMany(t => t.ToFullString().Split('\n'))
            .Count(l => pattern.IsMatch(l));
}
