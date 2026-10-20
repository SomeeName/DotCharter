using System.Reflection;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the wiring of the transport-bar popover prewarm — the half of
///     "opening a dropdown lags on first open" that <see cref="MenuFlyoutPrewarmer" />
///     did not reach until 0.54, because a <c>Button.Flyout</c> is not a
///     <c>MenuItem</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Structural, never timed.</b> The measured win is in
///         <c>Perf/FlyoutPrewarmUiPerfSmokeTests</c> (headless, r2r=no): the six
///         toolbar popovers cost 297-345 ms of first opens cold and 45-48 ms
///         prewarmed. What is asserted here is what breaks SILENTLY — a popover
///         declared as a bare <c>Flyout</c> is invisible to the prewarm, and a
///         shell that stops asking for the drain is a no-op nobody notices.
///     </para>
///     <para>
///         The behavioural half cannot live in the default gate: building a
///         flyout's popup needs a platform and a dispatcher. Same split as
///         <see cref="MenuFlyoutPrewarmerTests" />.
///     </para>
/// </remarks>
public sealed class PrewarmableFlyoutTests
{
    /// <summary>
    ///     The prewarm builds the popup by asking the flyout to show and
    ///     CANCELLING it. Lose the cancel and every prewarm turn flashes a popover
    ///     onto the screen and steals focus — which is the exact opposite of what
    ///     this is for, and it would only show up on a real display.
    /// </summary>
    [Fact]
    public void The_flyout_cancels_the_open_it_uses_to_build_its_popup()
    {
        var onOpening = typeof(PrewarmableFlyout).GetMethod(
            "OnOpening", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(onOpening is { DeclaringType: not null } && onOpening.DeclaringType == typeof(PrewarmableFlyout),
            "PrewarmableFlyout no longer overrides OnOpening — it can no longer refuse the open it " +
            "starts, so the prewarm would really show every popover");

        var source = Source("DotCharter.UI", "Views", "Shell", "PrewarmableFlyout.cs");

        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("_suppressOpen = true", source, StringComparison.Ordinal);

        // The suppression must be armed BEFORE the show and disarmed after, or a
        // real user click would be cancelled too.
        var arm = source.IndexOf("_suppressOpen = true", StringComparison.Ordinal);
        var show = source.IndexOf("ShowAt(target)", StringComparison.Ordinal);
        var disarm = source.IndexOf("_suppressOpen = false", StringComparison.Ordinal);
        Assert.True(arm > 0 && show > arm && disarm > show,
            "PrewarmableFlyout no longer arms the cancel around its own ShowAt — either the prewarm " +
            "really opens popovers, or the user's clicks are refused");
    }

    /// <summary>
    ///     <b>Every popover on the two chrome rails must be a
    ///     <see cref="PrewarmableFlyout" />.</b> A bare <c>&lt;Flyout&gt;</c> is
    ///     not reachable by <c>FlyoutsUnder</c>, so it is silently skipped and
    ///     goes on paying its whole realization on the user's first click — with
    ///     no warning, no exception and every other test still green.
    /// </summary>
    [Theory]
    // Mix · Tempo · Tools · Tracks · Undo · Redo · Overflow (0.59, P2-16)
    [InlineData("ToolbarView.axaml", 7)]
    // Session log · profile (0.59: the ⚠/⛔ chips became lint-count buttons
    // that jump to the Issues tab — the recent-messages flyout moved to the
    // one neutral session-log chip)
    [InlineData("StatusBar.axaml", 2)]
    public void Every_popover_on_the_chrome_rails_is_prewarmable(string file, int expected)
    {
        var markup = Source("DotCharter.UI", "Views", "Shell", file);

        Assert.DoesNotContain("<Flyout ", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Flyout>", markup, StringComparison.Ordinal);

        var declared = Occurrences(markup, "<shell:PrewarmableFlyout");
        Assert.True(declared == expected,
            $"{file} declares {declared} prewarmable popovers, expected {expected} — a new one was " +
            "added without the prewarm, or one was removed and this count is stale");
    }

    /// <summary>
    ///     <b>Every remaining bare <c>&lt;Flyout&gt;</c> in the app is accounted
    ///     for.</b> The two chrome rails are covered above; this is the rest of
    ///     the codebase, and it exists because "there are N unconverted flyouts"
    ///     is a number that rots the moment somebody adds one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Counted 2026-09-24: <b>11</b> bare <c>&lt;Flyout&gt;</c> elements
    ///         across <c>DotCharter.UI/Views</c> (10 the day before, until Song Select's
    ///         redesign took the Library's header template; 16 on the morning of 2026-09-23, before the song browser's
    ///         seven went with it and the Library's one templated funnel stayed; 15 on
    ///         2026-09-22; 16 on 2026-08-29 — re-count, never carry forward), against 18
    ///         <c>&lt;shell:PrewarmableFlyout&gt;</c>.
    ///         <b>None of the 11 is a
    ///         conversion worth making, and eleven of them would be actively
    ///         wrong to convert</b>, which is what this table records.
    ///     </para>
    ///     <para>
    ///         The eleven are the column FACET FUNNELS in the song browser and
    ///         Play Mode's song select. Every one carries
    ///         <c>Opening="OnFacetFlyoutOpening"</c>, and that handler is what
    ///         BUILDS the facet list from the loaded library.
    ///         <see cref="PrewarmableFlyout" /> works by calling its own
    ///         <c>ShowAt</c> and cancelling in <c>OnOpening</c> — Avalonia raises
    ///         the view's <c>Opening</c> handler on that path, so a prewarm would
    ///         run the facet build at BOOT, against a library the PSARC scan has
    ///         not finished loading (7.9 s / 2,160 songs cold). What these
    ///         popovers actually pay for is their DATA, which the rendering-perf
    ///         guide already says not to chase with prewarm.
    ///     </para>
    ///     <para>
    ///         The list only ever SHRINKS by conversion, never grows by excuse: a
    ///         new bare flyout in a file that is not in the table fails here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_bare_flyout_outside_the_chrome_rails_is_accounted_for()
    {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Library/LibraryView.axaml"] =
                "The Library's column funnels: ONE templated flyout per filterable header, " +
                "its options the column's data, whose counts are computed as it opens (OnFilterOpening) — " +
                "prewarming would run that against a half-scanned library, and the cost is the DATA, not the template.",
            ["PlayMode/Redesign/SongSelectScreenView.axaml"] =
                "Song Select's redesign carries the Library's header template, and with it the same ONE " +
                "templated funnel flyout; the same reason as the Library's.",
            ["PlayMode/Screens/SongSelectView.axaml"] =
                "Five more of the same facet funnels on Song Select; the case above.",
            ["Editor/Preview3DView.axaml"] =
                "The 3D HUD's effects toggles and its missed-notes list. The 3D preview panel is " +
                "DELIBERATELY not prewarmed at all — realizing it builds the Vulkan surface, which must " +
                "stay an explicit decision (see rendering-perf/startup-and-prewarm.md) — so no mechanism " +
                "can reach a flyout inside it either.",
            ["../../DotCharter.Tone.UI/Views/PlayMode/AmpFxRigView.axaml"] =
                "The rig header's DI · re-amp popover (RigReampPanel, ABI v37). PrewarmableFlyout lives in " +
                "DotCharter.UI's shell and the rig assembly (shared with RigLab) cannot name it; the panel is " +
                "a few dozen controls behind a deliberate click on a rarely-opened control, not a hot path.",
            ["../../DotCharter.Tone.UI/Views/PlayMode/VstRackView.axaml"] =
                "The shared plugin rack's Add-VST picker, a ListBox over IVstRack.AvailablePlugins (it moved " +
                "here from the Tools popover, 2026-09-27). It sits INSIDE the Tools popover (itself a " +
                "PrewarmableFlyout), Play Mode's Amp/FX overlay and RigLab's Plugins tab; the rig assembly " +
                "cannot name PrewarmableFlyout, and its body is a data list that is empty at boot."
        };

        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var path in UiSourceTree.GetFiles(views, "*.axaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var markup = UiSourceTree.ReadAllText(path);
            var bare = Occurrences(markup, "<Flyout ") + Occurrences(markup, "<Flyout>");
            if (bare == 0) continue;

            total += bare;
            var file = Path.GetRelativePath(views, path).Replace('\\', '/');
            seen.Add(file);
            if (!reasons.ContainsKey(file)) offenders.Add($"{file} ({bare})");
        }

        Assert.True(offenders.Count == 0,
            "A bare <Flyout> lives outside MenuFlyoutPrewarmer's reach and pays its whole realization on " +
            "the user's first click:\n  " + string.Join("\n  ", offenders) +
            "\n\nDeclare it <shell:PrewarmableFlyout> if it is on a hot path, or add the file here with " +
            "the reason it is not.");

        var stale = reasons.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "This table still excuses file(s) with no bare <Flyout> left in them:\n  " +
            string.Join("\n  ", stale) + "\n\nDelete the entry — the list only ever shrinks.");

        // Pinned against the markup so the classification above cannot silently
        // drift. Re-count when you convert one.
        Assert.True(total == 11,
            $"{total} bare <Flyout> elements in DotCharter.UI/Views, expected 11. Re-read the reasons " +
            "above and update this count in the same change.");
    }

    /// <summary>
    ///     The shell has to ASK for the popover drain, and it has to ask from
    ///     inside the menu drain's callback — which is what keeps it after the
    ///     first frame AND inside the <c>DOTCHARTER_NO_SURFACE_PREWARM=1</c> A/B
    ///     switch that the whole prewarm chain hangs off.
    /// </summary>
    [Fact]
    public void The_shell_drains_the_transport_popovers_after_the_menus()
    {
        var src = MainWindowCodeBehind();

        Assert.Contains("MenuFlyoutPrewarmer.FlyoutsUnder", src, StringComparison.Ordinal);

        // The popover pass is the menu pass's continuation, which is what keeps
        // it after the first frame AND inside the DOTCHARTER_NO_SURFACE_PREWARM
        // A/B switch that the whole chain hangs off. Written as a nesting rather
        // than as source ORDER since 0.55: both now run from the chrome drain's
        // callback, ahead of the dialogs and screens.
        Assert.Contains("ScheduleMenuFlyoutPrewarm(() =>", src, StringComparison.Ordinal);
        Assert.Contains("ScheduleTransportFlyoutPrewarm(PrewarmTheRestOfTheHiddenSurfaces)",
            src, StringComparison.Ordinal);
        Assert.Contains("HiddenSubtreePrewarmer.ScheduleDrain", src, StringComparison.Ordinal);

        // Found by the names the markup declares: a rename here is a silent
        // no-op, exactly like TopLevelMenus() resolving the menu bar. Since #194
        // the rails are ContentTemplate bodies, reached through their HOSTS.
        foreach (var name in new[] { "EditorToolbarHost", "StatusBarHost" })
        {
            Assert.Contains($"\"{name}\"", src, StringComparison.Ordinal);
            Assert.Contains($"x:Name=\"{name}\"",
                MainWindowSource.ReadAllText(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     <b>A close re-parents the popup BEFORE it re-realizes it (#176).</b>
    ///     Avalonia's close takes the <c>Popup</c> out of the logical tree, and the
    ///     next <c>ShowAt</c> re-styles the whole popover inside the click — 25-90 ms
    ///     a repeat open on the real window. The close now buys that back, but only
    ///     in this ORDER: a close-time re-measure without the re-parent shipped once
    ///     and drew every dropdown UNSTYLED.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A detached subtree has no style scope, so <c>ApplyTemplate</c> and
    ///         the measure after it resolve every <c>DynamicResource</c> against
    ///         nothing and cache the FALLBACK — four of six brushes wrong on the
    ///         toolbar's popovers. The guard that shipped alongside it asserted
    ///         <c>IsMeasureValid</c>/<c>IsArrangeValid</c> and stayed green
    ///         throughout, because a view whose styles failed still arranges.
    ///     </para>
    ///     <para>
    ///         So this pins the order in the source, and the behavioural halves
    ///         need a platform: <c>Perf/PopoverReopenUiPerfSmokeTests</c> (parented
    ///         and realized after a close) and <c>Perf/PopoverRestyleUiPerfSmokeTests</c>
    ///         (the resolved brushes across a close).
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_close_re_parents_before_it_re_realizes()
    {
        var source = Source("DotCharter.UI", "Views", "Shell", "PrewarmableFlyout.cs");

        // The close schedules the restore, after the close has painted.
        var closed = BodyOf(source, "protected override void OnClosed()");
        Assert.Contains("RestoreClosedStructure", closed, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", closed, StringComparison.Ordinal);

        // …which re-parents into a LIVE scope first, and only then realizes.
        var restore = BodyOf(source, "internal void RestoreClosedStructure(Control target)");
        Assert.Contains("IsAttachedToLogicalTree", restore, StringComparison.Ordinal);
        var parent = restore.IndexOf("SetParent(target)", StringComparison.Ordinal);
        var realize = restore.IndexOf("RealizeBuiltContent(", StringComparison.Ordinal);
        Assert.True(parent >= 0, "RestoreClosedStructure no longer re-parents the popup");
        Assert.True(realize > parent,
            "RestoreClosedStructure realizes before (or without) re-parenting — that measures a DETACHED " +
            "subtree, which caches every DynamicResource fallback (the unstyled-dropdown bug)");

        // The realize must not build, show or open anything. The one ShowAt in
        // this file is BuildWithoutOpening's, which is cancelled.
        var body = BodyOf(source, "public bool RealizeBuiltContent(Size available)");
        foreach (var forbidden in new[] { "ShowAt", "IsOpen = true", ".Open()", "IsVisible" })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>…and the GUIDES may not describe the re-heat as live either.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This repo's rule is that project context lives in the guides, so an
    ///         agent reads them instead of the code — which makes a guide that
    ///         documents a REVERTED feature more dangerous than no guide at all.
    ///         Both rendering-perf files described <c>PrewarmableFlyout.Reheat</c>,
    ///         its <c>_lastTarget</c> field and the lints that supposedly pinned
    ///         it, in the present tense, for as long as the source lint above was
    ///         forbidding exactly that. The next agent to "restore the missing
    ///         re-heat" would have shipped the unstyled-dropdown bug a second time.
    ///     </para>
    ///     <para>
    ///         Cheap and exact: the source lint and this one disagree the moment
    ///         either side drifts.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("startup-and-prewarm/hidden-surface-prewarm.md")]
    [InlineData("responsiveness-budget.md")]
    public void The_rendering_perf_guides_do_not_describe_the_reverted_reheat(string guide)
    {
        var text = Source("docs", "agents", "ui", "rendering-perf", guide);

        foreach (var claim in new[] { "PrewarmableFlyout.Reheat", "_lastTarget" })
            Assert.DoesNotContain(claim, text, StringComparison.Ordinal);
    }

    /// <summary>The brace-matched body of the member whose signature is
    /// <paramref name="signature" />.</summary>
    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"PrewarmableFlyout.cs no longer declares '{signature}'.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static string Source(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));

    /// <summary>The shell code-behind is SPLIT across <c>MainWindow.axaml.cs</c>,
    /// its <c>MainWindow.*.cs</c> partials AND the <c>MainWindow*</c> collaborators
    /// extracted out of them (<c>Views/Shell/MainWindowPrewarm.cs</c> and friends) — read them all, or a lint
    /// silently stops looking at the half it cares about.</summary>
    private static string MainWindowCodeBehind() =>
        string.Join('\n', UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));
}
