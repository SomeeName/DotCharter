using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Localization;

/// <summary>
///     No display code hands the RESOURCE culture (<c>ILocalizationService.Culture</c>) to a
///     formatter as its <see cref="IFormatProvider" />. It is the lint behind the cases in
///     <see cref="FormattingCultureTests" />, which move the process culture and so run in the
///     serialized localization collection; this half only parses source files, writes no
///     culture, and therefore runs beside the rest of the gate.
/// </summary>
public sealed class ResourceCultureFormatLintTests
{
    /// <summary>
    ///     <c>ILocalizationService.Culture</c> is
    ///     the RESOURCE culture: handing it to a <c>ToString</c>, a
    ///     <c>string.Create</c> or a formatter as the <see cref="IFormatProvider" />
    ///     prints numbers in the UI language. Eight sites did. A read that is not an
    ///     argument (the tone narrator's language callback in <c>App.MatchServices</c>)
    ///     is not a formatting use and is not flagged.
    /// </summary>
    [Fact]
    public void NoDisplayCode_FormatsANumberWithTheResourceCulture()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var project in DisplayProjects)
        {
            var directory = Path.Combine(root, project);
            Assert.True(Directory.Exists(directory), $"'{project}' does not exist; the lint is blind.");
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                scanned++;
                foreach (var line in ResourceCultureFormatLines(File.ReadAllText(file)))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
            }
        }

        Assert.True(scanned > 1500, $"only {scanned} files scanned; the walk went blind");
        Assert.True(offenders.Count == 0,
            "These sites format a value with ILocalizationService.Culture, the UI LANGUAGE. Numbers follow "
            + "the Windows region (CultureInfo.CurrentCulture); see DotCharter.Localization/AGENTS.md, "
            + "'The UI LANGUAGE and the FORMATTING culture are two different settings':"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The probe: the pattern still recognises each shape it has to.</summary>
    [Fact]
    public void TheResourceCultureLint_FlagsEachShape_AndNotANonFormattingRead()
    {
        const string bad = """
            class A
            {
                string X(ILocalizationService loc, double v) => v.ToString("0.0", loc.Culture);
                string Y(double v) => string.Create(LocalizationService.Instance.Culture, $"{v}");
                string Z(double v) => Fmt(v, _localization.Culture);
                System.Globalization.CultureInfo C() { var c = _loc.Culture; return c; }
            }
            """;
        const string fine = """
            class B
            {
                // v.ToString("0.0", loc.Culture) in a comment is not code
                object Narrator(IServiceProvider sp) => new ToneNarrator(() => sp.Get().Culture);
                string W(double v) => v.ToString("0.0", CultureInfo.CurrentCulture);
            }
            """;

        Assert.Equal(4, ResourceCultureFormatLines(bad).Count);
        Assert.Empty(ResourceCultureFormatLines(fine));
    }

    private static readonly string[] DisplayProjects =
    [
        "DotCharter.UI", "DotCharter.Presentation", "DotCharter.Presentation.Fakes", "DotCharter.Tone.UI",
        "DotCharter.Tone", "DotCharter.Services", "DotCharter.RigLab", "DotCharter.UI.Gallery"
    ];

    /// <summary>The lines where a <c>.Culture</c> member is passed as an argument or
    /// captured into a variable: the two ways it becomes a format provider.</summary>
    private static List<int> ResourceCultureFormatLines(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.Text == "Culture")
            .Where(access => access.Parent is ArgumentSyntax or EqualsValueClauseSyntax)
            .Select(access => access.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
