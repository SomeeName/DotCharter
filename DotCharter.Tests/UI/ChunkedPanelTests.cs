using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the chunked prewarm (#253): a long hidden list or stack of heavy groups
///     is laid out a CHUNK per drain turn (<see cref="IChunkedPanel" />,
///     <see cref="HiddenSubtreePrewarmer.RealizeNextChunk" />) — the rig's tone
///     cards, the Keybinds and Appearance settings groups, the inspector's Mixer
///     sections — and each rig instance's tone browser is listed exactly once.
/// </summary>
/// <remarks>
///     Structural, never timed, and platform-free like
///     <see cref="HiddenSubtreePrewarmerTests" />: bare layout primitives, no font
///     manager or window. A detached tree never counts as SHOWN, so the
///     show-time half (the limit lifting on the first show) is pinned headless, in
///     <c>Perf/ToneBrowserChunkedPrewarmUiPerfSmokeTests</c>.
/// </remarks>
public sealed class ChunkedPanelTests
{
    private static readonly Size Slot = new(400, 300);

    /// <summary>A card that counts its own measure passes — the evidence that the
    /// real layout found its prewarmed measure valid instead of redoing it.</summary>
    private sealed class Card : Border
    {
        public Card()
        {
            Width = 90;
            Height = 40;
        }

        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    /// <summary>A hidden part around a chunked panel of <paramref name="count" />
    /// fixed-size cards in a vertical stack (an unbounded height, as the browser's
    /// scroller gives it) — the tone browser's shape.</summary>
    private static (Border Part, ChunkedWrapPanel Panel) HiddenCards(int count, int chunk)
    {
        var panel = new ChunkedWrapPanel { ChunkSize = chunk };
        for (var i = 0; i < count; i++)
            panel.Children.Add(new Card());
        var part = new Border { Child = new StackPanel { Children = { panel } }, IsVisible = false };
        _ = new Decorator { Child = part };
        return (part, panel);
    }

    private static int Measured(Panel panel) => panel.Children.Count(c => c.IsMeasureValid);

    [Fact]
    public void AHiddenPanel_LaysOutOneChunk_AndEveryTurnAfterOneMore()
    {
        var (part, panel) = HiddenCards(count: 10, chunk: 3);

        Assert.True(HiddenSubtreePrewarmer.Realize(part, Slot));
        Assert.Equal(3, Measured(panel));

        var perTurn = new List<int>();
        while (HiddenSubtreePrewarmer.RealizeNextChunk(part, Slot))
            perTurn.Add(Measured(panel));

        // 3 → 6 → 9 → 10: one chunk a turn, never the rest of the list at once.
        Assert.Equal([6, 9, 10], perTurn);
        Assert.False(panel.HasBacklog);
        Assert.False(part.IsVisible);
    }

    [Fact]
    public void AHiddenPanel_ReportsTheWholeListsSize_NotOneChunks()
    {
        var (part, panel) = HiddenCards(count: 10, chunk: 3);
        HiddenSubtreePrewarmer.Realize(part, Slot);

        var plain = new WrapPanel();
        for (var i = 0; i < 10; i++)
            plain.Children.Add(new Card());
        plain.Measure(new Size(Slot.Width, double.PositiveInfinity));

        // The scroller around the browser sizes its bar from this extent; one
        // chunk's worth would leave the bar's page buttons for the first show to build.
        Assert.Equal(plain.DesiredSize, panel.DesiredSize);
        Assert.Equal(3, Measured(panel));
    }

    [Fact]
    public void TheFirstShow_PlacesThePrewarmedCards_WithoutMeasuringThemAgain()
    {
        var (part, panel) = HiddenCards(count: 7, chunk: 2);
        HiddenSubtreePrewarmer.Realize(part, Slot);
        while (HiddenSubtreePrewarmer.RealizeNextChunk(part, Slot)) { }
        Assert.All(panel.Children.Cast<Card>(), c => Assert.Equal(1, c.Measures));

        // The show: the part becomes visible and the panel lays out as a plain
        // WrapPanel (the shown-state invalidation is the watcher's job, pinned
        // headless; done by hand here because a detached tree is never "shown").
        part.IsVisible = true;
        panel.InvalidateMeasure();
        ((Layoutable)panel.Parent!).InvalidateMeasure();
        part.Measure(Slot);
        part.Arrange(new Rect(Slot));

        // Each chunk measured a card with WrapPanel's own constraint, so the real
        // layout only PLACES it: one measure per card, ever.
        Assert.All(panel.Children.Cast<Card>(), c => Assert.Equal(1, c.Measures));
        Assert.Equal(7, panel.Children.Select(c => c.Bounds.Position).Distinct().Count());
        Assert.True(panel.DesiredSize.Height > 40, "the shown panel did not wrap its cards onto rows");
    }

    [Fact]
    public void AVisiblePanel_IsAPlainWrapPanel()
    {
        var panel = new ChunkedWrapPanel { ChunkSize = 2 };
        var plain = new WrapPanel();
        for (var i = 0; i < 7; i++)
        {
            panel.Children.Add(new Border { Width = 90, Height = 40 });
            plain.Children.Add(new Border { Width = 90, Height = 40 });
        }

        panel.Measure(Slot);
        panel.Arrange(new Rect(Slot));
        plain.Measure(Slot);
        plain.Arrange(new Rect(Slot));

        Assert.Equal(7, Measured(panel));
        Assert.Equal(plain.DesiredSize, panel.DesiredSize);
        Assert.Equal(plain.Children.Select(c => c.Bounds), panel.Children.Select(c => c.Bounds));
        Assert.Equal(int.MaxValue, panel.RealizedLimit);
    }

    [Fact]
    public void ASettle_DoesNotCountTheHeldBackCardsAsStaleWork()
    {
        var (part, panel) = HiddenCards(count: 10, chunk: 3);
        HiddenSubtreePrewarmer.Realize(part, Slot);

        // The cards a chunk has not granted are unmeasured on purpose. Counted as
        // stale, they would make the settle turn re-run the realize four times and
        // then lay them out anyway — or never stop asking.
        Assert.Equal(0, HiddenSubtreePrewarmer.Settle(part, Slot));
        Assert.Equal(3, Measured(panel));
    }

    [Fact]
    public void ANestedHiddenPanel_IsNotThisPartsToChunk()
    {
        var (inner, panel) = HiddenCards(count: 10, chunk: 3);
        ((Decorator)inner.Parent!).Child = null;
        var part = new Border { Child = new Decorator { Child = inner }, IsVisible = false };
        _ = new Decorator { Child = part };

        HiddenSubtreePrewarmer.Realize(part, Slot);

        // The inner gate is a part of its own (the prewarmer never recurses into
        // one), so its cards are its own realize's — not this part's chunks.
        Assert.Empty(HiddenSubtreePrewarmer.ChunkedPanelsUnder(part));
        Assert.False(HiddenSubtreePrewarmer.RealizeNextChunk(part, Slot));
        Assert.Equal(0, Measured(panel));
    }

    /// <summary>
    ///     The drain lays out at most ONE chunk per dispatcher turn: the
    ///     <c>chunk</c> branch of <c>ScheduleDrain</c>'s step calls
    ///     <see cref="HiddenSubtreePrewarmer.RealizeNextChunk" /> once and posts the
    ///     next step. A loop there would be the 1-2 s turn again.
    /// </summary>
    [Fact]
    public void TheDrain_LaysOutOneChunkPerTurn()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "HiddenSubtreePrewarmer.cs")));
        var drain = src[src.IndexOf("public static void ScheduleDrain(", StringComparison.Ordinal)..];
        drain = drain[..drain.IndexOf("public static int Settle(", StringComparison.Ordinal)];

        Assert.Single(Regex.Matches(drain, @"RealizeNextChunk\("));
        Assert.Matches(new Regex(
            @"if \(RealizeNextChunk\(chunked, available\(\)\)\)\s*\{[^{}]*PrewarmPacer\.Post\(Step\);\s*return;\s*\}"), drain);
        Assert.DoesNotMatch(new Regex(@"(while|for|foreach)\s*\([^)]*RealizeNextChunk"), drain);
    }

    /// <summary>
    ///     The tone browser is realized ONCE PER RIG INSTANCE. There are two
    ///     instances of <c>AmpFxRigView</c> — the chart editor's rig overlay
    ///     (<c>MainWindow.axaml</c>, <c>ShellRigScreenHost</c>) and Play Mode's
    ///     (<c>PlayModeView.axaml</c>, <c>RigScreenHost</c>) — each with its own
    ///     <c>RigTonesView</c>, and prewarming one lays out nothing of the other.
    ///     So the browser is named exactly once by each owner, each scoped to its
    ///     own rig, and a third host must come with a third entry.
    /// </summary>
    [Fact]
    public void TheToneBrowser_IsListedOncePerRigInstance()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var hosts = UiSourceTree.GetFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetFileName(f), Regex.Matches(UiSourceTree.ReadAllText(f), @"<\w+:AmpFxRigView\b").Count))
            .Where(h => h.Count > 0)
            .ToList();
        Assert.Equal([("MainWindow.axaml", 1), ("PlayModeView.axaml", 1)], hosts.OrderBy(h => h.File, StringComparer.Ordinal));

        var shell = string.Join('\n', UiSourceTree.GetFiles(views, "MainWindow*.cs", SearchOption.AllDirectories)
            .Select(UiSourceTree.ReadAllText).Select(StripComments));
        Assert.Single(Regex.Matches(shell, "\"RigToneBrowser\""));
        // …and it is the shell rig's: looked up under PedalboardOverlay.
        Assert.Matches(new Regex(@"HiddenNamedDescendants\(rig,\s*\[""RigTonesMode"", ""RigToneBrowser"""), shell);

        var playMode = StripComments(UiSourceTree.ReadAllText(Path.Combine(views, "PlayMode", "PlayModeView.axaml.cs")));
        Assert.Single(Regex.Matches(playMode, "\"RigToneBrowser\""));
        Assert.Contains("RigToneBrowser", DotCharter.UI.Views.PlayMode.PlayModeView.NestedScreenBodyNames);
    }

    /// <summary>The cards' panel IS the chunked one — without it the browser is one
    /// realize turn again and every guard above passes over a panel nobody uses.</summary>
    [Fact]
    public void TheToneCards_AreLaidOutByAChunkedPanel()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "RigTonesView.axaml")));
        var cards = markup[markup.IndexOf("x:Name=\"RigBrowserCards\"", StringComparison.Ordinal)..];
        cards = cards[..cards.IndexOf("</ItemsControl.ItemsPanel>", StringComparison.Ordinal)];

        Assert.Contains("<controls:ChunkedWrapPanel", cards, StringComparison.Ordinal);
        Assert.Contains("xmlns:controls=\"using:DotCharter.Tone.UI.Controls\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AHiddenStack_LaysOutAGroupPerTurn_AndShownIsAPlainStackPanel()
    {
        var stack = new ChunkedStackPanel { ChunkSize = 1, Spacing = 4 };
        var plain = new StackPanel { Spacing = 4 };
        for (var i = 0; i < 4; i++)
        {
            stack.Children.Add(new Card());
            plain.Children.Add(new Card());
        }

        var part = new Border { Child = stack, IsVisible = false };
        _ = new Decorator { Child = part };

        HiddenSubtreePrewarmer.Realize(part, Slot);
        var perTurn = new List<int> { Measured(stack) };
        while (HiddenSubtreePrewarmer.RealizeNextChunk(part, Slot))
            perTurn.Add(Measured(stack));
        Assert.Equal([1, 2, 3, 4], perTurn);

        // Hidden, it reports the whole stack's size; shown, it IS a StackPanel —
        // and every group keeps the one measure its chunk gave it.
        plain.Measure(Slot);
        Assert.Equal(plain.DesiredSize, stack.DesiredSize);
        part.IsVisible = true;
        stack.InvalidateMeasure();
        part.Measure(Slot);
        part.Arrange(new Rect(Slot));
        plain.Arrange(new Rect(Slot));
        Assert.Equal(plain.Children.Select(c => c.Bounds), stack.Children.Select(c => c.Bounds));
        Assert.All(stack.Children.Cast<Card>(), c => Assert.Equal(1, c.Measures));
    }

    [Fact]
    public void AMasonryPanel_ChunksOnlyWhenMarkupAsksIt()
    {
        static (Border Part, MasonryPanel Panel) Hidden(int chunk)
        {
            var panel = new MasonryPanel { ColumnCount = 1 };
            if (chunk > 0) panel.ChunkSize = chunk;
            for (var i = 0; i < 5; i++)
                panel.Children.Add(new Card());
            var part = new Border { Child = panel, IsVisible = false };
            _ = new Decorator { Child = part };
            return (part, panel);
        }

        var (plainPart, plain) = Hidden(chunk: 0);
        HiddenSubtreePrewarmer.Realize(plainPart, Slot);
        Assert.Equal(5, Measured(plain));
        Assert.False(plain.HasBacklog);

        var (part, chunked) = Hidden(chunk: 2);
        HiddenSubtreePrewarmer.Realize(part, Slot);
        var perTurn = new List<int> { Measured(chunked) };
        while (HiddenSubtreePrewarmer.RealizeNextChunk(part, Slot))
            perTurn.Add(Measured(chunked));
        Assert.Equal([2, 4, 5], perTurn);
        Assert.Equal(plain.DesiredSize, chunked.DesiredSize);
    }

    /// <summary>The settings tabs whose single prewarm turn was the longest after the
    /// tone browser's (Keybinds ~600 ms, Appearance ~390 ms, Release R2R) stack their
    /// groups in a chunked panel — without it each is one turn again and the tests
    /// above pass over a panel nobody uses. The Inspector's Mixer tab (~230-280 ms)
    /// left this list with #601: its five groups are rail SECTIONS now, each its own
    /// prewarm turn through <c>InspectorView.NestedPaneNames</c>.</summary>
    [Theory]
    [InlineData("Settings/KeybindsSettingsView.axaml", "x:Name=\"KeybindGroups\"", "</ItemsControl.ItemsPanel>",
        "<controls:ChunkedStackPanel")]
    [InlineData("Settings/AppearanceSettingsView.axaml", "x:Name=\"AppearanceColorGroups\"", "</ItemsControl.ItemsPanel>",
        "<controls:ChunkedStackPanel")]
    public void TheHeaviestTabs_StackTheirGroupsInAChunkedPanel(string view, string anchor, string end, string chunked)
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            [TestPaths.RepoRoot(), "DotCharter.UI", "Views", .. view.Split('/')])));
        var at = markup.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{view} no longer declares {anchor}; this guard is blind");
        var element = markup[at..];
        element = element[..element.IndexOf(end, StringComparison.Ordinal)];
        Assert.Contains(chunked, element, StringComparison.Ordinal);
    }

    private static string StripComments(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
}
