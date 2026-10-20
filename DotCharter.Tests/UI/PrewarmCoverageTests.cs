using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Every big <c>IsVisible</c>-gated surface in the app is either reached by
///     a prewarm mechanism or written down as knowingly cold.</b> Derived from the
///     markup, so a screen added tomorrow is judged the day it compiles.
/// </summary>
/// <remarks>
///     <para>
///         The sibling guards in <c>HiddenSubtreePrewarmerTests</c> check that the
///         mechanisms are WIRED — that both exit paths schedule the drain, that
///         every <c>IPrewarmableSurfaces</c> implementor has an owner branch, that
///         the names <c>FindControl</c> looks up exist in the markup. None of them
///         can notice a surface that was never offered to any mechanism at all:
///         it simply goes on paying its whole first layout on the user's first
///         switch into it, with no warning, no exception and every other test
///         green. That is how the song browser's Library Doctor stayed cold for
///         three releases.
///     </para>
///     <para>
///         <b>The claim is sound, not heuristic.</b> Avalonia skips
///         <c>MeasureCore</c> for an invisible element, and
///         <c>HiddenSubtreePrewarmer.Realize</c> deliberately measures only the
///         VISIBLE children of the root it is given. So a gated element is reached
///         by exactly one of two things: it is NAMED in a prewarm list, or it is
///         swept structurally by one of the three rules in
///         <see cref="CoverageRules" />. Anything else is cold, forever, by
///         construction.
///     </para>
///     <para>
///         <b>Cost is measured in ELEMENTS, not milliseconds.</b> A wall-clock
///         assertion in the default gate flakes the moment suites run in parallel
///         (this repo's own rule). Realization cost tracks the number of visuals a
///         first measure has to style, template and lay out, so the metric here is
///         the element's own markup subtree minus the subtrees of the gated
///         elements inside it — those are separately accounted, and double-counting
///         them made a container that owns nothing but a tab strip read as the
///         largest surface in the app.
///     </para>
///     <para>
///         <see cref="KnownColdSurfaces" /> is the backlog, and it may only ever
///         SHRINK — the same contract as <c>RelayCommandReachabilityTests</c>'
///         <c>KnownUnreachable</c>. An entry that has become covered, or whose
///         surface no longer exists, fails this suite too: a stale exemption is
///         where the next cold screen would hide.
///     </para>
/// </remarks>
public sealed class PrewarmCoverageTests
{
    /// <summary>
    ///     A gated surface has to own at least this many elements before the gate
    ///     asks about it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Re-measured 2026-08-29 over <c>DotCharter.UI/Views</c>: <b>976</b>
    ///         gated elements (930 when this was written), <b>34</b> of them 40 or
    ///         more. Everything below that is a banner, a badge, an empty-state
    ///         card or a disclosure row.
    ///     </para>
    ///     <para>
    ///         <b>Why it is 35 now, measured rather than assumed.</b> The bar
    ///         started at 40 with a six-entry backlog. Applying the coverage rules
    ///         at each candidate threshold on 2026-08-30, the number of UNCOVERED
    ///         surfaces the gate would have demanded an answer for was
    ///         <c>&gt;=40 → 6</c>, <c>&gt;=35 → 10</c>, <c>&gt;=30 → 15</c>,
    ///         <c>&gt;=20 → 38</c>. The 0.49.3 nested-body lists
    ///         (<c>PlayModeView.NestedScreenBodyNames</c>,
    ///         <c>DialogContainerView.NestedDialogBodyNames</c>,
    ///         the settings tabs' Advanced bodies,
    ///         <c>InspectorView.NestedPaneNames</c>) covered nine of those ten
    ///         plus three more in the 30-34 band, which is what let the bar drop
    ///         to 35 with the backlog SHRINKING to one entry rather than growing.
    ///         <b>The ratchet is: empty the list, then lower the threshold.</b>
    ///     </para>
    ///     <para>
    ///         What stands between 35 and 30 is two surfaces no mechanism can
    ///         reach: the 3D HUD's practice strip (the preview panel is
    ///         deliberately never prewarmed — realizing it builds the Vulkan
    ///         surface) and <c>LanSongBoardView</c>'s board body (built by a
    ///         row-level <c>ContentTemplate</c> on the row's first expansion, on
    ///         purpose). Lowering to 30 means two new excuses below; the entry
    ///         above says why that is the wrong order.
    ///     </para>
    ///     <para>
    ///         A real SCREEN does not fit under the bar: the smallest thing the
    ///         shell prewarms by name is 31. <b>Ratchet it DOWN as the backlog
    ///         below empties; never up.</b>
    ///     </para>
    /// </remarks>
    private const int CostThreshold = 35;

    /// <summary>
    ///     The surfaces that are knowingly cold, with the reason each one is not
    ///     fixed yet. Keyed by "<c>&lt;file&gt;#&lt;x:Name or gate&gt;</c>".
    /// </summary>
    /// <remarks>
    ///     A gated body NESTED inside a surface a mechanism already reaches is
    ///     NOT a reason to be here any more — the prewarmer stops at the first
    ///     hidden root on purpose, so "the parent is prewarmed" buys it nothing,
    ///     and the fix is one line: give it an <c>x:Name</c> and append the name
    ///     to its owner's nested-body list (<c>PlayModeView.NestedScreenBodyNames</c>,
    ///     <c>DialogContainerView.NestedDialogBodyNames</c>,
    ///     a settings view's own <c>HiddenSurfaces</c>, <c>InspectorView.NestedPaneNames</c>),
    ///     which <c>HiddenSubtreePrewarmer.HiddenNamedDescendants</c> resolves
    ///     through the logical tree AFTER the gated parent. Five entries left this
    ///     list that way in 0.49.3. What is left is gated by DATA in a way that
    ///     makes the prewarm realize nothing.
    /// </remarks>
    private static readonly Dictionary<string, string> KnownColdSurfaces = new(StringComparer.Ordinal)
    {
        ["PlayMode/AmpFxRigView.axaml#RigBoardPath"] =
            "The rig board's chain subtree — gated by DATA, not navigation: `HasChain` hides it exactly when " +
            "the chain is EMPTY, which is when its segment/lane/card templates have zero items to realize. " +
            "With a chain present the subtree is visible and inflates with the screen's own first layout; a " +
            "prewarm of the hidden state would lay out nothing but the two jacks.",
        ["PlayMode/RigEditors/RigRoutingConsoleView.axaml#RigRoutingConsole"] =
            "The routing console is one of the editor FAMILY views `RigEditorTemplateSelector` builds on " +
            "demand when a junction unit is selected — the whole view inflates with that selection, and " +
            "`HasRouting` is a DATA gate that is true for every unit this view is ever built for. There is " +
            "no hidden state to prewarm: the view does not exist until it is shown."
    };

    /// <summary>
    ///     The STRUCTURAL sweeps, spelled out. Each one mirrors exactly one line of
    ///     production code, named in its <c>Mechanism</c> — a rule that stops
    ///     matching anything is a mechanism that has silently stopped running, so
    ///     <see cref="Every_structural_sweep_rule_still_matches_a_real_surface" />
    ///     fails on that too.
    /// </summary>
    private static readonly (string Mechanism, Func<GatedSurface, bool> Matches)[] CoverageRules =
    [
        ("DialogContainerView.HiddenSurfaces() → HiddenContentOf(each dialog view)",
            s => s.File.StartsWith("Dialogs/", StringComparison.Ordinal) && s.IsRootContent),
        ("PlayModeView.HiddenSurfaces() → HiddenChildrenOf(Content as Panel)",
            s => s.File == "PlayMode/PlayModeView.axaml" && s.ParentIsRootContent)
    ];

    // ── the gate ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_expensive_gated_surface_is_prewarmed_or_knowingly_cold()
    {
        var cold = ExpensiveSurfaces().Where(s => CoverageOf(s) is null).ToList();

        var duplicates = cold.GroupBy(s => s.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            "Two cold surfaces share an identity (" + string.Join(", ", duplicates) + "). Give one of them an " +
            "x:Name — an unnamed surface is identified by its gate expression here, and it is exactly as " +
            "unreachable to a prewarm list.");

        var unlisted = cold.Where(s => !KnownColdSurfaces.ContainsKey(s.Key)).ToList();

        Assert.True(unlisted.Count == 0,
            $"{unlisted.Count} gated surface(s) of {CostThreshold}+ elements are reached by NO prewarm " +
            "mechanism, so each pays its whole first layout on the user's first switch into it (the ≤150 ms " +
            "budget, target <20 ms):\n  " +
            string.Join("\n  ", unlisted.Select(s => $"{s.Key}  ({s.OwnElements} elements, <{s.Tag}>)")) +
            "\n\nOffer it through IPrewarmableSurfaces on its owning view — name it and append it to that " +
            "view's HiddenSurfaces() AFTER its gated parent — or add it to KnownColdSurfaces with the reason.");
    }

    /// <summary>
    ///     The backlog may only shrink. An entry whose surface got covered, shrank
    ///     below the threshold or was deleted has to GO — leaving it is how the
    ///     next cold screen hides behind a name nobody rechecks.
    /// </summary>
    [Fact]
    public void No_exemption_outlives_the_surface_it_excuses()
    {
        var cold = ExpensiveSurfaces().Where(s => CoverageOf(s) is null)
            .Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var stale = KnownColdSurfaces.Keys.Where(k => !cold.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "KnownColdSurfaces still excuses surface(s) that are no longer cold, no longer big enough to " +
            "matter, or no longer exist:\n  " + string.Join("\n  ", stale) +
            "\n\nDelete the entry. This list only ever shrinks.");
    }

    /// <summary>
    ///     A structural rule that matches nothing is a mechanism that has stopped
    ///     running — a renamed Play Mode content panel, a dialog container that no
    ///     longer sweeps its children — and every surface it used to cover goes
    ///     cold at once, silently.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Every_structural_sweep_rule_still_matches_a_real_surface(int rule)
    {
        Assert.True(CoverageRules.Length == 2,
            $"There are {CoverageRules.Length} structural sweep rules but this Theory enumerates 2 — add or " +
            "remove an InlineData so the new rule is checked too.");

        var (mechanism, matches) = CoverageRules[rule];

        Assert.True(AllSurfaces().Any(matches),
            $"No gated surface in the markup is covered by '{mechanism}' any more. Either the mechanism was " +
            "removed and every surface it reached is now cold, or the markup it keys on was renamed and the " +
            "lookup silently returns nothing.");
    }

    /// <summary>
    ///     The naming rule has to see the real prewarm lists. If the source scan
    ///     stops finding them — a partial split, a moved folder — every named
    ///     surface reads as cold at once and the gate above turns into noise.
    /// </summary>
    [Fact]
    public void The_named_coverage_rule_reads_the_real_prewarm_lists()
    {
        var names = PrewarmedNames();

        // One from each list that names surfaces explicitly.
        foreach (var expected in new[] { "MainSplitGrid", "GameSettingsAudioPane", "LibraryNoMatch", "GeneralAdvancedBody" })
        {
            Assert.True(names.Contains(expected),
                $"'{expected}' is prewarmed by name in production code, but the source scan behind this " +
                "suite did not find it — the scan is looking at the wrong files and every named surface " +
                "would now read as cold.");
        }
    }

    // ── the model ───────────────────────────────────────────────────────────

    /// <summary>One <c>IsVisible</c>-gated element, as the markup declares it.</summary>
    public sealed record GatedSurface(
        string File,
        string Tag,
        string? Name,
        string Gate,
        int OwnElements,
        bool IsRootContent,
        bool ParentIsRootContent,
        string? ParentName)
    {
        /// <summary>Stable identity: the <c>x:Name</c> when there is one, else the
        /// gate expression, which is the only other thing about an unnamed surface
        /// a human can recognise.</summary>
        public string Key => $"{File}#{Name ?? Gate}";
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string? CoverageOf(GatedSurface surface)
    {
        if (surface.Name is { } name && PrewarmedNames().Contains(name)) return "named";

        foreach (var (mechanism, matches) in CoverageRules)
        {
            if (matches(surface))
                return mechanism;
        }

        return null;
    }

    private static IEnumerable<GatedSurface> ExpensiveSurfaces() =>
        AllSurfaces().Where(s => s.OwnElements >= CostThreshold);

    private static readonly Lazy<List<GatedSurface>> Surfaces = new(ScanViews, isThreadSafe: true);

    private static List<GatedSurface> AllSurfaces() => Surfaces.Value;

    private static List<GatedSurface> ScanViews()
    {
        var root = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var found = new List<GatedSurface>();

        foreach (var path in UiSourceTree.GetFiles(root, "*.axaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(path);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{path} is not parseable XAML: {ex.Message}");
                return found; // unreachable
            }

            var file = UiSourceTree.Relative(root, path).Replace('\\', '/');
            var rootContent = doc.Root?.Elements()
                .FirstOrDefault(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

            Collect(doc.Root, null, rootContent, file, found);
        }

        return found;
    }

    /// <summary>
    ///     Walks one markup file and records each gated element with its OWN
    ///     element count — its whole subtree minus the subtrees of the outermost
    ///     gated elements inside it, which are separately accounted.
    /// </summary>
    /// <returns>The element count this subtree contributes to its nearest gated
    /// ancestor: the whole subtree when it is itself gated, the sum of its gated
    /// descendants otherwise.</returns>
    private static int Collect(
        XElement? element, XElement? parent, XElement? rootContent, string file, List<GatedSurface> found)
    {
        if (element is null) return 0;

        var gate = element.Attribute("IsVisible")?.Value.Trim();
        var gated = gate is not null && gate.StartsWith('{');

        var nestedGated = 0;
        foreach (var child in element.Elements())
            nestedGated += Collect(child, element, rootContent, file, found);

        if (!gated) return nestedGated;

        var total = element.DescendantsAndSelf().Count();
        found.Add(new GatedSurface(
            file,
            element.Name.LocalName,
            NameOf(element),
            string.Join(' ', gate!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            total - nestedGated,
            ReferenceEquals(element, rootContent),
            parent is not null && ReferenceEquals(parent, rootContent),
            parent is null ? null : NameOf(parent)));

        return total;
    }

    private static string? NameOf(XElement element) =>
        element.Attribute(Xaml + "Name")?.Value ?? element.Attribute("Name")?.Value;

    private static readonly Lazy<HashSet<string>> PrewarmSourceNames =
        new(ScanPrewarmSources, isThreadSafe: true);

    /// <summary>
    ///     Every string literal in the UI's prewarm sources — the files that
    ///     implement <c>IPrewarmableSurfaces</c> or declare
    ///     <c>HiddenShellSurfaces</c>. Deliberately every literal rather than only
    ///     <c>FindControl</c> arguments: three of the lists are
    ///     <c>static readonly string[]</c> name tables (the Progress hub, the
    ///     gameplay-settings panes, the LAN modal's bodies) and a
    ///     <c>FindControl</c>-only scan would read every one of those as cold.
    /// </summary>
    private static HashSet<string> PrewarmedNames() => PrewarmSourceNames.Value;

    private static HashSet<string> ScanPrewarmSources()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in UiSourceTree.GetFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.cs", SearchOption.AllDirectories))
        {
            var source = UiSourceTree.ReadAllText(path);
            if (!source.Contains("IPrewarmableSurfaces", StringComparison.Ordinal) &&
                !source.Contains("HiddenShellSurfaces", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, "\"([A-Za-z][A-Za-z0-9_]{3,})\""))
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }
}
