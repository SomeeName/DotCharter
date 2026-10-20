using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>A shape a theme must be able to restate is a TOKEN, not a literal.</b>
///     <para>
///         The colour half of this rule has always been enforced ("no hard-coded
///         colors in axaml", <c>DotCharter.UI/AGENTS.md</c>); the SHAPE half was
///         not, and that is what stopped a design proposal from being a theme:
///         the corner radii of the slider track, the thumb, the tab pipe, the
///         nav-row accent bar and the Fire meters, and every border weight, were
///         literals inside control templates
///         (<c>artifacts/design-proposals/would-need-tokenising.md</c> items
///         2–7). They were tokenised on 2026-09-20 with their existing values,
///         so nothing changed on screen.
///     </para>
///     <para>
///         The exemptions are the rig's HARDWARE edges. A switch collar, a
///         switch ring and a fader cap edge are material — the same call the
///         palette makes for the gear artwork and the string identities — and a
///         theme that restyled them would look like a filter over the
///         photograph. The list only ever SHRINKS: a new literal belongs in a
///         token, not here.
///     </para>
/// </summary>
public sealed class ShapeTokenLintTests
{
    /// <summary>Shape literals that stay, with the reason they are not palette.</summary>
    private static readonly (string File, int Count, string Why)[] KnownMaterialEdges =
    [
        ("Styles/Fire/FireRigBoard.axaml", 2, "the switch collar (1) and its ring (3) — hardware, like the gear art"),
        ("Views/PlayMode/RigModuleEditorStyles.axaml", 3, "two hardware shadows and the fader cap edge"),
    ];

    private static readonly Regex Literal = new(
        @"(?<what>CornerRadius|BorderThickness)=""(?<value>[0-9][^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void NoStyleFileStatesAShapeAsALiteral_ExceptTheRigsMaterialEdges()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var allowed = KnownMaterialEdges.ToDictionary(e => e.File.Replace('/', Path.DirectorySeparatorChar),
            e => e.Count, StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in StyleFiles(root))
        {
            scanned++;
            var relative = UiSourceTree.Relative(root, file);
            var hits = Literal.Matches(SourceText.StripMarkupComments(UiSourceTree.ReadAllText(file)))
                .Select(m => $"{m.Groups["what"].Value}=\"{m.Groups["value"].Value}\"")
                .ToList();
            if (hits.Count == 0) continue;

            var budget = allowed.TryGetValue(relative, out var n) ? n : 0;
            if (hits.Count > budget)
            {
                offenders.Add($"{relative}: {hits.Count} literal(s) [{string.Join(", ", hits.Distinct())}], " +
                              $"{budget} allowed");
            }
        }

        Assert.True(scanned > 20, $"only {scanned} style files scanned — the sweep is not reaching them");
        Assert.True(offenders.Count == 0,
            "a shape a theme must be able to restate is a token (Styles/Resources/Metrics.axaml, " +
            "Styles/FireTokens/Ladders.axaml), not a literal:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     Every style dictionary: the theme under <c>Styles/</c>, and the view-scoped
    ///     grammars under <c>Views/</c> (a file whose root is <c>Styles</c>) — the rig's
    ///     own rules moved into <c>RigModuleEditorStyles.axaml</c> / <c>AmpFxRigStyles.axaml</c>
    ///     under #242, and a scan of <c>Styles/</c> alone would stop seeing them.
    /// </summary>
    private static IEnumerable<string> StyleFiles(string root) =>
        UiSourceTree.EnumerateFiles(Path.Combine(root, "Styles"), "*.axaml", SearchOption.AllDirectories)
            .Concat(UiSourceTree.EnumerateFiles(Path.Combine(root, "Views"), "*.axaml", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(UiSourceTree.ReadAllText(f), @"^\uFEFF?\s*<Styles\b")));

    /// <summary>An entry that stopped being needed must leave the list with it.</summary>
    [Fact]
    public void EveryMaterialEdgeExemption_IsStillUsedInFull()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        foreach (var (file, count, why) in KnownMaterialEdges)
        {
            var path = UiSourceTree.Resolve(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(path), $"{file} is gone — drop its exemption ({why})");
            Assert.Equal(count, Literal.Matches(SourceText.StripMarkupComments(UiSourceTree.ReadAllText(path))).Count);
        }
    }

    /// <summary>The tokens the templates now name really exist, with their old values.</summary>
    [Theory]
    [InlineData("Styles/Resources/Metrics.axaml", "RadiusTrack", "2")]
    [InlineData("Styles/Resources/Metrics.axaml", "RadiusThumb", "7")]
    [InlineData("Styles/Resources/Metrics.axaml", "BorderHairline", "1")]
    [InlineData("Styles/Resources/Metrics.axaml", "BorderFocusRing", "2")]
    [InlineData("Styles/Resources/Metrics.axaml", "BorderTopHairline", "0,1,0,0")]
    [InlineData("Styles/Resources/Metrics.axaml", "BorderBottomHairline", "0,0,0,1")]
    [InlineData("Styles/FireTokens/Ladders.axaml", "FireRadiusTrack", "3")]
    [InlineData("Styles/FireTokens/Ladders.axaml", "FireRadiusBar", "2")]
    [InlineData("Styles/FireTokens/Ladders.axaml", "FireBorderHairline", "1")]
    public void EveryNewShapeToken_KeepsTheValueItReplaced(string file, string key, string value)
    {
        var text = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            file.Replace('/', Path.DirectorySeparatorChar))));
        var declaration = Regex.Match(text, $@"x:Key=""{Regex.Escape(key)}""\s*>\s*(?<v>[^<]+)</");
        Assert.True(declaration.Success, $"{key} is not declared in {file}");
        Assert.Equal(value, declaration.Groups["v"].Value.Trim());
    }

    /// <summary>The lint bites: a literal in a style file is an offender.</summary>
    [Fact]
    public void Probe_TheLintReportsALiteralItIsNotToldToAllow()
    {
        const string markup = """
                              <Styles>
                                  <Style Selector="Border.probe">
                                      <Setter Property="CornerRadius" Value="4" />
                                  </Style>
                                  <Border CornerRadius="9" BorderThickness="1,2,3,4" />
                                  <!-- a comment with CornerRadius="5" does not count -->
                              </Styles>
                              """;

        var hits = Literal.Matches(SourceText.StripMarkupComments(markup)).Select(m => m.Value).ToList();
        Assert.Equal(["CornerRadius=\"9\"", "BorderThickness=\"1,2,3,4\""], hits);
    }
}
