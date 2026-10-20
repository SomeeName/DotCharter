using Avalonia;
using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the contract of <see cref="HiddenSubtreePrewarmer" /> — the thing that
///     moves an <c>IsVisible=false</c> subtree's first layout off the user's
///     first switch into it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Structural, never timed.</b> The win is measured by
///         <c>LocalOnly_ViewInflationProbe</c> (20-137 ms per settings tab, 2-102
///         ms per dialog, all of it down to ~0.2 ms); asserting milliseconds in
///         the gate has burned this repo repeatedly. What is asserted here is what
///         can actually break silently: that the subtree really gets realized,
///         that <c>IsVisible</c> is left alone, and that the binding which owns
///         that property is still in charge afterwards.
///     </para>
///     <para>
///         Platform-free on purpose: every control below is a bare layout
///         primitive, so no font manager, render interface or app session is
///         needed and these stay in the default gate.
///     </para>
/// </remarks>
public sealed class HiddenSubtreePrewarmerTests
{
    private static readonly Size Slot = new(400, 300);

    /// <summary>Counts its own measure passes, which is the only direct evidence
    /// that a subtree was realized rather than skipped.</summary>
    private sealed class CountingPanel : Panel
    {
        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private sealed class ExplodingPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize) =>
            throw new InvalidOperationException("measure is not possible here");
    }

    /// <summary>A hidden part with a visible child, the shape every gated surface
    /// in the app takes (a screen root / tab / dialog scrim around content).</summary>
    private static (Border Part, CountingPanel Content) HiddenPart()
    {
        var content = new CountingPanel();
        var part = new Border { Child = content, IsVisible = false };
        _ = new Decorator { Child = part };
        return (part, content);
    }

    [Fact]
    public void An_invisible_subtree_is_not_measured_by_Avalonia()
    {
        // The premise the whole helper rests on. If this ever stops holding,
        // the prewarm is pointless rather than wrong — and we want to know.
        var (part, content) = HiddenPart();

        part.Measure(Slot);

        Assert.Equal(0, content.Measures);
    }

    [Fact]
    public void Realize_measures_the_hidden_subtree()
    {
        var (part, content) = HiddenPart();

        var realized = HiddenSubtreePrewarmer.Realize(part, Slot);

        Assert.True(realized);
        Assert.True(content.Measures > 0, "the prewarm did not reach the content");
    }

    /// <summary>
    ///     #141: a part realized once and then changed DEEP while hidden (a list's
    ///     rows arriving behind a prewarmed tab). <c>Realize</c> alone cannot reach
    ///     that node — the part's direct children still hold a valid measure for
    ///     the same constraint and return at once — so the whole re-layout landed
    ///     on the click. <c>RealizeStale</c> opens the path down to it first.
    /// </summary>
    [Fact]
    public void RealizeStale_relays_out_a_deep_node_that_went_stale_while_hidden()
    {
        var deep = new CountingPanel();
        var part = new Border { Child = new StackPanel { Children = { new Border { Child = deep } } }, IsVisible = false };
        _ = new Decorator { Child = part };
        Assert.True(HiddenSubtreePrewarmer.Realize(part, Slot));
        var measured = deep.Measures;

        deep.InvalidateMeasure();
        HiddenSubtreePrewarmer.Realize(part, Slot);
        Assert.Equal(measured, deep.Measures);          // the trap: a no-op
        Assert.False(deep.IsMeasureValid);

        Assert.True(HiddenSubtreePrewarmer.RealizeStale(part, Slot) > 0);
        Assert.True(deep.Measures > measured, "RealizeStale did not reach the stale node");
        Assert.True(deep.IsMeasureValid);
        Assert.False(part.IsVisible);

        // Nothing stale, nothing done.
        Assert.Equal(0, HiddenSubtreePrewarmer.RealizeStale(part, Slot));
    }

    [Fact]
    public void Realize_leaves_the_part_hidden()
    {
        var (part, _) = HiddenPart();

        HiddenSubtreePrewarmer.Realize(part, Slot);

        Assert.False(part.IsVisible);
    }

    /// <summary>
    ///     The reason the helper measures children instead of flipping
    ///     <c>IsVisible</c>: that property is owned by a binding on every real
    ///     surface (a settings group's search visibility, a dialog's
    ///     <c>IsOpen</c>, a Play Mode screen's <c>IsMenuScreen</c>). A prewarm
    ///     that wrote to it would race the binding — and five dialog views also
    ///     grab keyboard focus the moment their scrim turns visible.
    /// </summary>
    /// <remarks>
    ///     A source lint rather than a live binding, deliberately. The obvious
    ///     test — bind <c>IsVisible</c>, prewarm, flip the source, assert it
    ///     follows — passes in isolation and FAILS in the full gate: once any
    ///     other test has booted an Avalonia session, binding subscriptions are
    ///     scheduled onto the dispatcher thread and never arrive on the xUnit
    ///     worker, so the assertion reads a property the binding has not written
    ///     yet. Pinning the write instead is order-independent and says the same
    ///     thing: the prewarmer must not touch that property at all.
    /// </remarks>
    [Fact]
    public void Realize_leaves_the_IsVisible_binding_in_charge()
    {
        var code = string.Join('\n', UiSourceTree.ReadAllLines(UiSourceTree.Resolve(Path.Combine(
                TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "HiddenSubtreePrewarmer.cs")))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        string[] writes =
        [
            "IsVisible =", "IsVisible=",
            "SetValue(Visual.IsVisibleProperty", "SetValue(IsVisibleProperty",
            "SetCurrentValue(Visual.IsVisibleProperty", "SetCurrentValue(IsVisibleProperty",
            "ClearValue(Visual.IsVisibleProperty", "ClearValue(IsVisibleProperty"
        ];

        var offenders = writes.Where(w => code.Contains(w, StringComparison.Ordinal)).ToList();

        Assert.True(offenders.Count == 0,
            "HiddenSubtreePrewarmer writes IsVisible (" + string.Join(", ", offenders) + "). It must only " +
            "READ it: on every real surface that property is owned by a binding, and five dialog views " +
            "grab keyboard focus the moment their scrim turns visible.");
    }

    /// <summary>The realization must survive its part being shown and hidden by
    /// the binding afterwards — that is the whole point, one prewarm buying every
    /// later switch.</summary>
    [Fact]
    public void A_realized_subtree_stays_realized_across_hide_and_show()
    {
        var (part, content) = HiddenPart();
        HiddenSubtreePrewarmer.Realize(part, Slot);
        var afterPrewarm = content.Measures;
        Assert.True(afterPrewarm > 0);

        part.IsVisible = true;
        part.Measure(Slot);
        part.IsVisible = false;
        part.Measure(Slot);
        part.IsVisible = true;
        part.Measure(Slot);

        Assert.Equal(afterPrewarm, content.Measures);
    }

    [Fact]
    public void Realize_skips_a_part_that_is_already_visible()
    {
        var content = new CountingPanel();
        var part = new Border { Child = content, IsVisible = true };
        _ = new Decorator { Child = part };

        Assert.False(HiddenSubtreePrewarmer.Realize(part, Slot));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(400, 0)]
    [InlineData(double.NaN, 300)]
    [InlineData(double.PositiveInfinity, 300)]
    public void Realize_refuses_an_unusable_constraint(double width, double height)
    {
        var (part, content) = HiddenPart();

        Assert.False(HiddenSubtreePrewarmer.Realize(part, new Size(width, height)));
        Assert.Equal(0, content.Measures);
    }

    /// <summary>A view that cannot measure in isolation must cost the prewarm
    /// nothing but its own turn — never the window.</summary>
    [Fact]
    public void Realize_swallows_a_measure_failure()
    {
        var part = new Border { Child = new ExplodingPanel(), IsVisible = false };
        _ = new Decorator { Child = part };

        Assert.False(HiddenSubtreePrewarmer.Realize(part, Slot));
        Assert.False(part.IsVisible);
    }

    [Fact]
    public void SlotSizeOf_uses_the_parent_slot_minus_the_part_margin()
    {
        var part = new Border { IsVisible = false, Margin = new Thickness(10, 5, 20, 15) };
        var parent = new Grid { Children = { part } };
        parent.Measure(new Size(500, 400));
        parent.Arrange(new Rect(0, 0, 500, 400));

        var slot = HiddenSubtreePrewarmer.SlotSizeOf(part, new Size(1920, 1080));

        Assert.Equal(470, slot.Width, 3);
        Assert.Equal(380, slot.Height, 3);
    }

    [Fact]
    public void SlotSizeOf_falls_back_when_the_parent_has_not_been_arranged()
    {
        var part = new Border { IsVisible = false };
        _ = new Grid { Children = { part } };

        Assert.Equal(new Size(1920, 1080), HiddenSubtreePrewarmer.SlotSizeOf(part, new Size(1920, 1080)));
    }

    [Fact]
    public void HiddenChildrenOf_returns_only_the_hidden_children_in_order()
    {
        var a = new Border { IsVisible = false, Name = "a" };
        var b = new Border { IsVisible = true, Name = "b" };
        var c = new Border { IsVisible = false, Name = "c" };
        var host = new Grid { Children = { a, b, c } };

        Assert.Equal(["a", "c"], HiddenSubtreePrewarmer.HiddenChildrenOf(host).Select(x => x.Name));
        Assert.Empty(HiddenSubtreePrewarmer.HiddenChildrenOf(null));
    }

    [Fact]
    public void HiddenContentOf_returns_a_closed_dialog_scrim_and_nothing_for_an_open_one()
    {
        var closed = new ContentControl { Content = new Border { IsVisible = false } };
        var open = new ContentControl { Content = new Border { IsVisible = true } };

        Assert.Single(HiddenSubtreePrewarmer.HiddenContentOf(closed));
        Assert.Empty(HiddenSubtreePrewarmer.HiddenContentOf(open));
        Assert.Empty(HiddenSubtreePrewarmer.HiddenContentOf(null));
    }

    // ---- reachability ------------------------------------------------------

    /// <summary>
    ///     The prewarm has TWO exit paths (boot into the editor, and boot into
    ///     any other tab). A drain scheduled on only one of them leaves half the
    ///     users paying the first-switch cost, and nothing else would notice.
    /// </summary>
    [Fact]
    public void Both_prewarm_exit_paths_schedule_the_hidden_surface_drain()
    {
        var src = MainWindowCodeBehind();

        var calls = src.Split("SchedulePrewarmOfHiddenSurfaces()").Length - 1;

        // One declaration + one call per exit path.
        Assert.True(calls >= 3,
            $"MainWindow schedules the hidden-surface prewarm from {calls - 1} site(s); both " +
            "PrewarmTopLevelViews exit paths must call it.");
    }

    /// <summary>
    ///     The shell's OWN gated surfaces — the chart editor above all — are
    ///     declared straight in <c>MainWindow.axaml</c>, so there is no view to
    ///     hang <c>IPrewarmableSurfaces</c> on and nothing else notices if they
    ///     drop out of the drain. The editor measured 130 ms of first-switch
    ///     layout before the shell listed it and 37 ms after, against a &lt;20 ms
    ///     target and a 150 ms budget. A Play Mode boot puts HOME in front of it:
    ///     the Start screen leads to Home, not to the editor.
    /// </summary>
    [Fact]
    public void The_shell_prewarms_its_own_hidden_surfaces_starting_with_the_chart_editor()
    {
        var src = MainWindowCodeBehind();

        var scheduler = BodyOf(src, "private void SchedulePrewarmOfHiddenSurfaces()");
        Assert.Contains("HiddenShellSurfaces().ToList()", scheduler, StringComparison.Ordinal);

        // …behind HOME and its destinations only on a Play Mode boot (#234): the
        // Start screen leads to Home, not to the editor, whose chrome is not even
        // on screen. Any other boot drains the editor first.
        var home = scheduler.IndexOf("HomeAndItsDestinations(", StringComparison.Ordinal);
        Assert.True(home >= 0 && home < scheduler.IndexOf("HiddenShellSurfaces()", StringComparison.Ordinal),
            "a Play Mode boot no longer realizes Home ahead of the shell chrome");
        Assert.Contains("IsPlayModeVisible: true", scheduler[..home], StringComparison.Ordinal);
        var destinations = BodyOf(src, "private IEnumerable<Control?> HomeAndItsDestinations(");
        Assert.Contains("EnsureHomeCreated();", destinations[..destinations.IndexOf("yield", StringComparison.Ordinal)],
            StringComparison.Ordinal);
        // Song Select, Home's first destination, is a DEFERRED body (#648): the
        // drain builds it and realizes its gated HOST. Its root carries no gate,
        // so a lookup by the root's name finds a VISIBLE part and realizes nothing
        // — which is what the lookup did, silently, from #294 to #648.
        Assert.Contains("BuildDeferredScreenBody(PlayModeView.SongSelectHostName)", destinations, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SongSelectScreenRoot\"", destinations, StringComparison.Ordinal);

        var body = BodyOf(src, "private IEnumerable<Control> HiddenShellSurfaces()");
        Assert.Contains("MainSplitGrid", body, StringComparison.Ordinal);

        // The editor root must come FIRST: the drain runs one part per
        // Background turn, so ordering IS the "which switch is fast" decision.
        var names = LookedUpNames(body);
        Assert.Equal("MainSplitGrid", names[0]);

        // FindControl resolves by name, so a rename in the markup turns every
        // one of these into a silent no-op with no build error.
        var xaml = MainWindowSource.ReadAllText();
        foreach (var name in names)
        {
            Assert.True(xaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal),
                $"HiddenShellSurfaces() prewarms '{name}', but neither MainWindow.axaml nor WindowChromeView.axaml declares that x:Name — " +
                "the lookup silently returns null and that surface pays its first layout on the user's switch.");
        }
    }

    /// <summary>
    ///     <b>The editor chrome's DROPDOWNS drain before the dialogs, the settings
    ///     tabs and the Play Mode screens — and that order is a fix, not a
    ///     preference.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The drain pays one surface per <c>Background</c> dispatcher turn,
    ///         and <c>Background</c> is below <c>Normal</c> — so the boot-time
    ///         PSARC library scan, whose <c>IProgress</c> callbacks marshal every
    ///         found batch onto the UI thread, sits in front of every step of it
    ///         for as long as it runs (7.9 s / 2,160 songs, cold). Until 0.55 the
    ///         menu dropdowns and the two rails' popovers were drained LAST, behind
    ///         ~70 surfaces of that queue, and a user who clicked Tools or Mix in
    ///         the first seconds of a session still paid the full cold open —
    ///         46-175 ms per popover and 8-88 ms per menu, headless, <c>r2r=no</c>.
    ///         They are also the only surfaces in the whole list that are ONE CLICK
    ///         from the window the instant it appears.
    ///     </para>
    ///     <para>
    ///         Source-level because the ordering IS the behaviour and nothing else
    ///         observes it: put the owners' surfaces back in front and every test
    ///         in this file still passes while the dropdowns go cold again.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheChromesDropdowns_DrainBeforeTheDialogsAndScreens()
    {
        var src = MainWindowCodeBehind();

        var scheduler = BodyOf(src, "private void SchedulePrewarmOfHiddenSurfaces()");

        // The first block is the chrome, and the dropdowns are chained straight
        // off it.
        Assert.Contains("ScheduleMenuFlyoutPrewarm(", scheduler, StringComparison.Ordinal);
        Assert.Contains("ScheduleTransportFlyoutPrewarm(", scheduler, StringComparison.Ordinal);

        // …and NOTHING else is listed there. DialogContainerView.HiddenSurfaces()
        // materializes ~38 dialog views just to be listed, so even building the
        // list ahead of the dropdowns would put that construction in front of
        // them.
        Assert.DoesNotContain("PrewarmableSurfaceOwners()", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsurePlayModeViewCreated", scheduler, StringComparison.Ordinal);

        // The rest runs from the popover drain's callback, so it can only start
        // once both dropdown passes are done.
        Assert.Contains("ScheduleTransportFlyoutPrewarm(PrewarmTheRestOfTheHiddenSurfaces)",
            scheduler, StringComparison.Ordinal);

        var rest = BodyOf(src, "private void PrewarmTheRestOfTheHiddenSurfaces()");
        Assert.Contains("PrewarmableSurfaceOwners()", rest, StringComparison.Ordinal);
        Assert.Contains("HiddenSubtreePrewarmer.ScheduleDrain", rest, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The rest of the drain is PRODUCED inside it, one paced turn per build and
    ///     per listing (#234). Built up front, the list was one 0.7-1.9 s turn on the
    ///     Release R2R build (Play Mode's deferred bodies and the rig view, 34 dialog
    ///     views, the three top-level views) that no click could interrupt.
    ///     <para>
    ///         And the owners are listed AFTER the hosts have been realized: a
    ///         <c>ScreenTabHost</c> lists nothing until a layout pass has attached it
    ///         under its <c>ScreenHost</c>, so the up-front list silently left every
    ///         settings tab but the first one cold.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRestOfTheDrain_IsProducedInsideIt_OneTurnPerBuildAndPerListing()
    {
        var src = MainWindowCodeBehind();

        var rest = BodyOf(src, "private void PrewarmTheRestOfTheHiddenSurfaces()");
        Assert.Contains("ScheduleDrain(TheRestOfTheHiddenSurfaces(", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<Control>", rest, StringComparison.Ordinal);

        var producer = BodyOf(src, "private IEnumerable<Control?> TheRestOfTheHiddenSurfaces(");
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"ConstructionSteps\(\)\)\s*\{\s*Try\(owner, build\);\s*yield return null;"), producer);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"listed = owner\.HiddenSurfaces\(\)\);\s*yield return null;"), producer);

        var hosts = producer.IndexOf("yield return host;", StringComparison.Ordinal);
        Assert.True(hosts >= 0 && producer.IndexOf("listOwners()", StringComparison.Ordinal) > hosts,
            "the owners are listed before the hosts are realized — an unattached ScreenTabHost lists nothing");
    }

    /// <summary>
    ///     The two owners whose listing BUILDS views hand that building to the drain
    ///     as steps of its own (<see cref="IPrewarmConstruction" />); without it the
    ///     whole build is one turn again, inside <c>HiddenSurfaces()</c>.
    /// </summary>
    [Theory]
    [InlineData(typeof(DotCharter.UI.Views.PlayMode.PlayModeView))]
    [InlineData(typeof(DialogContainerView))]
    public void AnOwnerThatBuildsToList_HandsTheBuildingToTheDrain(Type owner) =>
        Assert.True(typeof(IPrewarmConstruction).IsAssignableFrom(owner),
            $"{owner.Name} builds views inside HiddenSurfaces() but no longer offers that work as IPrewarmConstruction steps.");

    /// <summary>The names <c>HiddenShellSurfaces</c> hands to
    /// <c>FindControl</c>, in declaration order.</summary>
    /// <summary>Every name the body looks up, in source order: by
    /// <c>FindControl</c> in the window's namescope, or as a member of
    /// <c>WindowChrome</c>, whose rows are in the chrome view's own (#654).</summary>
    private static List<string> LookedUpNames(string body) =>
        [.. System.Text.RegularExpressions.Regex.Matches(body, @"FindControl<[^>]+>\(""([^""]+)""\)|WindowChrome\.(\w+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)];

    /// <summary>The brace-matched body of the method whose signature is
    /// <paramref name="signature" />.</summary>
    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The MainWindow code-behind no longer declares '{signature}'.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    /// <summary>
    ///     Every implementor must be reachable from the shell, or it prewarms
    ///     nothing at all — a silent no-op that reads as "the fix did not work".
    /// </summary>
    /// <remarks>
    ///     Counted, not name-matched. Only ONE of the four owner branches names
    ///     its view type (<c>DialogContainerView</c>); the other three resolve
    ///     through a deferred host's content and mention no type at all, so a
    ///     name check would have to be satisfied by "the method mentions
    ///     IPrewarmableSurfaces somewhere" — which its own signature guarantees.
    ///     That is exactly how this guard passed while
    ///     <c>SongBrowserView</c> implemented nothing and the
    ///     <c>SongBrowserHost</c> branch matched no child: three owners, four
    ///     branches, no failure anywhere. A branch per implementor is the
    ///     smallest claim that is actually checkable, and it fails in BOTH
    ///     directions — a new implementor with no branch, and a branch that can
    ///     never resolve.
    /// </remarks>
    [Fact]
    public void Every_prewarmable_view_is_reachable_from_the_shell()
    {
        var implementors = typeof(IPrewarmableSurfaces).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IPrewarmableSurfaces).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(implementors);

        var src = MainWindowCodeBehind();
        var owners = BodyOf(src, "private IEnumerable<IPrewarmableSurfaces> PrewarmableSurfaceOwners()");
        var branches = owners.Split("yield return").Length - 1;

        Assert.True(branches == implementors.Count,
            $"PrewarmableSurfaceOwners() yields {branches} owner(s) but {implementors.Count} view(s) implement " +
            $"IPrewarmableSurfaces ({string.Join(", ", implementors)}). An implementor with no branch is never " +
            "asked for its surfaces; a branch with no implementor resolves nothing and is dead code. Both are " +
            "silent — the surfaces just keep paying their first layout on the user's gesture.");
    }

    // ---- the two deferred hosts' park / unpark -----------------------------

    /// <summary>
    ///     <b>An unparked host is opaque again whether it is being SHOWN or
    ///     HIDDEN.</b> <c>MainWindow.PrewarmTopLevelViews</c> is the one prewarm
    ///     that really flips <c>IsVisible</c> — the song browser and the settings
    ///     page are realized by being held visible at <c>Opacity 0</c> so their
    ///     templates run without painting over the editor's native 3D HWND. A host
    ///     left at zero opacity is not "hidden": it is a fully laid-out surface
    ///     that paints nothing, so the next switch into it shows a BLANK window
    ///     with no error and no log line.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnparkHost_restores_opacity_and_hit_testing_either_way(bool visible)
    {
        var host = new Border();
        MainWindowPrewarm.ParkHost(host);

        Assert.Equal(0, host.Opacity);
        Assert.False(host.IsHitTestVisible);
        Assert.True(host.IsVisible, "a parked host must be visible or Avalonia skips MeasureCore and the " +
                                    "whole realize pass does nothing");

        MainWindowPrewarm.UnparkHost(host, visible);

        Assert.Equal(1, host.Opacity);
        Assert.True(host.IsHitTestVisible);
        Assert.Equal(visible, host.IsVisible);
    }

    /// <summary>A host that is not in the markup must cost the prewarm nothing —
    /// <c>FindControl</c> returns null the moment a name changes.</summary>
    [Fact]
    public void ParkHost_and_UnparkHost_ignore_a_missing_host()
    {
        MainWindowPrewarm.ParkHost(null);
        MainWindowPrewarm.UnparkHost(null, true);
    }

    /// <summary>
    ///     <b>The visibility subscription must be live BEFORE the hosts are
    ///     parked.</b> The revert that unparks them is a second
    ///     <c>Background</c> post, and <c>Background</c> sits under the boot-time
    ///     PSARC library scan's <c>Normal</c>-priority <c>IProgress</c> callbacks
    ///     — measured 7.9 s / 2,160 songs, cold. A user who clicks LIBRARY or
    ///     SETTINGS inside that window switches to a host that is visible,
    ///     transparent and hit-test disabled; with the subscription live, that
    ///     switch unparks the host itself.
    /// </summary>
    /// <remarks>
    ///     Source-level because the race needs a real dispatcher, a real library
    ///     scan and a real click to reproduce — and because the failure is a blank
    ///     window rather than an exception, nothing else would ever notice the
    ///     order flipping back.
    /// </remarks>
    [Fact]
    public void TheHostsAreWired_BeforeTheyAreParkedForTheRealizePass()
    {
        var body = BodyOf(MainWindowCodeBehind(), "internal void PrewarmTopLevelViews()");

        // LAST, not first: PrewarmTopLevelViews has TWO exit paths and the
        // boot-into-another-tab one binds and returns before any of this. A
        // first-occurrence search is satisfied by THAT call whatever the
        // realize path does — the guard was written that way and proved
        // vacuous against a deliberate re-ordering.
        var bind = body.LastIndexOf("BindHostVisibility();", StringComparison.Ordinal);
        var park = body.IndexOf("ParkHost(", StringComparison.Ordinal);
        var unpark = body.IndexOf("UnparkHost(", StringComparison.Ordinal);

        Assert.True(bind >= 0 && park >= 0 && unpark >= 0,
            "PrewarmTopLevelViews no longer binds, parks and unparks the two deferred hosts.");
        Assert.True(bind < park,
            "PrewarmTopLevelViews parks the hosts before wiring their visibility, so a switch during the " +
            "prewarm window lands on a transparent, hit-test-disabled host — a blank window until the " +
            "Background revert drains.");

        // …and the subscription's own branches must unpark, not just re-write
        // IsVisible: the parked host is invisible because of its OPACITY.
        Assert.Contains("UnparkHost(songBrowserHost, vm.IsSongBrowserVisible)", body, StringComparison.Ordinal);
        Assert.Contains("UnparkHost(settingsHost, vm.IsSettingsVisible)", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The four nested-body name tables are resolved by <c>x:Name</c> through
    ///     <c>HiddenSubtreePrewarmer.HiddenNamedDescendants</c>, which SKIPS a
    ///     name it cannot find — so a renamed or deleted body silently goes cold,
    ///     and an entry that is not <c>IsVisible</c>-gated is a lookup that can
    ///     never return anything. Each name must be declared exactly once under
    ///     <c>Views/</c>, on a gated element. (<c>PrewarmCoverageTests</c> is the
    ///     other half: it fails when a 35+ element body is in NO list.)
    /// </summary>
    [Theory]
    [InlineData(typeof(DotCharter.UI.Views.PlayMode.PlayModeView), "NestedScreenBodyNames")]
    [InlineData(typeof(DialogContainerView), "NestedDialogBodyNames")]
    [InlineData(typeof(DotCharter.UI.Views.Editor.InspectorView), "NestedPaneNames")]
    public void Every_nested_body_name_is_declared_once_on_a_gated_element(System.Type owner, string table)
    {
        var field = owner.GetField(table,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        Assert.True(field != null, $"{owner.Name}.{table} is gone — its bodies are cold and this guard is blind.");
        var names = (string[])field!.GetValue(null)!;
        Assert.NotEmpty(names);

        var markup = UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "*.axaml", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetFileName(f), Doc: System.Xml.Linq.XDocument.Load(f)))
            .ToList();
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var name in names)
        {
            var hits = markup.SelectMany(m => m.Doc.Descendants()
                    .Where(e => (string?)e.Attribute(x + "Name") == name)
                    .Select(e => (m.File, Element: e)))
                .ToList();
            Assert.True(hits.Count == 1,
                $"{owner.Name}.{table} prewarms '{name}', but the markup declares that x:Name {hits.Count} " +
                "time(s) — the lookup is by name and silently finds nothing (or the wrong thing).");
            Assert.True(hits[0].Element.Attribute("IsVisible")?.Value.StartsWith('{') == true,
                $"'{name}' ({hits[0].File}) is in {owner.Name}.{table} but is not IsVisible-gated, so it is " +
                "never hidden and never needs prewarming — the entry is dead.");
        }
    }

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

    /// <summary>
    ///     The chart editor's Amp &amp; FX rig overlay must be in the shell's
    ///     prewarm list.
    ///     <para>
    ///         It is a SECOND <c>AmpFxRigView</c> instance — Play Mode's own copy
    ///         is realized through <c>PlayModeView.HiddenSurfaces()</c>, and
    ///         prewarming one view does nothing for another instance of it: what
    ///         costs is the first LAYOUT of that subtree, not the type's first
    ///         use. The rig is the largest surface either host shows, so its
    ///         first open is the one the owner would feel.
    ///     </para>
    ///     <para>
    ///         A source lint, not a layout test, for the reason the sibling sweep
    ///         cannot reach it either: <c>HiddenShellSurfaces()</c> names its
    ///         surfaces one by one, so a surface that is simply never named is
    ///         invisible to every runtime assertion — it just quietly pays its
    ///         realization on the user's click, forever.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheEditorsRigOverlay_IsNamedInTheShellPrewarmList()
    {
        var codeBehind = MainWindowCodeBehind();
        var markup = MainWindowSource.ReadAllText();

        // The name has to exist on both sides: a FindControl for a name the
        // markup does not carry returns null and yields nothing, silently.
        Assert.Contains("x:Name=\"PedalboardOverlay\"", markup, StringComparison.Ordinal);

        // The DEFINITION, not the call site — the call is ~60 lines earlier. It
        // used to be a fixed 1,800-character window from the signature, which is
        // a guard that goes blind the moment the method grows past it; the
        // brace-matched body cannot.
        var body = BodyOf(codeBehind, "private IEnumerable<Control> HiddenShellSurfaces()");
        Assert.Contains("PedalboardOverlay", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Sheet Music dock is a gated CHILD of the chart editor, and the
    ///     prewarmer deliberately does not recurse into nested hidden subtrees —
    ///     so realizing <c>MainSplitGrid</c> stops at its edge and the dock (a
    ///     whole second <c>DockControl</c> plus the AlphaTab notation strip) stays
    ///     cold until the first View-menu toggle.
    ///     <para>
    ///         It must come AFTER the editor in the list: a child measured against
    ///         a slot its parent has never arranged re-measures on the real show,
    ///         which is the same ordering rule <c>PrewarmableSurfaceOwners()</c>
    ///         follows for the settings host and the two owners nested inside it.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheSheetMusicDock_IsPrewarmedAfterTheEditorThatContainsIt()
    {
        var src = MainWindowCodeBehind();
        var names = LookedUpNames(BodyOf(src, "private IEnumerable<Control> HiddenShellSurfaces()"));

        var editor = names.IndexOf("MainSplitGrid");
        var sheet = names.IndexOf("SheetMusicDock");

        Assert.True(sheet >= 0,
            "the Sheet Music dock is not in HiddenShellSurfaces(); it is gated on IsSheetMusicVisible inside " +
            "MainSplitGrid, and the prewarmer does not recurse into nested hidden subtrees.");
        Assert.True(sheet > editor,
            "SheetMusicDock is prewarmed BEFORE MainSplitGrid, so it measures against a slot that has never " +
            "been arranged and re-measures on the real show. Parent before child.");
    }

    /// <summary>
    ///     The command palette is the odd one out: the view itself is always
    ///     visible and the <c>IsVisible="{Binding IsOpen}"</c> sits on its own
    ///     root Grid, in a namescope <c>FindControl</c> cannot cross. So it has to
    ///     be named in <c>MainWindow.axaml</c> and handed through
    ///     <see cref="HiddenSubtreePrewarmer.HiddenContentOf" /> — passing the
    ///     view straight to <c>Realize</c> would hit the "already visible" early
    ///     return and prewarm nothing at all, silently.
    /// </summary>
    [Fact]
    public void TheCommandPalette_IsPrewarmedThroughItsHiddenContent()
    {
        var src = MainWindowCodeBehind();
        var body = BodyOf(src, "private IEnumerable<Control> HiddenShellSurfaces()");

        Assert.Contains("CommandPaletteOverlay", body, StringComparison.Ordinal);
        Assert.Contains("HiddenSubtreePrewarmer.HiddenContentOf", body, StringComparison.Ordinal);

        // The IsVisible=false check every other entry uses would be wrong here —
        // and would quietly yield nothing, because the view is always visible.
        var palette = body[body.IndexOf("CommandPaletteOverlay", StringComparison.Ordinal)..];
        Assert.DoesNotContain("IsVisible: false", palette[..Math.Min(palette.Length, 120)],
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The song browser's own gated surfaces. The shell has always had a
    ///     <c>SongBrowserHost</c> branch looking for an
    ///     <see cref="IPrewarmableSurfaces" /> child; until 0.53 the view
    ///     implemented none, so the branch matched nothing and its hidden
    ///     states were reached by no mechanism at all. (The Library Doctor it
    ///     used to host is a dialog on the dialog host now, prewarmed there.)
    /// </summary>
    [Fact]
    public void TheLibrary_OffersItsGatedSurfaces_IncludingItsEmptyStates()
    {
        var view = typeof(IPrewarmableSurfaces).Assembly.GetTypes()
            .SingleOrDefault(t => t.Name == "LibraryView");
        Assert.True(view != null, "LibraryView is gone — this guard is blind");
        Assert.True(typeof(IPrewarmableSurfaces).IsAssignableFrom(view!),
            "LibraryView does not implement IPrewarmableSurfaces, so MainWindow's SongBrowserHost branch " +
            "matches nothing and is dead code.");

        var codeBehind = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Library", "LibraryView.axaml.cs")));
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Library", "LibraryView.axaml")));

        // FindControl is by name, so each lookup needs its x:Name in the markup
        // or it returns null and that surface silently stays cold.
        foreach (var name in LookedUpNames(BodyOf(codeBehind, "public IReadOnlyList<Control> HiddenSurfaces()")))
        {
            Assert.True(markup.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal),
                $"LibraryView.HiddenSurfaces() prewarms '{name}', but the markup declares no such x:Name.");
        }

        Assert.Contains("LibraryNoMatch", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LibraryDetailBody", codeBehind, StringComparison.Ordinal);
    }
}
