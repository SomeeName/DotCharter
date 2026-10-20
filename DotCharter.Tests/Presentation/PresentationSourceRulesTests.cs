using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The source-shaped rules of docs/agents/ui/target-architecture.md § 4,
///     over the two Presentation projects: file and type size, no narrative
///     history in comments, a comment budget, and no <c>#pragma</c> around the
///     banned-API analyzer. Syntax trees rather than regexes, so a type split
///     over partial files is one type and a string that looks like a comment is
///     not one.
///     <para>
///         Each rule is a pure function over source text with its own probe
///         fact, so the rule is seen to bite on a known-bad input in every run,
///         not only on the day it was written.
///     </para>
/// </summary>
public sealed class PresentationSourceRulesTests
{
    internal const int MaxFileLines = 400;
    internal const int MaxTypeLines = 600;
    internal const int MaxFilesPerType = 2;
    internal const double MaxCommentShare = 0.20;

    /// <summary>The Presentation source set. A floor, so a moved folder cannot pass vacuously.</summary>
    [Fact]
    public void The_scanned_source_set_is_not_empty()
    {
        var files = PresentationSourceFiles();
        Assert.True(files.Count >= 20, $"expected the Presentation projects' sources, found {files.Count} files");
        Assert.Contains(files, f => f.Contains("Contracts", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Contains("DotCharter.Presentation.Fakes", StringComparison.Ordinal));
    }

    [Fact]
    public void No_file_is_longer_than_the_limit()
    {
        var offenders = PresentationSourceFiles()
            .Select(f => (File: Relative(f), Lines: CountLines(UiSourceTree.ReadAllText(f))))
            .Where(x => x.Lines > MaxFileLines)
            .Select(x => $"{x.File}: {x.Lines} lines")
            .ToList();
        Assert.True(offenders.Count == 0,
            $"C# files over {MaxFileLines} lines (target-architecture.md § 4) — split by responsibility:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_type_is_longer_than_the_limit_or_spread_over_too_many_files()
    {
        var offenders = TypeSizeViolations(PresentationSourceFiles().Select(f => (Relative(f), UiSourceTree.ReadAllText(f))));
        Assert.True(offenders.Count == 0,
            $"types over {MaxTypeLines} lines or {MaxFilesPerType} files (target-architecture.md § 4):\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_comment_narrates_history()
    {
        var offenders = PresentationSourceFiles()
            .SelectMany(f => HistoryCommentLines(UiSourceTree.ReadAllText(f)).Select(l => $"{Relative(f)}: {l}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "comments that narrate history (a date, a version, until / used to / no longer / shipped). " +
            "History lives in git and docs/agents/**:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     <c>Contracts/</c> is exempt: a contract is an interface whose member
    ///     summaries ARE its specification ("already formatted", "hidden, not
    ///     zeroed"), so the ten that exist run at 40–58 % comment by nature.
    ///     The history rule above still applies to them.
    /// </summary>
    [Fact]
    public void No_file_is_mostly_comments()
    {
        var offenders = PresentationSourceFiles()
            .Where(f => !IsContract(Relative(f)))
            .Select(f => (File: Relative(f), Share: CommentShare(UiSourceTree.ReadAllText(f))))
            .Where(x => x.Share > MaxCommentShare)
            .Select(x => $"{x.File}: {x.Share:P0}")
            .ToList();
        Assert.True(offenders.Count == 0,
            $"files whose non-blank lines are more than {MaxCommentShare:P0} comment (target-architecture.md § 4):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The banned-API analyzer's only exemptions are the .editorconfig
    ///     sections for the composition root and <c>Platform/</c>. A
    ///     <c>#pragma</c> or a <c>SuppressMessage</c> anywhere else is a hole the
    ///     next reader cannot see from the exemption list, so the count must be
    ///     zero — over the UI too, ready for the day the analyzer is on there.
    /// </summary>
    [Fact]
    public void No_source_suppresses_the_banned_api_analyzer_outside_the_exempt_locations()
    {
        var files = PresentationSourceFiles().Concat(UiSourceFiles()).ToList();
        Assert.True(files.Count >= 500, $"expected the UI and Presentation sources, found {files.Count} files");

        var offenders = files
            .Where(f => !IsBannedApiExempt(Relative(f)))
            .Where(f => SuppressesBannedApis(UiSourceTree.ReadAllText(f)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0,
            "RS0030 suppressed in source — move the call behind a Services interface or a Platform/ adapter:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The exemption list is the composition root and the two <c>Platform/</c>
    ///     folders — nothing else. A UI file that needs the disk takes a Services
    ///     seam; it never gets a section of its own.
    /// </summary>
    [Fact]
    public void Only_the_sanctioned_editorconfig_sections_turn_the_banned_api_analyzer_off()
    {
        var sections = EditorConfigSectionsTouching("RS0030", UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), ".editorconfig")));
        string[] expected =
        [
            "[DotCharter.UI/Platform/**.cs]", "[DotCharter.UI/{App*.cs,Program.cs}]",
            "[DotCharter.Tone.UI/Platform/**.cs]"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(),
            sections.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The UI project really has the analyzer switched on.</summary>
    [Fact]
    public void The_ui_project_opts_into_the_banned_api_analyzer()
    {
        var csproj = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "DotCharter.UI.csproj")));
        Assert.Contains("<DotCharterBannedApis>true</DotCharterBannedApis>", csproj, StringComparison.Ordinal);
    }

    // ── probes: each rule bites on a known-bad input ─────────────────────

    [Fact]
    public void Probe_the_type_rule_counts_partials_as_one_type()
    {
        var body = string.Concat(Enumerable.Repeat("    public int F() => 1;\n", 350));
        var a = $"namespace P;\npublic partial class Big\n{{\n{body}}}\n";
        var b = $"namespace P;\npublic partial class Big\n{{\n{body}}}\n";
        var c = "namespace P;\npublic partial class Big { }\n";

        var tooLong = TypeSizeViolations([("a.cs", a), ("b.cs", b)]);
        Assert.Single(tooLong, v => v.StartsWith("P.Big: ", StringComparison.Ordinal) && v.Contains("lines", StringComparison.Ordinal));

        var tooSpread = TypeSizeViolations([("a.cs", c), ("b.cs", c), ("c.cs", c)]);
        Assert.Single(tooSpread, v => v.Contains("3 files", StringComparison.Ordinal));

        Assert.Empty(TypeSizeViolations([("a.cs", c), ("b.cs", c)]));
    }

    [Fact]
    public void Probe_the_history_rule_reads_comments_and_only_comments()
    {
        const string source = """
            namespace P;
            // Added 2026-09-19 for the redesign.
            /// <summary>Since 0.44 this is split.</summary>
            public sealed class C
            {
                // This used to live in the shell.
                /* It no longer does. */
                public string S => "shipped 2026-09-19 until 0.52";   // plain words here
                // Wait until the scan finishes.
                // Holds the state the door opens in.
            }
            """;
        var hits = HistoryCommentLines(source);
        Assert.Equal(5, hits.Count);
        Assert.DoesNotContain(hits, h => h.Contains("plain words", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.Contains("the door opens", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_the_comment_share_counts_doc_and_line_comments_against_non_blank_lines()
    {
        const string source = """
            namespace P;

            /// <summary>One.</summary>
            public sealed class C
            {
                // two
                public int A => 1;
            }
            """;
        // 7 non-blank lines, 2 of them comment.
        Assert.Equal(2.0 / 7.0, CommentShare(source), 6);
    }

    [Fact]
    public void Probe_the_suppression_rule_finds_a_pragma_and_an_attribute()
    {
        Assert.True(SuppressesBannedApis("#pragma warning disable RS0030 // file access\nclass C { }"));
        Assert.True(SuppressesBannedApis("#pragma warning disable CS0168, RS0030\nclass C { }"));
        Assert.True(SuppressesBannedApis("[SuppressMessage(\"ApiDesign\", \"RS0030:Do not use banned APIs\")]\nclass C { }"));
        Assert.False(SuppressesBannedApis("#pragma warning disable CS0168\nclass C { }"));
        Assert.False(SuppressesBannedApis("// RS0030 is the banned-API diagnostic\nclass C { }"));

        Assert.True(IsBannedApiExempt("DotCharter.UI/App.AudioServices.cs"));
        Assert.True(IsBannedApiExempt("DotCharter.UI/Program.cs"));
        Assert.True(IsBannedApiExempt("DotCharter.UI/Platform/DesktopFileStorageService.cs"));
        Assert.False(IsBannedApiExempt("DotCharter.UI/Views/AppShellView.axaml.cs"));
        Assert.False(IsBannedApiExempt("DotCharter.Presentation/App.cs"));
    }

    // ── the rules ─────────────────────────────────────────────────────────

    private static readonly Regex HistoryPattern = new(
        @"20\d\d-\d\d|\b0\.\d\d\b|\buntil\b|\bused to\b|\bno longer\b|\bshipped\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SuppressionPattern = new(
        @"#\s*pragma\s+warning\s+disable\b[^\r\n]*\bRS0030\b|SuppressMessage\s*\([^)]*""RS0030",
        RegexOptions.CultureInvariant);

    internal static List<string> TypeSizeViolations(IEnumerable<(string File, string Source)> sources)
    {
        var spans = new Dictionary<string, (int Lines, HashSet<string> Files)>(StringComparer.Ordinal);
        foreach (var (file, source) in sources)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var span = type.GetLocation().GetLineSpan();
                var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
                var name = FullName(type);
                if (!spans.TryGetValue(name, out var entry))
                    entry = (0, new HashSet<string>(StringComparer.Ordinal));
                entry.Files.Add(file);
                spans[name] = (entry.Lines + lines, entry.Files);
            }
        }

        return spans
            .Where(kv => kv.Value.Lines > MaxTypeLines || kv.Value.Files.Count > MaxFilesPerType)
            .Select(kv => $"{kv.Key}: {kv.Value.Lines} lines in {kv.Value.Files.Count} files")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    internal static List<string> HistoryCommentLines(string source) =>
        CommentTrivia(source)
            .SelectMany(t => t.ToFullString().Split('\n'))
            .Select(l => l.Trim())
            .Where(l => HistoryPattern.IsMatch(l))
            .ToList();

    internal static double CommentShare(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var text = tree.GetText();
        var commentLines = new HashSet<int>();
        foreach (var trivia in CommentTrivia(source, tree))
        {
            var span = trivia.GetLocation().GetLineSpan();
            // A doc comment's trivia ends with its newline, i.e. at column 0 of the next line.
            var last = span.EndLinePosition.Character == 0 && span.EndLinePosition.Line > span.StartLinePosition.Line
                ? span.EndLinePosition.Line - 1
                : span.EndLinePosition.Line;
            for (var line = span.StartLinePosition.Line; line <= last; line++)
                if (!string.IsNullOrWhiteSpace(text.Lines[line].ToString())) commentLines.Add(line);
        }

        var nonBlank = text.Lines.Count(l => !string.IsNullOrWhiteSpace(l.ToString()));
        return nonBlank == 0 ? 0 : (double)commentLines.Count / nonBlank;
    }

    internal static bool IsContract(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith("DotCharter.Presentation/Contracts/", StringComparison.Ordinal);

    internal static bool SuppressesBannedApis(string source) =>
        SuppressionPattern.IsMatch(source);

    /// <summary>Mirrors the RS0030 sections of the root .editorconfig.</summary>
    internal static bool IsBannedApiExempt(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        if (p.StartsWith("DotCharter.UI/Platform/", StringComparison.Ordinal)) return true;
        if (!p.StartsWith("DotCharter.UI/", StringComparison.Ordinal)) return false;
        var rest = p["DotCharter.UI/".Length..];
        return !rest.Contains('/') && (rest == "Program.cs" || (rest.StartsWith("App", StringComparison.Ordinal) && rest.EndsWith(".cs", StringComparison.Ordinal)));
    }

    private static List<string> EditorConfigSectionsTouching(string diagnostic, string editorConfig)
    {
        var sections = new List<string>();
        string? current = null;
        foreach (var raw in editorConfig.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) current = line;
            else if (current != null && !line.StartsWith('#') && line.Contains(diagnostic, StringComparison.Ordinal) && !sections.Contains(current))
                sections.Add(current);
        }

        return sections;
    }

    private static IEnumerable<SyntaxTrivia> CommentTrivia(string source, SyntaxTree? tree = null) =>
        (tree ?? CSharpSyntaxTree.ParseText(source)).GetRoot().DescendantTrivia(descendIntoTrivia: false)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static string FullName(BaseTypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        for (SyntaxNode? node = type; node != null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax t:
                    parts.Add(t.Identifier.Text + (t.TypeParameterList is { } tp ? $"`{tp.Parameters.Count}" : ""));
                    break;
                case BaseTypeDeclarationSyntax b:
                    parts.Add(b.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);

    // ── the source set ───────────────────────────────────────────────────

    internal static List<string> PresentationSourceFiles() =>
        new[] { "DotCharter.Presentation", "DotCharter.Presentation.Fakes" }
            .SelectMany(p => SourceFilesUnder(Path.Combine(TestPaths.RepoRoot(), p)))
            .ToList();

    private static IEnumerable<string> UiSourceFiles() =>
        SourceFilesUnder(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"));

    private static IEnumerable<string> SourceFilesUnder(string projectDir) =>
        UiSourceTree.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(projectDir, f).Replace('\\', '/');
                return !rel.StartsWith("bin/", StringComparison.Ordinal) && !rel.StartsWith("obj/", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(TestPaths.RepoRoot(), path).Replace('\\', '/');
}
