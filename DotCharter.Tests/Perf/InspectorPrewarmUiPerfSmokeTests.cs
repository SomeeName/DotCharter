using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What the inspector's tab strip costs on a first switch, and what
///     <see cref="InspectorView.HiddenSurfaces" /> takes off that path.
/// </summary>
/// <remarks>
///     <para>
///         The inspector is a DOCK pane. All eight tab panels are CONSTRUCTED
///         with <c>InspectorView</c>, but Avalonia skips <c>MeasureCore</c> for an
///         invisible element, so styles, <c>ApplyTemplate</c>, resource lookups
///         and text layout were all deferred to the user's first click on each
///         tab header — and no prewarm mechanism reached the pane at all, because
///         the app <c>ViewLocator</c> materializes it inside <c>BottomDock</c> and
///         it is named in no markup the shell can <c>FindControl</c>.
///     </para>
///     <para>
///         Measured here, headless, Debug, <c>r2r=no</c> — so directional, not
///         product figures — and LOGGED rather than asserted, per this repo's
///         no-wall-clock-in-a-gate rule. What is asserted is what can break
///         silently: that the sweep finds the panels at all, and that realizing
///         one really does measure it.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class InspectorPrewarmUiPerfSmokeTests(ITestOutputHelper output)
{
    private const double W = 1200;
    private const double H = 820;
    private static readonly Size Slot = new(W, H);

    /// <summary>
    ///     The premise. Laid out with no DataContext every
    ///     <c>IsVisible="{Binding …}"</c> keeps its default (true), so the harness
    ///     hides the seven inactive tabs itself — that is the shape the app is in,
    ///     where exactly one tab is selected.
    /// </summary>
    [Fact]
    public Task TheSweep_FindsEveryInactiveTabPanel() =>
        WithInspector((inspector, _) =>
        {
            var hidden = inspector.HiddenSurfaces();

            // Pinned against InspectorView.axaml's InspectorTabPanels Grid: nine
            // tab UserControls, one of them active. Re-count the markup when a
            // tab is added or removed — a hard-coded count over derived markup is
            // the failure mode this suite keeps having. After them come the hidden
            // SECTIONS of the tabs with a rail (#601), every one named in
            // InspectorView.NestedPaneNames.
            var tabs = hidden.Where(c => c.Parent is Grid { Name: "InspectorTabPanels" }).ToList();
            Assert.Equal(8, tabs.Count);
            Assert.All(hidden, c => Assert.False(c.IsVisible));
            Assert.DoesNotContain(hidden, c => c is SoundInspectorView);
            var sections = hidden.Except(tabs).ToList();
            Assert.NotEmpty(sections);
            Assert.All(sections, c => Assert.Contains(c.Name, InspectorView.NestedPaneNames));
        });

    /// <summary>
    ///     The Sound tab's own cards are one level further down, and the prewarmer
    ///     stops at the first hidden root on purpose — so the tab being realized
    ///     buys them nothing. They were the two largest gated surfaces in the app
    ///     that no mechanism reached.
    /// </summary>
    [Fact]
    public Task TheSoundTab_ListsItsOwnGatedCards() =>
        WithInspector((inspector, _) =>
        {
            var sound = inspector.SoundTab;
            Assert.NotNull(sound);

            var cards = sound.HiddenSurfaces();
            // SoundInspectorView.HiddenSurfaces(): every section body of the #601 rail plus the
            // two bend hosts inside Bends, named in SoundInspectorView.SectionBodyNames. With no
            // DataContext every one of them is hidden.
            Assert.Equal(SoundInspectorView.SectionBodyNames.Length, cards.Count);
            Assert.All(cards, c => Assert.False(c.IsVisible));
        });

    /// <summary>
    ///     <b>The measurement.</b> One window pays every tab's first show cold;
    ///     a second one is drained through <c>HiddenSubtreePrewarmer</c> first and
    ///     pays it again. Both are logged. The ASSERTION is structural: the
    ///     prewarm must actually have measured each panel, because a realize that
    ///     silently did nothing looks exactly like a fast one.
    /// </summary>
    [Fact]
    public async Task RealizingTheInactiveTabs_TakesTheirFirstLayoutOffTheClick()
    {
        var cold = new List<(string Name, double Ms)>();
        var warm = new List<(string Name, double Ms)>();
        var prewarmMs = 0.0;

        await WithInspector((inspector, window) =>
        {
            foreach (var panel in inspector.HiddenSurfaces())
                cold.Add((panel.GetType().Name, TimeFirstShow(panel, window)));
        });

        await WithInspector((inspector, window) =>
        {
            var sw = Stopwatch.StartNew();
            foreach (var panel in inspector.HiddenSurfaces())
            {
                Assert.True(HiddenSubtreePrewarmer.Realize(panel, Slot),
                    $"{panel.GetType().Name} was not realized — the prewarm did nothing for it, and a " +
                    "realize that silently no-ops looks exactly like a fast one.");
            }

            prewarmMs = sw.Elapsed.TotalMilliseconds;

            foreach (var panel in inspector.HiddenSurfaces())
            {
                Assert.True(RealizedHeightOf(panel) > 1,
                    $"{panel.GetType().Name} {panel.Name}'s content measured nothing after the prewarm — it was " +
                    "realized in name only. (The part ITSELF stays hidden and so keeps a zero DesiredSize " +
                    "by design; what the prewarm measures is its visible children.)");
                warm.Add((panel.GetType().Name, TimeFirstShow(panel, window)));
            }
        });

        output.WriteLine($"Inspector tab first-show, headless Debug r2r=no. Prewarm cost {prewarmMs:0.0} ms:");
        foreach (var (name, ms) in cold)
        {
            var after = warm.First(w => w.Name == name).Ms;
            output.WriteLine($"  {name,-28} {ms,7:0.0} ms -> {after,6:0.0} ms");
        }

        output.WriteLine($"  {"TOTAL",-28} {cold.Sum(c => c.Ms),7:0.0} ms -> {warm.Sum(w => w.Ms),6:0.0} ms");
    }

    /// <summary>Same measurement one level down, for the Sound tab's cards.</summary>
    [Fact]
    public async Task RealizingTheSoundCards_TakesTheirFirstLayoutOffTheSelection()
    {
        var cold = new List<(string Name, double Ms)>();
        var warm = new List<(string Name, double Ms)>();

        await WithInspector((inspector, window) =>
        {
            foreach (var card in inspector.SoundTab!.HiddenSurfaces())
                cold.Add((NameOf(card), TimeFirstShow(card, window)));
        });

        await WithInspector((inspector, window) =>
        {
            // The Sound tab is the ACTIVE one in this harness, which is the
            // state the cards are gated in: the tab is open and the user has not
            // selected a note yet. Its own realization is therefore already paid
            // by Window.Show(); what is still cold is each card body.
            var sound = inspector.SoundTab!;

            foreach (var card in sound.HiddenSurfaces())
            {
                Assert.True(HiddenSubtreePrewarmer.Realize(card, Slot),
                    $"{NameOf(card)} was not realized by the prewarm.");
                Assert.True(RealizedHeightOf(card) > 1,
                    $"{NameOf(card)}'s content measured nothing after the prewarm — realized in name only.");
                warm.Add((NameOf(card), TimeFirstShow(card, window)));
            }
        });

        output.WriteLine("Sound-tab card first-show, headless Debug r2r=no:");
        foreach (var (name, ms) in cold)
        {
            var after = warm.First(w => w.Name == name).Ms;
            output.WriteLine($"  {name,-28} {ms,7:0.0} ms -> {after,6:0.0} ms");
        }

        output.WriteLine($"  {"TOTAL",-28} {cold.Sum(c => c.Ms),7:0.0} ms -> {warm.Sum(w => w.Ms),6:0.0} ms");
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static string NameOf(Control c) => c.Name ?? c.GetType().Name;

    /// <summary>
    ///     What the prewarm actually produced. <c>Realize</c> never flips
    ///     <c>IsVisible</c>, so the hidden ROOT's own <c>MeasureCore</c> is still
    ///     skipped and its <c>DesiredSize</c> stays zero — the realization lives
    ///     in its visible children, and that is what has to be non-zero.
    /// </summary>
    private static double RealizedHeightOf(Control part) =>
        part.GetVisualChildren().OfType<Layoutable>().Select(c => c.DesiredSize.Height)
            .DefaultIfEmpty(0).Max();

    /// <summary>The user's gesture: the surface becomes visible and the next
    /// layout pass pays for whatever was deferred. Restored afterwards so the
    /// same window can time the next one.</summary>
    private static double TimeFirstShow(Control part, Window window)
    {
        var sw = Stopwatch.StartNew();
        part.IsVisible = true;
        window.UpdateLayout();
        var ms = sw.Elapsed.TotalMilliseconds;
        part.IsVisible = false;
        window.UpdateLayout();
        return ms;
    }

    private static async Task WithInspector(Action<InspectorView, Window> assert)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var inspector = new InspectorView();

            // With no DataContext every IsVisible binding produces no value and
            // the property keeps its default — TRUE. Put the tree into the shape
            // the app is really in (one active tab, one visible card body)
            // BEFORE the window is shown: `Window.Show()` IS the first layout
            // pass, and anything visible for it is realized there. Hiding the
            // panels afterwards would leave the realization cached and every
            // "cold" reading below would be ~0 ms — which is exactly what the
            // first cut of this harness measured.
            var grid = inspector.FindControl<Grid>("InspectorTabPanels");
            Assert.True(grid is not null,
                "InspectorView.axaml no longer names 'InspectorTabPanels' — HiddenSurfaces() looks it up " +
                "by name, so a rename there silently takes every inspector tab back to cold.");
            var panels = grid.Children.OfType<Control>().ToList();
            Assert.True(panels.Count >= 2, "InspectorView has no tab panels — the harness measured nothing");
            for (var i = 1; i < panels.Count; i++) panels[i].IsVisible = false;

            var soundTab = panels.OfType<SoundInspectorView>().Single();
            foreach (var name in SoundInspectorView.SectionBodyNames)
            {
                var card = soundTab.FindControl<Control>(name);
                Assert.True(card is not null,
                    $"SoundInspectorView.axaml no longer names '{name}' — the prewarm list looks it up " +
                    "by name and a rename there is a SILENT no-op.");
                card.IsVisible = false;
            }

            var window = new Window { Width = W, Height = H, Content = inspector };
            window.Show();
            try
            {
                window.UpdateLayout();
                assert(inspector, window);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
