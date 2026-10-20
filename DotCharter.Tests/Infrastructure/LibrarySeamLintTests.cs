using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps the song library's callers on the neutral seam (#886): no screen, view model or
///     service outside the Library names a package source, so the PSARC source can leave the
///     public tree without touching them.
/// </summary>
public sealed partial class LibrarySeamLintTests
{
    /// <summary>Where a source may be named: the library (and the sources themselves) and the
    /// composition root that registers them.</summary>
    private static readonly string[] Allowed =
    [
        "DotCharter.Services/Library/",
        "DotCharter.Services/Package/DcSong/Library/",
        "DotCharter.UI/App."
    ];

    [Fact]
    public void Only_the_composition_root_names_a_library_package_source()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "DotCharter.Services", "DotCharter.Presentation", "DotCharter.UI", "DotCharter.Tone" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
                if (Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal))) continue;
                var code = SourceText.StripCommentsKeepingLines(File.ReadAllText(file));
                foreach (Match match in SourceName().Matches(code))
                    offenders.Add($"{relative}:{SourceText.LineAt(code, match.Index)}: names {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, LintReport.Of(
            "Callers reach the library through ILibraryService / ILibraryAudioPreparer / ILibrarySongPlayability / ILibraryPackageProbe, never a package source",
            offenders,
            "ask the container for the neutral interface; register a new source as an ILibraryPackageSource in App.EditingServices",
            "docs/agents/services/parsing-psarc.md"));
    }

    [Fact]
    public void The_lint_sees_a_source_named_in_code_and_not_one_named_in_a_comment()
    {
        var code = SourceText.StripCommentsKeepingLines("""
            // DcSongLibrarySource in a comment is fine
            var source = new DcSongLibrarySource(logger, cache, () => null);
            """);

        Assert.Equal(["DcSongLibrarySource"], SourceName().Matches(code).Select(m => m.Value));
    }

    [GeneratedRegex(@"\bDcSongLibrarySource\b")]
    private static partial Regex SourceName();
}
