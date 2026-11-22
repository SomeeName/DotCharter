using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Tests.Presentation.Editor;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The virtual Shapes list is a plain list of cards to the keyboard, the styles and a screen
///     reader: it takes no key, Tab walks every card, and a strip of cards is nothing by itself.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesPanelKeyboardUiPerfSmokeTests
{
    /// <summary>The editor's bare-key shortcuts (seek and nudge keys) plus the keys a selecting list would take.</summary>
    private static readonly PhysicalKey[] EditorKeys =
    [
        PhysicalKey.Home, PhysicalKey.End, PhysicalKey.ArrowUp, PhysicalKey.ArrowDown, PhysicalKey.ArrowLeft,
        PhysicalKey.ArrowRight, PhysicalKey.PageUp, PhysicalKey.PageDown, PhysicalKey.Space, PhysicalKey.Enter,
        PhysicalKey.A, PhysicalKey.S
    ];

    /// <summary>Keys that press a focused button: they are sent to the card only.</summary>
    private static readonly PhysicalKey[] ButtonKeys = [PhysicalKey.Space, PhysicalKey.Enter];

    [Fact]
    public Task AKeyPressedOnACardPassesTheListUnhandledAndMovesNothing() => WithPanel((rig, view, window) =>
    {
        var (atSource, pastList, atWindow) = (false, false, false);
        var watched = new HashSet<Interactive>();
        void Watch(Interactive at, Action<bool> seen)
        {
            if (!watched.Add(at)) return;
            at.AddHandler(InputElement.KeyDownEvent, (_, e) => seen(e.Handled), RoutingStrategies.Bubble, true);
            at.AddHandler(InputElement.TextInputEvent, (_, e) => seen(e.Handled), RoutingStrategies.Bubble, true);
        }

        Watch(Assert.IsAssignableFrom<Interactive>(view.RowsList.GetVisualParent()), handled => pastList = handled);
        Watch(window, handled => atWindow = handled);
        var offenders = new List<string>();
        if (view.RowsList.GetType().IsAssignableTo(typeof(SelectingItemsControl))) offenders.Add("the list has a selection for keys and clicks to move");
        foreach (var name in new[] { "ShapeCard", "ShapeTick" })
        {
            void Check(string what, Action press)
            {
                // Looked up per key: a list that takes the key scrolls, and the card's visuals then show another row.
                var card = ShapesPanelCards.Card(view, rig.Screen.Rows[8]);
                var source = name == "ShapeCard" ? card : card.GetVisualDescendants().OfType<Control>().Single(b => b.Name == name);
                Watch(source, handled => atSource = handled);
                Assert.True(source.Focus(), $"{name} did not take the focus");
                ShapesPanelCards.Settle(view);
                var offset = view.Scroll.Offset;
                (atSource, pastList, atWindow) = (false, false, false);
                press();
                ShapesPanelCards.Settle(view);
                if (pastList && !atSource) offenders.Add($"{name}: {what} was handled by the list");
                if (atWindow && !pastList && !ScrollsThePane(what)) offenders.Add($"{name}: {what} was handled above the list");
                if (!ReferenceEquals(source, window.FocusManager?.GetFocusedElement())) offenders.Add($"{name}: {what} moved the focus");
                if (!ScrollsThePane(what) && view.Scroll.Offset != offset) offenders.Add($"{name}: {what} scrolled the list");
            }

            foreach (var key in EditorKeys.Where(k => name == "ShapeCard" || !ButtonKeys.Contains(k)))
                Check(key.ToString(), () => window.KeyPressQwerty(key, RawInputModifiers.None));
            Check("typed text", () => window.KeyTextInput("a"));
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    });

    /// <summary>The pane's scroll viewer pages on these, with or without a virtual list inside it.</summary>
    private static bool ScrollsThePane(string key) => key is nameof(PhysicalKey.PageUp) or nameof(PhysicalKey.PageDown);

    [Fact]
    public Task TheShapeCardsNameBoxKeepsItsOwnKeys() => WithPanel((rig, view, window) =>
    {
        rig.Screen.CurrentRow = rig.Screen.Rows[8];
        view.Cards.ShowPage(view.FindControl<Control>("ShapesShapeCard")!);
        ShapesPanelCards.Settle(view);
        var box = view.FindControl<TextBox>("ShapeName")!;
        Assert.True(box.Focus());
        box.Text = "Shape";
        box.CaretIndex = 5;

        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(0, box.CaretIndex);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(1, box.CaretIndex);
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(5, box.CaretIndex);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(4, box.CaretIndex);
        window.KeyTextInput("x");

        Assert.Equal("Shapxe", box.Text);
        Assert.Same(box, window.FocusManager?.GetFocusedElement());
    });

    [Fact]
    public Task TabWalksEveryCardInOrderAndShiftTabWalksBack() => WithPanel((rig, view, window) =>
    {
        var rows = rig.Screen.Rows.ToList();
        Assert.True(ShapesPanelCards.Realized(view).Count() < rows.Count, "every card is realized: the walk proves nothing");
        Assert.True(view.FindControl<TextBox>("ShapesSearch")!.Focus());

        var forward = Walk(view, window, RawInputModifiers.None, rows.Count);

        Assert.True(rows.SequenceEqual(forward), $"Tab went through {forward.Count} of {rows.Count} cards, or out of order");
        // The walk ended one Tab past the last card: step back in and go up.
        var back = Walk(view, window, RawInputModifiers.Shift, rows.Count);
        Assert.True(rows.AsEnumerable().Reverse().SequenceEqual(back), $"Shift+Tab went through {back.Count} of {rows.Count} cards, or out of order");
    });

    /// <summary>Presses Tab until the focus has been through the cards and left them; the rows of the cards it visited, in order.</summary>
    private static List<IShapePanelRow> Walk(ShapesPanelView view, Window window, RawInputModifiers modifiers, int rowCount)
    {
        var visited = new List<IShapePanelRow>();
        for (var press = 0; press < rowCount * 16 + 16; press++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, modifiers);
            ShapesPanelCards.Settle(view);
            var row = (window.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
                .FirstOrDefault(b => b.Name == "ShapeCard")?.DataContext as IShapePanelRow;
            if (row is null && visited.Count > 0) return visited;
            if (row is not null && !ReferenceEquals(row, visited.LastOrDefault())) visited.Add(row);
        }

        Assert.Fail($"the focus never left the cards ({visited.Count} visited)");
        return visited;
    }

    [Fact]
    public Task AStripOfCardsHasNoChromeGapOrColourOfItsOwn() => WithPanel((_, view, _) =>
    {
        var strips = view.RowsList.GetRealizedContainers().OrderBy(c => c.Bounds.Y).ToList();
        Assert.True(strips.Count >= 3, $"{strips.Count} strips in view");

        Assert.All(strips, strip =>
        {
            Assert.Equal(default, strip.Margin);
            Assert.False(strip.IsSet(TextElement.ForegroundProperty), "a strip sets its own text colour");
            Assert.False(strip.Focusable);
        });
        for (var i = 1; i < strips.Count; i++)
            Assert.Equal(strips[i - 1].Bounds.Bottom, strips[i].Bounds.Y, 0.01);
        Assert.DoesNotContain(view.GetVisualDescendants(), v => v is ListBoxItem);
    });

    [Fact]
    public Task AScreenReaderHearsTheListAndItsCardsButNoStrip() => WithPanel((rig, view, _) =>
    {
        var list = ControlAutomationPeer.CreatePeerForElement(view.RowsList);
        var peers = new List<AutomationPeer>();
        void Collect(AutomationPeer peer)
        {
            foreach (var child in peer.GetChildren())
            {
                peers.Add(child);
                Collect(child);
            }
        }

        Collect(list);
        Assert.False(string.IsNullOrWhiteSpace(list.GetName()));
        Assert.Contains(peers, p => NameOf(p) == rig.Screen.Rows[0].FretsText);
        var strips = peers.Where(p => p.GetAutomationControlType() == AutomationControlType.ListItem
                                      || NameOf(p).Contains("Strip", StringComparison.Ordinal)
                                      || NameOf(p).Contains("DotCharter.", StringComparison.Ordinal)).ToList();
        Assert.True(strips.Count == 0, "announced: " + string.Join(", ", strips.Select(p => $"{p.GetClassName()} \"{NameOf(p)}\"")));
    });

    private static string NameOf(AutomationPeer peer) => peer.GetName() ?? string.Empty;

    private static Task WithPanel(Action<ShapesSwitchRig, ShapesPanelView, Window> check) => HeadlessSessionFixture.Dispatch(() =>
    {
        var rig = new ShapesSwitchRig();
        var view = new ShapesPanelView { DataContext = rig.Screen };
        var window = NarrowWidthLayout.NewWindow(600, 524, view);
        window.Show();
        ShapesPanelCards.Settle(view);
        try { check(rig, view, window); }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);
}
