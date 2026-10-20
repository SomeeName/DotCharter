// The rig's tone browser in the startup prewarm (#253): its preset cards are laid
// out a chunk per drain turn, and the first show of the browser builds nothing.

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.Views.Shell;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The tone browser on the REAL rig view, drained the way both shells drain it
///     (the rig, then <c>RigTonesMode</c>, <c>RigToneBrowser</c>,
///     <c>RigToneDesignPanel</c>). It was ONE realize turn of 1.1-2.0 s per rig
///     instance on the Release R2R build, and a click during it waited for all of
///     it. Structure only, never a wall clock (repo rule): what is pinned is how many
///     cards each turn lays out, and that the first show builds and re-measures
///     nothing.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneBrowserChunkedPrewarmUiPerfSmokeTests
{
    private const int PresetCount = 40;
    private static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);
    private static readonly string[] BrowserParts = ["RigTonesMode", "RigToneBrowser", "RigToneDesignPanel"];

    private static (PedalboardViewModel Rig, AmpFxRigView View, Panel Overlay, Window Window) HiddenRig()
    {
        var presets = Enumerable.Range(0, PresetCount)
            .Select(i => new DspTonePreset($"p{i}", $"Preset {i}", (DspToneCategory)(i % 5), "", new DcRigPreset([])))
            .ToList();
        var rig = TestRigBuilders.Pedalboard(
            new Infrastructure.FakeDspChainService(), new Infrastructure.FakeDspTonePresetService(presets), LocalizationService.Instance);
        var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
        // The shells' shape: the rig under a hidden overlay, beside what is showing.
        var overlay = new Panel { IsVisible = false, Children = { view } };
        var window = new Window
        {
            Width = 1600,
            Height = 1000,
            Content = new Grid { Children = { new Border(), overlay } }
        };
        window.Show();
        return (rig, view, overlay, window);
    }

    private static ChunkedWrapPanel Cards(AmpFxRigView view) =>
        view.GetVisualDescendants().OfType<ChunkedWrapPanel>().Single();

    [Fact]
    public async Task TheDrain_LaysTheCardsOutAChunkPerTurn_AndTheFirstShowBuildsNothing()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var (rig, view, overlay, window) = HiddenRig();
            try
            {
                Assert.Equal(PresetCount, rig.Browser.Cards.Count);

                IEnumerable<Control?> Parts()
                {
                    yield return overlay;
                    foreach (var part in HiddenSubtreePrewarmer.HiddenNamedDescendants(view, BrowserParts))
                        yield return part;
                }

                var turns = new List<(string Kind, string Part, int Cards)>();
                var done = false;
                HiddenSubtreePrewarmer.ScheduleDrain(Parts(), () => window.Bounds.Size, (_, _) => done = true,
                    onStep: step => turns.Add((step.Kind, step.PartName,
                        view.GetVisualDescendants().OfType<ChunkedWrapPanel>().FirstOrDefault()?.Children
                            .Count(c => c.IsMeasureValid) ?? 0)));
                var deadline = DateTime.UtcNow + DeadlockCap;
                while (!done && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    if (!done) await Task.Delay(5);
                }

                Assert.True(done, "the prewarm drain never finished (deadlock detector, not a budget — the box was starved)");
                Assert.False(overlay.IsVisible);

                var panel = Cards(view);
                var chunk = panel.ChunkSize;
                Assert.True(chunk < PresetCount, "the chunk holds every card; this case proves nothing");
                Assert.Equal(PresetCount, panel.Children.Count);
                Assert.All(panel.Children, c => Assert.True(c.IsMeasureValid, "a card was left for the first show"));

                // The browser is realized ONCE, and that turn lays out one chunk…
                var realize = turns.FindIndex(t => t is { Kind: "realize", Part: "RigToneBrowser" });
                Assert.True(realize >= 0, "the drain never realized the tone browser");
                Assert.Single(turns, t => t is { Kind: "realize", Part: "RigToneBrowser" });
                Assert.Equal(chunk, turns[realize].Cards);

                // …and every turn after it at most one more, until the list is done.
                var chunkTurns = turns.Where(t => t.Kind == "chunk").ToList();
                Assert.Equal((PresetCount + chunk - 1) / chunk - 1, chunkTurns.Count);
                var laidOut = turns[realize].Cards;
                foreach (var turn in turns.Skip(realize + 1))
                {
                    Assert.True(turn.Cards - laidOut <= chunk,
                        $"one {turn.Kind} turn laid out {turn.Cards - laidOut} cards; a turn may lay out one chunk ({chunk})");
                    laidOut = turn.Cards;
                }

                // The first show: no card is constructed. (The grid's scroll bar
                // builds its two page buttons' borders on the show with or without
                // chunking — measured with the chunk lifted — so this counts the
                // card grid, the part the drain is responsible for.)
                var before = panel.GetVisualDescendants().ToHashSet();
                overlay.IsVisible = true;
                rig.IsBrowserOpen = true;
                window.UpdateLayout();
                var created = panel.GetVisualDescendants().Where(v => !before.Contains(v)).ToList();
                Assert.True(created.Count == 0, $"opening the prewarmed tone browser built {created.Count} controls in the card grid on the click: " +
                    string.Join(", ", created.Select(v => $"{v.GetType().Name} under {v.GetVisualParent()?.GetType().Name}")));
                AssertCardsLaidOut(panel);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A user who opens the browser before the drain has reached its last chunk
    ///     still gets every card: the panel stops limiting itself the moment it is
    ///     shown and lays out the rest, as a plain <see cref="WrapPanel" />.
    /// </summary>
    [Fact]
    public async Task AShowBeforeTheLastChunk_LaysOutEveryCard()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (rig, view, overlay, window) = HiddenRig();
            try
            {
                HiddenSubtreePrewarmer.Realize(overlay, window.Bounds.Size);
                foreach (var part in HiddenSubtreePrewarmer.HiddenNamedDescendants(view, BrowserParts))
                    HiddenSubtreePrewarmer.Realize(part, window.Bounds.Size);
                var panel = Cards(view);
                Assert.True(panel.HasBacklog, "every card was laid out in one go; this case proves nothing");

                overlay.IsVisible = true;
                rig.IsBrowserOpen = true;
                window.UpdateLayout();

                Assert.False(panel.HasBacklog);
                Assert.Equal(int.MaxValue, panel.RealizedLimit);
                AssertCardsLaidOut(panel);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The show itself must re-measure the panel: its last measure was a hidden,
    ///     limited one, and nothing else on the way down is guaranteed to be stale —
    ///     here nothing is (fixed slot, fixed cards), so without the panel's own
    ///     invalidation on its first show the held-back cards would never be laid out.
    /// </summary>
    [Fact]
    public async Task AChunkedPanel_LaysOutInFull_TheMomentItIsShown()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var panel = new ChunkedWrapPanel { ChunkSize = 2 };
            for (var i = 0; i < 9; i++)
                panel.Children.Add(new Border { Width = 90, Height = 40 });
            var part = new Border { IsVisible = false, Child = new StackPanel { Children = { panel } } };
            var window = new Window { Width = 400, Height = 300, Content = new Grid { Children = { part } } };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(HiddenSubtreePrewarmer.Realize(part, window.Bounds.Size));
                Assert.Equal(2, panel.Children.Count(c => c.IsMeasureValid));

                part.IsVisible = true;
                window.UpdateLayout();

                Assert.All(panel.Children, c => Assert.True(c.IsMeasureValid && c.Bounds.Width > 0,
                    "a held-back card was not laid out by the show"));
                Assert.Equal(9, panel.Children.Select(c => c.Bounds.Position).Distinct().Count());
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static void AssertCardsLaidOut(ChunkedWrapPanel panel)
    {
        Assert.Equal(PresetCount, panel.Children.Count);
        Assert.All(panel.Children, c => Assert.True(c.Bounds is { Width: > 0, Height: > 0 },
            "a card has no size: the show did not lay it out"));
        Assert.Equal(PresetCount, panel.Children.Select(c => c.Bounds.Position).Distinct().Count());
        Assert.True(panel.Bounds.Height > panel.Children[0].Bounds.Height,
            "the cards did not wrap onto rows: the shown panel is still the hidden, limited layout");
    }
}
