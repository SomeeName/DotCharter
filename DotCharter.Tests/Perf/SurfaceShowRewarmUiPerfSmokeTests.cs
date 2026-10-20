using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Editing;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #141, the half the Phrases fix left: switching the Inspector to Song and
///     opening the Tone designer stuttered on a real project. Three causes, one
///     structural fact each; the timings are LOGGED (headless, Debug, so
///     directional), never asserted.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>The Song tab is prewarmed once at startup — before a project is
///         open — and the project's rows then arrive behind its
///         <c>IsVisible=false</c>, so the click laid them out
///         (<see cref="HiddenSurfaceRewarmer" />).</item>
///         <item>Every hosted dialog open took the pooled view OUT of the tree
///         and put it back to raise it, which drops and re-applies every style
///         in it (<see cref="DialogHost" />).</item>
///         <item>The Tone designer built every arrangement's five
///         <c>AutoCompleteBox</c>es on every open, seen or not.</item>
///     </list>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class SurfaceShowRewarmUiPerfSmokeTests(ITestOutputHelper output)
{
    private const double PaneWidth = 400;
    private const double PaneHeight = 820;

    // ── the Song tab ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheSongTab_LaysOutRowsThatArriveWhileItIsHidden_BeforeTheClick()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // What the click cost before: rows arrive, the click comes at once.
            var before = WithSongTab((tab, state, window, other) =>
            {
                state.ActiveProject = SongProject();
                return TimeShow(tab, other, window);
            });

            var after = WithSongTab((tab, state, window, other) =>
            {
                state.ActiveProject = SongProject();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

                Assert.False(tab.IsVisible, "the harness showed the tab — nothing below would mean anything");
                Assert.True(tab.Rewarmer.Rewarms >= 1,
                    "the Song tab's lists changed while it was hidden and nothing re-realized it — the " +
                    "click on 'Song' lays every new row out");
                var stale = StaleNodes(tab);
                Assert.True(stale.Count == 0,
                    $"{stale.Count} node(s) under the hidden Song tab still need a layout after the background " +
                    $"rewarm ({string.Join(", ", stale.Select(s => s.GetType().Name).Distinct())}) — the click pays for them");

                var vm = (SongOverviewViewModel)tab.DataContext!;
                foreach (var list in tab.GetVisualDescendants().OfType<ListBox>())
                {
                    Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Any(),
                        $"the '{list.Name ?? list.GetType().Name}' list realized no rows while hidden " +
                        $"(tones {vm.Tones.Count}, sections {vm.Sections.Count}, phrases {vm.Phrases.Count})");
                }

                return TimeShow(tab, other, window);
            });

            output.WriteLine($"Song tab, first show after the project opened behind it: {before:0.0} ms -> {after:0.0} ms " +
                             "(headless, Debug)");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The other half of the trade: a list long enough that building all of it
    ///     would cost more than the click keeps virtualizing, as it has since a dense
    ///     arrangement's hundreds of rows were first measured there.
    /// </summary>
    [Fact]
    public async Task ALongSongTabList_StillVirtualizes()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            const int names = 200;
            WithSongTab((tab, state, window, other) =>
            {
                state.ActiveProject = SongProject(phraseNames: names);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                TimeShow(tab, other, window);

                var phrases = tab.GetVisualDescendants().OfType<ListBox>()
                    .Single(l => ReferenceEquals(l.ItemsSource, ((SongOverviewViewModel)tab.DataContext!).Phrases));
                var rows = phrases.GetVisualDescendants().OfType<ListBoxItem>().Count(r => r.IsVisible);
                Assert.True(rows is > 0 and < names,
                    $"{rows} of {names} phrase rows were built — a long list must virtualize inside its 360 px " +
                    "viewport (ItemsPanelConverters.StackedRowLimit), or the background rewarm builds hundreds of rows " +
                    "in one uninterruptible step");
                return 0;
            });
        }, CancellationToken.None);
    }

    private static double WithSongTab(Func<SongInspectorView, ProjectState, Window, Control, double> act)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var vm = new SongOverviewViewModel(state, new HistoryService(logger), logger, FakeLocalization.English());
        var tab = new SongInspectorView { DataContext = vm, IsVisible = false };
        var other = new Border { Height = 300 };
        var window = new Window { Width = PaneWidth, Height = PaneHeight, Content = new Grid { Children = { other, tab } } };
        window.Show();
        try
        {
            window.UpdateLayout();
            // The startup prewarm, which runs before any project is open.
            Assert.True(HiddenSubtreePrewarmer.Realize(tab, new Size(PaneWidth, PaneHeight)));
            return act(tab, state, window, other);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A long chart's worth of names: 24 tones, 15 sections and 20 phrases
    /// over two arrangements (the corpus song measured for #141 had 4 / 9 / 0).</summary>
    private static ChartProject SongProject(int phraseNames = 20)
    {
        var project = new ChartProject();
        foreach (var name in new[] { "Lead", "Rhythm" })
        {
            var arrangement = new Arrangement { Name = name, StartingTone = "Tone 00" };
            for (var i = 0; i < 24; i++) arrangement.ToneChanges.Add(new ToneChange { ToneName = $"Tone {i:00}" });
            for (var i = 0; i < Math.Max(40, phraseNames); i++)
                arrangement.EventPoints.Add(new EventPoint { Section = $"section {i % 15:00}", Phrase = $"phrase {i % phraseNames:000}" });
            project.Arrangements.Add(arrangement);
        }

        return project;
    }

    /// <summary>What the real show would still have to lay out: stale nodes reached
    /// through visible elements only (a collapsed card is its own gate).</summary>
    private static List<Layoutable> StaleNodes(Visual root)
    {
        var stale = new List<Layoutable>();

        void Walk(Visual parent)
        {
            foreach (var child in parent.GetVisualChildren())
            {
                if (!child.IsVisible) continue;
                if (child is Layoutable { IsMeasureValid: false } or Layoutable { IsArrangeValid: false })
                    stale.Add((Layoutable)child);
                Walk(child);
            }
        }

        Walk(root);
        return stale;
    }

    private static double TimeShow(Control tab, Control other, Window window)
    {
        var sw = Stopwatch.StartNew();
        other.IsVisible = false;
        tab.IsVisible = true;
        window.UpdateLayout();
        return sw.Elapsed.TotalMilliseconds;
    }

    // ── the dialog host ────────────────────────────────────────────────────

    /// <summary>
    ///     Raising a pooled dialog by <c>Children.Remove</c> + <c>Add</c> detached it
    ///     from the logical tree, and a detached control drops every style; the
    ///     re-attach applied them all again — 55-130 ms per Tone designer open
    ///     (Debug), every open, which also threw away what the context-free prewarm
    ///     had paid for. The host now reorders in place.
    /// </summary>
    [Fact]
    public async Task OpeningAPrewarmedDialog_KeepsItInTheTree_SoItsStylesSurvive()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = ScreenCatalog.Default.Entries.Single(e => e.Contract == typeof(IToneDesignerScreen));
            var fake = FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == FakeVariantNames.Default);
            var services = new ServiceCollection();
            services.AddSingleton<DialogsSlot>();
            services.AddScoped(entry.Contract, _ => fake.Create());
            // A second dialog to stack on top, so the scrim has to move under it.
            var other = ScreenCatalog.Default.Entries.First(e => e.Kind == ScreenKind.Dialog && e.Contract != entry.Contract
                                                                  && e.Contract.GetInterfaces().Contains(typeof(IDialogScreen<bool>)));
            var otherFake = FakeScreens.VariantsOf(other.Contract).Single(v => v.Name == FakeVariantNames.Default);
            services.AddScoped(other.Contract, _ => otherFake.Create());
            using var provider = services.BuildServiceProvider();

            WithDialogHost(provider, (host, window) =>
            {
                var view = host.Children.OfType<ToneDesignerDialogView>().Single();
                var detaches = 0;
                view.DetachedFromLogicalTree += (_, _) => detaches++;
                view.DetachedFromVisualTree += (_, _) => detaches++;

                for (var open = 1; open <= 2; open++)
                {
                    _ = host.ShowAsync<IToneDesignerScreen, bool>();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();

                    Assert.Same(view, Assert.Single(host.OpenViews));
                    Assert.True(detaches == 0,
                        $"open {open}: the pooled Tone designer view left the tree {detaches} time(s) to be raised — " +
                        "every style in it was dropped and re-applied, and its prewarm was thrown away");
                    Assert.Same(view, host.Children[^1]);
                    Assert.Same(view, host.GetVisualChildren().Last());
                    Assert.Equal(host.Children.Count - 2, host.Children.IndexOf(host.Scrim));

                    // Stack another dialog on it: the scrim moves under the new top…
                    _ = OpenAsync(host, other.Contract);
                    Dispatcher.UIThread.RunJobs();
                    var top = host.OpenViews[^1];
                    Assert.Equal(host.Children.IndexOf(top) - 1, host.Children.IndexOf(host.Scrim));
                    Assert.True(host.Children.IndexOf(view) < host.Children.IndexOf(host.Scrim),
                        "the scrim must dim the lower dialog");
                    // …and back under the designer when that one closes.
                    host.CloseTop();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(host.Children.IndexOf(view) - 1, host.Children.IndexOf(host.Scrim));

                    host.CloseTop();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.Empty(host.OpenViews);
                    Assert.False(host.Scrim.IsVisible);
                }

                Assert.Equal(0, detaches);
            });
        }, CancellationToken.None);
    }

    private static Task OpenAsync(DialogHost host, Type contract)
    {
        var show = typeof(IDialogs).GetMethod(nameof(IDialogs.ShowAsync))!.MakeGenericMethod(contract, typeof(bool));
        return (Task)show.Invoke(host, [null])!;
    }

    // ── the Tone designer's slot cards ─────────────────────────────────────

    /// <summary>
    ///     The arrangement slot cards are rebuilt on every open (the view model is
    ///     new each time). Only the cards the column shows are built, and since #173
    ///     their five slots are resting fields that build no <c>AutoCompleteBox</c>
    ///     until focused (<see cref="ToneDesignerSlotEditorUiPerfSmokeTests" />).
    ///     Also logs what an open costs on an owner-sized project.
    /// </summary>
    [Fact]
    public async Task TheToneDesigner_BuildsOnlyTheSlotCardsItsColumnShows()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // The first pass is the cold one (the JIT, the first real view model); the
            // repeat of the owner-sized project is the steady state.
            foreach (var (arrangements, tones) in new[] { (2, 5), (10, 5), (2, 5) })
            {
                var project = ToneProject(arrangements, tones);
                var services = new ServiceCollection();
                services.AddSingleton<DialogsSlot>();
                services.AddScoped<IToneDesignerScreen>(_ => ToneDesigner(project));
                using var provider = services.BuildServiceProvider();

                WithDialogHost(provider, (host, window) =>
                {
                    var times = new List<double>();
                    for (var open = 0; open < 3; open++)
                    {
                        var sw = Stopwatch.StartNew();
                        _ = host.ShowAsync<IToneDesignerScreen, bool>();
                        window.UpdateLayout();
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        Dispatcher.UIThread.RunJobs();

                        var view = host.OpenViews[^1];
                        var rows = view.GetVisualDescendants().OfType<ItemsControl>()
                            .Single(c => c.Name == "ArrangementSlotRows");
                        // Five slot fields per card (ToneDesignerDialogView.axaml). They are
                        // DeferredAutoCompleteBoxes since #173: no AutoCompleteBox at rest.
                        var cards = rows.GetVisualDescendants().OfType<DeferredAutoCompleteBox>()
                            .Count(b => b.IsEffectivelyVisible) / 5;
                        Assert.Empty(rows.GetVisualDescendants().OfType<AutoCompleteBox>());
                        Assert.True(cards > 0, "the Tone designer built no arrangement slot card at all");
                        if (arrangements > 4)
                        {
                            Assert.True(cards < arrangements,
                                $"all {arrangements} arrangement slot cards were built ({cards}) though the column " +
                                "shows a few — ArrangementSlotRows no longer virtualizes, and every open builds " +
                                "every arrangement's card");
                        }
                        else
                        {
                            Assert.Equal(arrangements, cards);
                        }

                        host.CloseTop();
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                    }

                    output.WriteLine($"Tone designer, {arrangements} arrangements / {tones} tones, open + layout: " +
                                     string.Join(" | ", times.Select(t => $"{t:0.0} ms")) + " (headless, Debug)");
                });
            }
        }, CancellationToken.None);
    }

    internal static ChartProject ToneProject(int arrangements, int tones)
    {
        var project = new ChartProject();
        for (var i = 0; i < tones; i++)
        {
            var key = $"tone{i}";
            project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared))
            });
        }

        for (var i = 0; i < arrangements; i++)
        {
            project.Arrangements.Add(new Arrangement
            {
                Name = $"Part {i}", ArrangementType = i % 2 == 0 ? ArrangementType.Lead : ArrangementType.Bass,
                ToneBase = "tone0", ToneA = "tone0", ToneB = "tone1"
            });
        }

        return project;
    }

    internal static ToneDesignerViewModel ToneDesigner(ChartProject project)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var chain = new FakeDspChainService([]);
        var edit = new ChartEditDialogServices(
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<IChartEditorSurface>().Proxy,
            history, new ProjectState { ActiveProject = project }, new FakeConfigurationService(),
            FakeLocalization.English(), logger);
        return new ToneDesignerViewModel(edit, new FakeFileStorageService(), FakeTonelabSession.Targets(new TonePreviewSession(chain, new DcRigApplier(chain))),
            new ToneDesignerTools(new DotCharter.Services.Editing.Tones.ToneGenerationRun(history,
                new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger), new DotCharter.Presentation.Dialogs.DialogsSlot()), new DialogMemory(),
            DotCharter.Services.Plugins.PackageFeatures.All);
    }

    /// <summary>A real <see cref="DialogHost" /> inside the real <see cref="DialogContainerView" />
    /// in a shown window, with the app's context-free prewarm already drained.</summary>
    internal static void WithDialogHost(ServiceProvider provider, Action<DialogHost, Window> act)
    {
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = 1280, Height = 900, Content = container };
        window.Show();
        try
        {
            window.UpdateLayout();
            foreach (var part in host.HiddenSurfaces()) HiddenSubtreePrewarmer.Realize(part, new Size(1280, 900));
            act(host, window);
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }
}
