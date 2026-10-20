using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI;

/// <summary>
///     Which resource tokens may be <c>{StaticResource}</c> and which must stay
///     <c>{DynamicResource}</c> (#241).
///     <para>
///         A <c>DynamicResource</c> keeps a live expression per use — 2.16 M of
///         them, 281 MB, in the prewarmed app (#239) — and for a value nothing
///         ever rewrites that is pure cost. The size ladders (<c>Space*</c>,
///         <c>Padding*</c>, <c>FireSize*</c>, <c>FirePad*</c>, radii, weights,
///         font families…) are such values: the interface scale is a
///         <c>LayoutTransform</c> on the root (<c>UiScaleController</c>), not a
///         token rewrite, and the only code that writes application resources at
///         runtime is <c>ThemeService</c> (colours) and
///         <c>UiBreakpointController</c> (the wide ladder). So:
///     </para>
///     <list type="bullet">
///         <item>a token <c>Ladders.Wide.axaml</c> swaps is referenced ONLY
///         dynamically — a static use would freeze at the base value and never
///         see the breakpoint;</item>
///         <item>a fixed token is referenced ONLY statically — so a new use does
///         not quietly bring the cost back;</item>
///         <item>nothing else writes application resources, and the colour writer
///         cannot reach a fixed key;</item>
///         <item><c>StaticResource</c> resolves at LOAD, so inside the theme's own
///         include chain a fixed token must be defined before it is used.</item>
///     </list>
///     Colours and brushes are not in scope at all: the palette switches at
///     runtime and every one stays dynamic.
/// </summary>
public sealed class FixedTokenReferenceTests
{
    private const string WideLadder = "DotCharter.Tone.UI/Styles/FireTokens/Ladders.Wide.axaml";

    /// <summary>The dictionaries whose fixed-type keys are the app-wide token
    /// ladders. Fluent override keys (<c>FireMenuChrome.axaml</c>,
    /// <c>Dock.axaml</c>) are NOT here: those are re-stated in a nested scope
    /// on purpose, which only a dynamic lookup sees.</summary>
    private static readonly string[] TokenDictionaries =
    [
        "DotCharter.Tone.UI/Styles/Resources/Metrics.axaml",
        "DotCharter.Tone.UI/Styles/Typography.axaml",
        "DotCharter.Tone.UI/Styles/FireTokens/Palette.axaml",
        "DotCharter.Tone.UI/Styles/FireTokens/Ladders.axaml",
        "DotCharter.Tone.UI/Styles/FireTokens/PopoverTokens.axaml"
    ];

    /// <summary>The value types that make a token "fixed". No colour, no brush,
    /// no <c>BoxShadows</c> (a shadow carries a colour).</summary>
    private static readonly string[] FixedTypes =
        ["x:Double", "Thickness", "CornerRadius", "FontWeight", "sys:TimeSpan", "FontFamily"];

    /// <summary>The only product code that writes application resources at runtime.</summary>
    private static readonly string[] RuntimeResourceWriters =
    [
        "DotCharter.UI/Services/UiBreakpointController.cs",
        "DotCharter.UI/Theming/ThemeService.cs"
    ];

    /// <summary>Files that may still reach a fixed token dynamically, each with
    /// its reason. Shrink-only.</summary>
    /// <remarks>Empty since #242 converted the rig's style parts.</remarks>
    private static readonly (string Path, string Reason)[] StillDynamic = [];

    /// <summary>Dialog VIEWS resolve every resource dynamically by their own rule
    /// (<c>DialogConstructionUiPerfSmokeTests.DialogViews_PullResourcesThroughDynamicResource</c>);
    /// the same top-level folders that rule reads. Their style grammar
    /// (<c>Views/Dialogs/DialogStyles/</c>) is converted.</summary>
    private static readonly string[] DialogViewFolders =
        ["DotCharter.UI/Views/Dialogs/", "DotCharter.UI/Views/Diagnostics/"];

    private static readonly Regex Reference = new(
        @"\{(?<kind>Dynamic|Static)Resource\s+(?:ResourceKey=)?(?<key>[A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);

    [Fact]
    public void WideLadderTokens_AreReferencedOnlyDynamically()
    {
        var wide = KeysIn(WideLadder, ["x:Double"]);
        Assert.True(wide.Count >= 10, $"only {wide.Count} keys found in {WideLadder}; the scan is broken");

        var dynamicUses = 0;
        var offenders = new List<string>();
        foreach (var (file, m) in References())
        {
            if (!wide.Contains(m.Groups["key"].Value)) continue;
            if (m.Groups["kind"].Value == "Dynamic") dynamicUses++;
            else offenders.Add($"{file}: {m.Value}");
        }

        Assert.True(dynamicUses > 10, $"only {dynamicUses} dynamic uses of the wide ladder found; the scan is broken");
        Assert.True(offenders.Count == 0,
            "These swap at runtime when the window crosses the Wide breakpoint (UiBreakpointController), " +
            "so a StaticResource freezes them at the base value:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void FixedTokens_AreReferencedOnlyStatically()
    {
        var fix = FixedKeys();
        var staticUses = 0;
        var offenders = new List<string>();
        foreach (var (file, m) in References())
        {
            if (!fix.Contains(m.Groups["key"].Value)) continue;
            if (m.Groups["kind"].Value == "Static") staticUses++;
            else if (!MayStayDynamic(file)) offenders.Add($"{file}: {m.Value}");
        }

        Assert.True(staticUses > 2000, $"only {staticUses} static uses of the fixed tokens found; the scan is broken");
        Assert.True(offenders.Count == 0,
            "These tokens never change at runtime; a DynamicResource costs a live expression per use (#239). " +
            "Use StaticResource:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void FixedTokens_AreNeverWrittenAtRuntime()
    {
        var root = TestPaths.RepoRoot();
        var writer = new Regex(
            @"Resources\s*\[[^\]]+\]\s*=(?![=>])|Resources\.(?:Add|Remove|TryAdd|Clear)\s*\(|" +
            @"MergedDictionaries\.(?:Add|Insert|Remove|RemoveAt|Clear)\s*\(|\.Resources\s*=(?![=>])");
        var writers = ProductFiles(root, "*.cs")
            .Where(f => writer.IsMatch(SourceText.StripComments(File.ReadAllText(Path.Combine(root, f)))))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(RuntimeResourceWriters.Order(StringComparer.Ordinal), writers);

        // ThemeService writes Color values under the theme's colour keys (plus
        // Fluent's accent family); none of those may name a fixed token.
        var fix = FixedKeys();
        var colourKeys = ThemeTokens.All
            .Concat(FluentAccent.Derive(Colors.OrangeRed).Keys)
            .Concat(ThemePresets.All.SelectMany(p => p.Colors.Keys))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(colourKeys.Count > 20, "the colour keys were not found");
        Assert.Empty(colourKeys.Intersect(fix));

        // The wide ladder's keys live in the base ladder too — the subtraction
        // in FixedKeys is what keeps them dynamic.
        var wide = KeysIn(WideLadder, ["x:Double"]);
        Assert.Empty(wide.Intersect(fix));
        Assert.Subset(TokenDictionaries.SelectMany(f => KeysIn(f, FixedTypes)).ToHashSet(StringComparer.Ordinal), wide);
    }

    /// <summary>A <c>StaticResource</c> in a STYLE setter has no delayed
    /// fallback: if the key is not already loaded when the file is parsed, the
    /// app throws at <c>App.Initialize()</c>. Both theme chains are walked in
    /// include order, the app's and the standalone rig's. A use inside a
    /// <c>ControlTheme</c> or a template is DEFERRED content — built on first
    /// lookup or instantiation, after the whole chain has loaded — so it only
    /// has to be defined somewhere in the chain (<c>ControlThemes.axaml</c>
    /// reads <c>FontSize*</c>, which <c>Typography.axaml</c> defines after it).</summary>
    [Theory]
    [InlineData("DotCharter.UI/Styles/Theme.axaml")]
    [InlineData("DotCharter.Tone.UI/Styles/RigTheme.axaml")]
    public void FixedTokens_InTheThemeChain_AreDefinedBeforeTheirFirstUse(string chain)
    {
        var fix = FixedKeys();
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var visited = new List<string>();
        var offenders = new List<string>();
        var deferred = new List<(string Where, string Key)>();
        var lazySpans = 0;
        Walk(chain);
        offenders.AddRange(deferred.Where(d => !defined.Contains(d.Key)).Select(d => $"{d.Where} (deferred, never defined)"));

        Assert.True(visited.Count >= 10, $"{chain} expanded to only {visited.Count} files; the include scan is broken");
        Assert.True(defined.IsSupersetOf(fix), $"{chain} does not define every fixed token");
        Assert.True(lazySpans > 10, $"only {lazySpans} ControlTheme/template blocks found; the deferred scan is broken");
        Assert.True(offenders.Count == 0,
            $"In {chain} these StaticResource uses come BEFORE the token is loaded:\n  " + string.Join("\n  ", offenders));
        return;

        void Walk(string file)
        {
            if (visited.Contains(file)) return;
            visited.Add(file);
            var text = Markup(file);
            var lazy = Regex.Matches(text, @"<(?<t>ControlTheme|ControlTemplate|DataTemplate|TreeDataTemplate)\b.*?</\k<t>>",
                    RegexOptions.Singleline)
                .Select(m => (Start: m.Index, End: m.Index + m.Length))
                .ToList();
            lazySpans += lazy.Count;
            var events = Regex.Matches(text, @"<(?:Style|Resource|MergeResource)Include\s+Source=""avares://(?<asm>[^/]+)/(?<path>[^""]+)""")
                .Select(m => (m.Index, Kind: 'i', Value: ProjectOf(m.Groups["asm"].Value) is { } p ? $"{p}/{m.Groups["path"].Value}" : null))
                .Concat(Regex.Matches(text, @"<[A-Za-z:]+\s+x:Key=""(?<key>[^""]+)""").Select(m => (m.Index, Kind: 'd', Value: (string?)m.Groups["key"].Value)))
                .Concat(Reference.Matches(text).Where(m => m.Groups["kind"].Value == "Static")
                    .Select(m => (m.Index, Kind: 'r', Value: (string?)m.Groups["key"].Value)))
                .OrderBy(e => e.Index);
            foreach (var (index, kind, value) in events)
            {
                if (value is null) continue;
                if (kind == 'i') Walk(value);
                else if (kind == 'd') defined.Add(value);
                else if (!fix.Contains(value) || defined.Contains(value)) continue;
                else if (lazy.Any(s => index > s.Start && index < s.End))
                    deferred.Add(($"{file}:{text.Take(index).Count(c => c == '\n') + 1} {{StaticResource {value}}}", value));
                else offenders.Add($"{file}:{text.Take(index).Count(c => c == '\n') + 1} {{StaticResource {value}}}");
            }
        }
    }

    [Fact]
    public void EveryStillDynamicFile_ExistsAndStillNeedsIt()
    {
        var root = TestPaths.RepoRoot();
        var fix = FixedKeys();
        foreach (var (path, reason) in StillDynamic)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), path);
            Assert.True(File.Exists(Path.Combine(root, path)), $"{path} is gone; delete its StillDynamic entry");
            Assert.True(Reference.Matches(Markup(path)).Any(m => m.Groups["kind"].Value == "Dynamic" && fix.Contains(m.Groups["key"].Value)),
                $"{path} no longer reaches a fixed token dynamically; delete its StillDynamic entry");
        }
    }

    private static bool MayStayDynamic(string file) =>
        StillDynamic.Any(s => s.Path == file)
        || DialogViewFolders.Any(d => file.StartsWith(d, StringComparison.Ordinal)
                                      && !file[d.Length..].Contains('/')
                                      && !file.EndsWith("/DialogStyles.axaml", StringComparison.Ordinal));

    private static HashSet<string> FixedKeys()
    {
        var keys = TokenDictionaries.SelectMany(f => KeysIn(f, FixedTypes)).ToHashSet(StringComparer.Ordinal);
        keys.ExceptWith(KeysIn(WideLadder, ["x:Double"]));
        Assert.True(keys.Count >= 250, $"only {keys.Count} fixed tokens found; the dictionaries have moved");
        return keys;
    }

    private static HashSet<string> KeysIn(string file, string[] types)
    {
        var alt = string.Join('|', types.Select(Regex.Escape));
        return Regex.Matches(Markup(file), $@"<(?:{alt})\s+x:Key=""(?<key>[^""]+)""")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string File, Match Match)> References()
    {
        var root = TestPaths.RepoRoot();
        return ProductFiles(root, "*.axaml")
            .SelectMany(f => Reference.Matches(Markup(f)).Select(m => (f, m)));
    }

    /// <summary>The file with its comments blanked to their line breaks, so a
    /// reported line number is the real one.</summary>
    private static string Markup(string relative) =>
        Regex.Replace(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative)), "<!--.*?-->",
            m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);

    private static string? ProjectOf(string assembly) => assembly switch
    {
        "DotCharter" => "DotCharter.UI",
        _ when assembly.StartsWith("DotCharter.", StringComparison.Ordinal) => assembly,
        _ => null
    };

    private static IEnumerable<string> ProductFiles(string root, string pattern) =>
        Directory.EnumerateDirectories(root, "DotCharter.*")
            .Where(p => Path.GetFileName(p) != "DotCharter.Tests")
            .SelectMany(p => Directory.EnumerateFiles(p, pattern, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal));
}
