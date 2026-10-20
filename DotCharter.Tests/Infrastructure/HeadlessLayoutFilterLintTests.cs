using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Every headless class that asserts a width or a height runs in German and
///     Japanese before a release.
///     <para>
///         The release preflight (<c>tools/release/New-Release.ps1</c>) runs the
///         whole headless suite in en-US and then only <c>$HeadlessLayoutFilter</c>
///         in each of <c>$HeadlessSweepCultures</c> (de-DE, ja-JP): German prose
///         runs ~40 % longer and Japanese sets differently, so that is where text
///         clips, wraps and overflows. A class outside the filter is only ever
///         measured in English. The list was kept by hand ("if a new class
///         starts asserting anything width-shaped, add it"), and when this lint
///         was written 66 classes that compare a size were missing from it.
///     </para>
///     <para>
///         What counts (<see cref="HeadlessLayoutAssertions" />): an
///         <c>Assert.*(…)</c> whose comment- and literal-stripped text reads
///         <c>.Width</c>, <c>.Height</c>, a <c>Width:</c> / <c>Height:</c>
///         property pattern, <c>Max/MinWidth</c>, <c>Max/MinHeight</c> or an edge
///         (<c>.Right</c>, <c>.Bottom</c>, <c>.Left</c>, <c>.Top</c> — not
///         <c>HorizontalAlignment.Right</c> or <c>Padding.Left</c>), UNLESS that
///         size is only checked to be non-zero (<c>&gt; 0</c>, <c>&gt; 1</c>,
///         <c>&gt;= 0.5</c>, <c>{ Width: &gt; 1 }</c>). "It rendered something"
///         is the construction smoke every view has, and no language changes it.
///         A size read only inside the failure MESSAGE does not count either.
///     </para>
///     <para>
///         A class that compares a size and still has no business in the
///         localized pass goes in <see cref="NotCultureLayout" /> with its
///         reason. The list may only hold classes the detector flags and the
///         filter does not, so an entry cannot outlive its reason.
///     </para>
/// </summary>
public sealed class HeadlessLayoutFilterLintTests
{
    /// <summary>
    ///     Classes that compare a size whose value no UI language can change.
    ///     Every entry says why; "it is slow" is not a reason — the localized pass
    ///     is sharded and its cost is recorded beside the filter.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotCultureLayout = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ComposedSurfaceGridUiPerfSmokeTests"] =
            "compares a composed surface's reported pixel size with its OWN Bounds times the scale — an identity " +
            "that holds at any size, so a longer string can move both sides but never break it",

        // A size floor that is only the PRECONDITION of what the class measures
        // (allocations, latency, a prewarm, a drag, a scroll, a capture): "the
        // thing under test laid out at all". Longer text can only grow it.
        ["GameplaySplitterUiPerfSmokeTests"] =
            "compares the editor's row geometry during a run (the band, the splitter, the run's top and bottom) with " +
            "itself; the rows are pixel and star lengths no string sets, and the notation band's height is a policy value",
        ["BendEditorSustainCurveUiPerfSmokeTests"] =
            "checks where the bend curve starts and ends against the plot's own edges; its one size check (> 1 x 1) " +
            "only says the editor laid out, and the graph's tick labels are fixed glyphs, not localized text",
        ["EqPlotRenderAllocationUiPerfSmokeTests"] =
            "counts the EQ plot's render allocations; its one size check (> 100 x 50) only says the plot laid out " +
            "before the count, in a fixed window",
        ["GalleryPagesUiPerfSmokeTests"] =
            "its one size check is that each page fills its fixed 1600-px window (> 1000 x 500) — 'it rendered " +
            "something'; text cannot shrink a page below that",
        ["LibraryResponsivenessUiPerfSmokeTests"] =
            "measures selection latency; its one size check is the selected row being taller than 30 px, which " +
            "longer text only makes taller",
        ["PlayModeScreensCaptureUiPerfSmokeTests"] =
            "a capture harness; its one size check is each screen filling its fixed window (> 1000 x 500)",
        ["RigDesignCompareUiPerfSmokeTests"] =
            "a capture harness; its size checks are only that the tone browser or editor body laid out (> 100 px " +
            "tall) before the capture",
        ["RigSelectionLatencyUiPerfSmokeTests"] =
            "times node selection; its one size check is the editor body laying out (> 100 px tall) after each select",
        ["RigSelectionOpensTheEditorUiPerfSmokeTests"] =
            "its one size check is the precondition that the expanded rack overflows its viewport, so the scroll " +
            "it then tests means something; longer text only overflows it more",
        ["ScreenHostUiPerfSmokeTests"] =
            "tests screen and dialog routing; its size checks are only that the start screen fills its window " +
            "(> 1000 x 500) and the message prompt's card laid out (> 300 x 80), which text only grows",
        ["SettingsShellUiPerfSmokeTests"] =
            "its one size check reads the tab body's MaxWidth PROPERTY (the 1040 column token), a style value no " +
            "string can change, not a laid-out size",
        ["SheetOverlayCaptureUiPerfSmokeTests"] =
            "a capture harness over the AlphaTab-engraved score; its size check is each overlay matching the " +
            "canvas it covers — an identity that holds at any size, and the score carries no UI string",
        ["TitleBarDragUiPerfSmokeTests"] =
            "tests what drags the window; its one size check is the go-to-anything button laying out (> 20 x 10) " +
            "before it is hit-tested",
        ["ToneBrowserChunkedPrewarmUiPerfSmokeTests"] =
            "tests the chunked prewarm; its one size check is that the shown cards wrapped onto more than one row, " +
            "i.e. the panel left its hidden, limited layout",

        // "Takes no room": a hidden block measured at <= 1 px. Collapsed is
        // collapsed in every language.
        ["ToneDesignMatchUiPerfSmokeTests"] =
            "its size checks are only that the hidden results list and match block take no room (<= 1 px)",
        ["ToneSectionsUiPerfSmokeTests"] =
            "its size checks are only that the hidden section list and section host take no room (<= 1 px)",
    };

    /// <summary>
    ///     Filter entries the detector cannot see: they collect every layout
    ///     fault into an offender list and assert the list is empty, so no
    ///     <c>Assert.*(…)</c> names a size. Any OTHER filter entry must be
    ///     flagged, so a class that stops comparing sizes leaves the localized
    ///     pass instead of costing it time.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LayoutThroughOffenderLists = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UiSweepUiPerfSmokeTests"] =
            "the sweep over every view: clipped text, overflow and overlap are UiSweep findings, asserted as an empty report",
    };

    [Fact]
    public void EveryLayoutAssertingHeadlessClass_IsInTheReleaseCultureLayoutPass()
    {
        var filter = ReleaseLayoutFilter();
        var flagged = FlaggedClasses();

        var missing = flagged
            .Where(c => !InFilter(filter, c.Key) && !NotCultureLayout.ContainsKey(c.Key))
            .Select(c => $"{c.Key}: {c.Value[0]}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These *UiPerfSmokeTests classes compare a width or a height but are not in $HeadlessLayoutFilter " +
            "(tools/release/New-Release.ps1), so a release only ever measures them in English. Add each as " +
            "'--filter-class', '*<Class>', or — only if no language can change the size it asserts — to " +
            "NotCultureLayout with the reason:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>A filter entry that matches no class sweeps nothing, one whose
    /// class compares no size spends the localized pass's time for nothing, and
    /// an exclusion for a class that no longer compares a size (or is in the
    /// filter after all) is a stale reason.</summary>
    [Fact]
    public void EveryFilterEntryAndExclusion_NamesALiveClass()
    {
        var filter = ReleaseLayoutFilter();
        var classes = UiPerfSmokeSources().Keys.ToHashSet(StringComparer.Ordinal);
        var flagged = FlaggedClasses();

        var deadEntries = filter.Where(p => !classes.Any(c => c.EndsWith(p, StringComparison.Ordinal))).ToList();
        Assert.True(deadEntries.Count == 0,
            "$HeadlessLayoutFilter names classes that do not exist: " + string.Join(", ", deadEntries));

        var compareNothing = filter
            .Where(p => !flagged.Keys.Any(c => c.EndsWith(p, StringComparison.Ordinal)) && !LayoutThroughOffenderLists.ContainsKey(p))
            .ToList();
        Assert.True(compareNothing.Count == 0,
            "$HeadlessLayoutFilter names classes that compare no width or height, so the de-DE/ja-JP pass spends " +
            "time on them for nothing — drop them from the filter (or, if they assert layout through an offender " +
            "list, add them to LayoutThroughOffenderLists): " + string.Join(", ", compareNothing));

        foreach (var (name, reason) in LayoutThroughOffenderLists)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"LayoutThroughOffenderLists['{name}'] gives no reason");
            Assert.True(InFilter(filter, name), $"LayoutThroughOffenderLists lists {name}, which is not in $HeadlessLayoutFilter");
            Assert.False(flagged.ContainsKey(name),
                $"LayoutThroughOffenderLists lists {name}, which the detector now flags — delete the entry");
        }

        foreach (var (name, reason) in NotCultureLayout)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"NotCultureLayout['{name}'] gives no reason");
            Assert.True(flagged.ContainsKey(name),
                $"NotCultureLayout lists {name}, which no longer compares a size — delete the entry");
            Assert.False(InFilter(filter, name),
                $"NotCultureLayout lists {name}, which IS in $HeadlessLayoutFilter — keep one of the two");
        }
    }

    public static TheoryData<string, string, bool> Fixtures => new()
    {
        {
            "an upper bound on a width",
            """
            public sealed class Fixture
            {
                [Fact] public void Fits() => Assert.True(label.Bounds.Width <= column.Bounds.Width + 0.5, "clipped");
            }
            """,
            true
        },
        {
            "a minimum height spread over lines",
            """
            Assert.True(
                row.Bounds.Height >= 28,
                $"the row is {row.Bounds.Height} px");
            """,
            true
        },
        {
            "a property pattern with a real floor",
            "Assert.True(card.Bounds is { Width: > 300, Height: > 80 }, \"card\");",
            true
        },
        {
            "a non-zero floor AND an upper bound in one pattern",
            "Assert.True(header.Bounds.Height is > 1 and <= 32, \"header\");",
            true
        },
        {
            "an edge comparison",
            "Assert.True(map.Bottom <= footer.Top + 1, \"overlap\");",
            true
        },
        {
            "a MaxWidth cap",
            "Assert.Equal(420, popover.MaxWidth);",
            true
        },
        {
            "only non-zero checks: the construction smoke",
            """
            Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0, $"laid out {view.Bounds.Width}x{view.Bounds.Height}");
            Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, "card");
            Assert.True(body.DesiredSize is { Width: > 1, Height: > 1 }, "body");
            Assert.True(part.Bounds.Width >= 1, "part");
            Assert.True(p.Flyout.Content is Control { DesiredSize.Height: 0 }, "measured early");
            """,
            false
        },
        {
            "a size only in a comment, a message or an alignment",
            """
            // Assert.True(view.Bounds.Width <= 400) used to live here.
            /* Assert.Equal(12, x.Bounds.Height); */
            Assert.True(ok, $"the row was {row.Bounds.Width:0} px wide");
            Assert.Equal(HorizontalAlignment.Right, cell.HorizontalAlignment);
            Assert.Equal(TextAlignment.Left, cell.TextAlignment);
            var w = row.Bounds.Width;
            """,
            false
        },
    };

    /// <summary>The detector on fixtures: a width-asserting class is flagged; a
    /// class that only checks non-zero, or names a size only in prose, is not.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheDetector_FlagsSizeComparisons_AndNotTheNonZeroSmoke(string what, string source, bool expected)
    {
        var found = HeadlessLayoutAssertions.In(source);
        Assert.True((found.Count > 0) == expected,
            $"{what}: expected {(expected ? "a" : "no")} layout assertion, found {found.Count}: {string.Join(" | ", found)}");
    }

    private static bool InFilter(IReadOnlyList<string> filter, string className) =>
        filter.Any(p => className.EndsWith(p, StringComparison.Ordinal));

    /// <summary>Class name → its first layout assertion, for every class the detector flags.</summary>
    private static Dictionary<string, List<string>> FlaggedClasses()
    {
        var sources = UiPerfSmokeSources();
        Assert.True(sources.Count >= 120, $"only {sources.Count} *UiPerfSmokeTests classes found — the walk is not reaching DotCharter.Tests");

        var flagged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, parts) in sources)
        {
            var found = parts.SelectMany(HeadlessLayoutAssertions.In).ToList();
            if (found.Count > 0) flagged[name] = found;
        }

        // Non-vacuity: a detector that has gone blind passes everything.
        Assert.True(flagged.Count >= 60, $"only {flagged.Count} classes flagged as asserting a size — the detector has gone blind");
        return flagged;
    }

    /// <summary>Every <c>*UiPerfSmokeTests</c> class → the source of each file that
    /// declares (a part of) it. A partial class is one class across its files.</summary>
    private static Dictionary<string, List<string>> UiPerfSmokeSources()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
                continue;

            var text = File.ReadAllText(file);
            foreach (var name in HeadlessLayoutAssertions.DeclaredUiPerfSmokeClasses(text))
            {
                if (!map.TryGetValue(name, out var parts)) map[name] = parts = [];
                parts.Add(text);
            }
        }

        return map;
    }

    /// <summary>The class names in <c>$HeadlessLayoutFilter</c>, without the leading <c>*</c>.</summary>
    private static List<string> ReleaseLayoutFilter()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));
        var block = Regex.Match(script, @"^\$HeadlessLayoutFilter\s*=\s*@\((?<body>[^)]*)\)", RegexOptions.Multiline);
        Assert.True(block.Success, "New-Release.ps1 no longer assigns $HeadlessLayoutFilter = @(…) — this lint is blind");

        var entries = Regex.Matches(block.Groups["body"].Value, @"'--filter-class'\s*,\s*'(?<p>[^']*)'")
            .Select(m => m.Groups["p"].Value)
            .ToList();
        Assert.True(entries.Count >= 20, $"only {entries.Count} entries parsed from $HeadlessLayoutFilter");

        var malformed = entries.Where(e => !Regex.IsMatch(e, @"^\*\w+UiPerfSmokeTests$")).ToList();
        Assert.True(malformed.Count == 0,
            "$HeadlessLayoutFilter entries must be '*<Class>UiPerfSmokeTests': " + string.Join(", ", malformed));

        return entries.Select(e => e[1..]).ToList();
    }
}

/// <summary>
///     Finds the assertions in a test source that compare a width or a height.
///     See <see cref="HeadlessLayoutFilterLintTests" /> for what counts and why.
/// </summary>
internal static partial class HeadlessLayoutAssertions
{
    public static IEnumerable<string> DeclaredUiPerfSmokeClasses(string source) =>
        ClassDeclaration().Matches(MaskLiterals(SourceText.StripComments(source))).Select(m => m.Groups["n"].Value).Distinct(StringComparer.Ordinal);

    /// <summary>Each <c>Assert.*(…)</c> in <paramref name="source" /> that compares a
    /// size against anything but zero, whitespace-collapsed.</summary>
    public static List<string> In(string source)
    {
        var code = MaskLiterals(SourceText.StripComments(source));
        var found = new List<string>();
        foreach (Match m in AssertCall().Matches(code))
        {
            var call = code[m.Index..ClosingParen(code, m.Index + m.Length)];
            if (ComparesASize(call)) found.Add(Whitespace().Replace(call, " "));
        }

        return found;
    }

    private static bool ComparesASize(string call)
    {
        foreach (Match token in SizeToken().Matches(call))
        {
            if (!NonZeroCheck().IsMatch(call, token.Index + token.Length)) return true;
        }

        return false;
    }

    private static int ClosingParen(string code, int afterOpen)
    {
        var depth = 1;
        var i = afterOpen;
        for (; i < code.Length && depth > 0; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')') depth--;
        }

        return i;
    }

    /// <summary>Empties every string and char literal, so neither a size read in
    /// a failure message nor a parenthesis in one is mistaken for code.</summary>
    private static string MaskLiterals(string code)
    {
        code = CharLiteral().Replace(code, "'_'");
        code = RawString().Replace(code, "\"\"");
        code = VerbatimString().Replace(code, "\"\"");
        return RegularString().Replace(code, "\"\"");
    }

    [GeneratedRegex(@"\b(?:partial\s+)?class\s+(?<n>\w+UiPerfSmokeTests)\b")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\bAssert\.\w+\s*\(")]
    private static partial Regex AssertCall();

    // A size: .Width / .Height, a Width: / Height: property pattern, Max/Min
    // Width/Height, or an edge that is not an enum member or a Thickness side.
    [GeneratedRegex(@"\.(?:Width|Height)\b|(?<![\w.])(?:Width|Height)(?=\s*:)|\b(?:Max|Min)(?:Width|Height)\b|(?<!(?:Alignment|Dock|Mode|Side|Key|Edge|Direction|Anchor|Position|Padding|Margin|Thickness|Inset|Corner|Radius))\.(?:Right|Bottom|Left|Top)\b")]
    private static partial Regex SizeToken();

    // What directly follows a size that is only being checked to EXIST: > 0,
    // > 1, >= 0.5 (also as `is > 1` or a `Width: > 1` pattern, but not
    // `is > 1 and <= 32`), a `Height: 0` pattern, or != 0.
    [GeneratedRegex(@"\G\s*(?::\s*)?(?:is\s+)?>=?\s*(?:0|1|0\.5)(?:\.0+)?[dDfF]?(?![\w.])(?!\s*(?:and|or)\b)|\G\s*:\s*0(?![\w.])|\G\s*!=\s*0(?![\w.])")]
    private static partial Regex NonZeroCheck();

    [GeneratedRegex(@"'(?:[^'\\\r\n]|\\.)+'")]
    private static partial Regex CharLiteral();

    [GeneratedRegex("\\$*\"\"\"[\\s\\S]*?\"\"\"")]
    private static partial Regex RawString();

    [GeneratedRegex("(?:\\$@|@\\$|@)\"(?:[^\"]|\"\")*\"")]
    private static partial Regex VerbatimString();

    [GeneratedRegex("\\$?\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex RegularString();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
