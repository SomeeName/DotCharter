using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.Tests.UI.Linking;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Views.Editor.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The linked-measures views built for real on a live linked chart, attached to a window and
///     laid out: the link panel, the timeline overlay with its toast (and the panel opened through
///     a chip click), and the Inspector's banner. A view whose styles or bindings failed still
///     arranges, it just measures nothing, so each asserts a non-zero size.
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app session, which
///         cannot share a testhost with the gate.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class LinkedMeasuresConstructionUiPerfSmokeTests
{
    [Fact]
    public async Task LinkPanelView_lists_the_placements_and_lays_out() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (t, group) = Linked();
            t.Vm.OpenPanel(group.Id, t.Links.Lead, At(4, 2));
            var view = new LinkPanelView { DataContext = t.Vm.Panel };

            WithWindow(view, () =>
            {
                AssertMeasured(view, "LinkPanelView");
                // A resource that failed to resolve leaves the card unpainted but still sized.
                Assert.NotNull(view.FindControl<Border>("LinkPanelCard")!.Background);
                AssertMeasured(view.FindControl<Button>("LinkPanelUnlink")!, "Unlink selected");
                AssertMeasured(view.FindControl<Button>("LinkPanelSplit")!, "Split by arrangement");
                Assert.Equal(3, view.GetVisualDescendants().OfType<CheckBox>().Count());

                // Lead bar 7 and Rhythm bars 1 and 7 play the same and are in no group.
                var suggestions = view.FindControl<StackPanel>("LinkPanelSuggestions")!;
                Assert.True(suggestions.IsVisible);
                AssertMeasured(suggestions, "the re-link suggestions");
                var links = suggestions.GetVisualDescendants().OfType<Button>().Where(b => b.Command == null ||
                    t.Vm.Panel.Suggestions.Any(s => ReferenceEquals(s.LinkCommand, b.Command))).ToList();
                Assert.Equal(3, links.Count);
                Assert.All(links, b => AssertMeasured(b, "a Link button"));

                links[0].Command!.Execute(null);
                Layout(view);
                Assert.Equal(4, view.GetVisualDescendants().OfType<CheckBox>().Count());
                Assert.Equal(2, t.Vm.Panel.Suggestions.Count);
            });
        }, CancellationToken.None);

    [Fact]
    public async Task LinkOverlayView_shows_the_toast_inside_the_timeline_and_opens_the_panel() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (t, group) = Linked();
            var overlay = new LinkOverlayView { DataContext = t.Vm };

            WithWindow(overlay, () =>
            {
                var h = t.Links;
                h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8);
                Dispatcher.UIThread.RunJobs();
                Layout(overlay);

                var toast = overlay.FindControl<Border>("LinkToast")!;
                Assert.True(toast.IsVisible);
                AssertMeasured(toast, "the toast");
                AssertMeasured(overlay.FindControl<Button>("LinkToastOnlyHere")!, "Only here");
                var left = Canvas.GetLeft(toast);
                Assert.True(left >= 0 && left + toast.Bounds.Width <= overlay.Bounds.Width,
                    $"the toast sits at x={left:0.#}, {toast.Bounds.Width:0.#} px wide, in a {overlay.Bounds.Width:0.#} px timeline");

                t.Editor.LinkHighlight.RequestPanel(new LinkPanelRequest(group.Id, null, default, h.Lead, At(1, 1)));
                Dispatcher.UIThread.RunJobs();
                var panel = overlay.FindControl<LinkPanelView>("LinkPanel")!;
                Assert.Same(t.Vm.Panel, panel.DataContext);
                Assert.True(panel.IsAttachedToVisualTree(), "the chip click did not open the link panel");
                AssertMeasured(panel, "the opened link panel");
                Assert.Equal(group.Id, t.Editor.LinkHighlight.SelectedGroupId);
            });
        }, CancellationToken.None);

    [Fact]
    public async Task LinkBannerView_shows_for_a_linked_selection_only() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (t, _) = Linked();
            var banner = new LinkBannerView { DataContext = t.Vm };
            // Hosted as SoundInspectorView hosts it: the host owns the visibility.
            banner[!Visual.IsVisibleProperty] = new Avalonia.Data.Binding(nameof(t.Vm.IsBannerVisible));
            var host = new StackPanel { Width = 420, Children = { banner } };

            WithWindow(host, () =>
            {
                Assert.False(banner.IsVisible);

                t.Select(t.NoteIn(4));
                Layout(host);

                Assert.True(banner.IsVisible);
                AssertMeasured(banner, "LinkBannerView");
                AssertMeasured(banner.FindControl<Button>("LinkBannerOnlyThisOne")!, "Only this one");
                AssertMeasured(banner.FindControl<Button>("LinkBannerPanel")!, "Panel");
                Assert.True(banner.Bounds.Width <= 420.5, $"the banner is {banner.Bounds.Width:0.#} px in a 420 px pane");
            });
        }, CancellationToken.None);

    private static (LinkedMeasuresHarness Harness, LinkGroup Group) Linked()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));
        return (t, group);
    }

    private static void WithWindow(Control content, Action body)
    {
        var window = new Window { Width = 900, Height = 500, Content = content };
        window.Show();
        try
        {
            Layout(content);
            body();
        }
        finally
        {
            window.Close();
        }
    }

    private static void Layout(Control control)
    {
        control.Measure(new Avalonia.Size(900, 500));
        control.Arrange(new Avalonia.Rect(0, 0, 900, 500));
        control.UpdateLayout();
    }

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1, $"{what} laid out as {control.Bounds} — it rendered nothing");
}
