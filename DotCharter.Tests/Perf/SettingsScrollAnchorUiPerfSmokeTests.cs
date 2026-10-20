using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>The settings pane keeps its scroll position when a dropdown opens, and
///     still scrolls a focused row into view.</b> Two halves of one rule, and a
///     fix for either that breaks the other is the regression this class exists
///     to catch.
/// </summary>
/// <remarks>
///     <para>
///         Background: the owner reports that opening a ComboBox in Settings
///         scrolls the pane back to the top. The chain
///         guessed there was: default <c>BringIntoViewOnFocusChange=True</c> →
///         <c>ScrollViewer.OnGotFocus</c> aligns a child above the viewport to
///         the top → the ComboBox hands focus across top levels → a
///         <c>(0,0,0,0)</c> target sets <c>Offset.Y=0</c>. <b>The first three
///         links are real and measured below; the last one is not.</b>
///     </para>
///     <para>
///         What the discriminating experiment measured (2026-08-29, Avalonia
///         12.1.1, the then General tab attached and laid out at 960×700,
///         extent 960×3903, driven with real headless mouse input):
///         <list type="bullet">
///             <item>Offset 400 → open the dropdown → <b>400</b>. No jump.</item>
///             <item>… → close it → <b>400</b>. No jump.</item>
///             <item>… → force focus onto the shell's zero-size focus sink (what
///                 <c>MainWindow.AnchorFocusIfLost</c> does) → <b>400</b>. No jump.</item>
///             <item>Repeated at offset 2000 with the combo near the TOP of the
///                 viewport (so the popup opens above the current offset), at UI
///                 scale 1.0 and 1.25 through a <c>LayoutTransformControl</c>
///                 exactly like <c>MainWindow</c>'s <c>RootScaler</c>, with the
///                 window at screen y 0 and 300, and with 3 / 40 / 200-item lists
///                 and the selection at the far end so the popup has to scroll
///                 its own selection into view: <b>every one survived</b>.</item>
///         </list>
///     </para>
///     <para>
///         What it DID establish, and what the fix must respect:
///         <list type="bullet">
///             <item><b>The lever is on the SCROLLVIEWER, not on the focused
///                 control.</b> Focusing a combo above the viewport moved the
///                 offset 400 → 109 with defaults, 400 → 109 with
///                 <c>ScrollViewer.SetBringIntoViewOnFocusChange(combo, false)</c>,
///                 and 400 → <b>400</b> with the property false on the
///                 ScrollViewer. Avalonia's <c>ScrollViewer.OnGotFocus</c> reads
///                 the attached property on ITSELF, so opting a single control
///                 out does nothing.</item>
///             <item><b>The popup really does route through the settings
///                 ScrollViewer</b> — instrumenting the pane showed four
///                 <c>RequestBringIntoView</c> events with a <c>ComboBoxItem</c>
///                 target plus its <c>GotFocus</c> arriving there. That half of
///                 the guess is correct. It does no harm because
///                 <c>ScrollContentPresenter.BringDescendantIntoView</c> bails on
///                 a null <c>TransformToVisual</c>, and under
///                 <c>Avalonia.Headless</c> the popup is hosted in the window's
///                 own overlay layer (both roots report <c>TopLevelHost</c>), so
///                 the transform resolves to the popup's real position.</item>
///         </list>
///         <b>So the reproduction needs the desktop backend's separate
///         <c>PopupRoot</c>, which this harness cannot create.</b> These cases are
///         therefore REGRESSION PINS, not a failing repro: they hold the three
///         steps at their measured values so a fix cannot silently move them, and
///         <see cref="FocusingARowAboveTheViewport_StillScrollsItIntoView" />
///         fails the moment somebody reaches for the blanket
///         <c>BringIntoViewOnFocusChange="False"</c> the handover floated. That
///         one is proved non-vacuous by the 400 → 400 measurement above: it is
///         literally the failing state of this test.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class SettingsScrollAnchorUiPerfSmokeTests
{
    private const double PaneWidth = 960;
    private const double PaneHeight = 700;

    /// <summary>The LEAST the pane is parked at: far enough down that a reset to
    /// the top is unmistakable. The actual offset is <see cref="Pane.Parked" />,
    /// chosen so the first combo past this line sits well inside the viewport,
    /// because a combo straddling the viewport's edge is scrolled into view by
    /// the click itself, which is correct and not the jump pinned here.</summary>
    private const double ParkedOffset = 400;

    [Fact]
    public Task TheScrollOffset_SurvivesOpeningADropdown() =>
        WithSettingsPane(pane =>
        {
            var combo = pane.SelectedComboInView();
            pane.Click(combo);

            Assert.True(combo.IsDropDownOpen, "the click did not open the dropdown — the case measured nothing");
            AssertParked(pane, "opening the dropdown");
        });

    [Fact]
    public Task TheScrollOffset_SurvivesClosingADropdown() =>
        WithSettingsPane(pane =>
        {
            var combo = pane.SelectedComboInView();
            pane.Click(combo);
            pane.Click(combo);

            Assert.False(combo.IsDropDownOpen, "the second click did not close the dropdown");
            AssertParked(pane, "closing the dropdown");
        });

    /// <summary>
    ///     The third step: focus leaves the pane entirely and the shell re-anchors
    ///     it on its zero-size sink, which is <c>MainWindow.AnchorFocusIfLost</c>'s
    ///     whole body. A sink at the window origin with <c>Bounds</c> of zero is
    ///     the closest thing in the shell to the <c>(0,0,0,0)</c> target the
    ///     handover suspected, so it is worth holding explicitly.
    /// </summary>
    [Fact]
    public Task TheScrollOffset_SurvivesAForcedFocusLossAndReAnchor() =>
        WithSettingsPane(pane =>
        {
            pane.SelectedComboInView().Focus();
            pane.Window.UpdateLayout();

            pane.Sink.Focus();
            pane.Window.UpdateLayout();

            Assert.True(pane.Sink.IsFocused, "the sink never took focus — the case measured nothing");
            AssertParked(pane, "re-anchoring focus on the shell's focus sink");
        });

    /// <summary>
    ///     <b>The other half: Settings is a keyboard form.</b> Moving focus to a
    ///     row above the viewport must still scroll it into view, which is exactly
    ///     what a blanket <c>BringIntoViewOnFocusChange="False"</c> on the tab's
    ///     ScrollViewer would take away — the precedents for that attribute in
    ///     this repo are all LISTS, where there is no row to reach by Tab.
    ///     Measured: 400 → 109 with the property left alone, 400 → 400 with it
    ///     false, so this case IS the difference between the two.
    /// </summary>
    [Fact]
    public Task FocusingARowAboveTheViewport_StillScrollsItIntoView() =>
        WithSettingsPane(pane =>
        {
            var above = pane.Inputs.First(c => pane.ContentY(c) > 0 && pane.ContentY(c) < pane.Parked);
            var wanted = pane.ContentY(above);

            pane.Sink.Focus();
            above.Focus();
            pane.Window.UpdateLayout();

            Assert.True(pane.Scroll.Offset.Y < pane.Parked,
                $"focus moved to a row at content y {wanted:0} while the pane sat at " +
                $"{pane.Parked:0}, and the offset stayed {pane.Scroll.Offset.Y:0}. The row is above the " +
                "viewport, so Tab navigation has just gone blind — this is what a blanket " +
                "BringIntoViewOnFocusChange=\"False\" on a settings ScrollViewer costs.");
            // …and it landed ON the row rather than somewhere arbitrary. The
            // control's own top can end a few px above the viewport (the pane's
            // 8 px ScrollViewer padding is inside the presenter), so the claim is
            // that the row now INTERSECTS the viewport, not that it is flush.
            var topInViewport = wanted - pane.Scroll.Offset.Y;
            Assert.True(topInViewport > -above.Bounds.Height && topInViewport < pane.Scroll.Viewport.Height,
                $"the pane scrolled to {pane.Scroll.Offset.Y:0} but the row sits at {wanted:0}, i.e. " +
                $"{topInViewport:0} px into a {pane.Scroll.Viewport.Height:0} px viewport — it is not on " +
                "screen, so the scroll went somewhere unrelated to the focused row.");
        });

    /// <summary>
    ///     The mechanism finding, held so an Avalonia upgrade that changes it is
    ///     visible rather than mysterious: a ComboBox popup's focus and its
    ///     bring-into-view requests DO travel out of the popup and through the
    ///     settings ScrollViewer. (A popup root's interactive parent is its
    ///     LOGICAL parent, so a popup is never routing-isolated from the control
    ///     that owns it.) That is the link in the handover's chain that is real.
    /// </summary>
    [Fact]
    public Task ThePopupsOwnFocusAndBringIntoView_ReachTheSettingsScrollViewer() =>
        WithSettingsPane(pane =>
        {
            var seenFocus = new List<string>();
            var seenBring = new List<string>();
            pane.Scroll.AddHandler(InputElement.GotFocusEvent,
                (_, e) => seenFocus.Add(e.Source?.GetType().Name ?? "<null>"),
                RoutingStrategies.Bubble, handledEventsToo: true);
            pane.Scroll.AddHandler(Control.RequestBringIntoViewEvent,
                (_, e) => seenBring.Add(e.TargetObject?.GetType().Name ?? "<null>"),
                RoutingStrategies.Bubble, handledEventsToo: true);

            pane.Click(pane.SelectedComboInView());

            Assert.Contains(nameof(ComboBoxItem), seenFocus);
            Assert.Contains(nameof(ComboBoxItem), seenBring);
        });

    // ── harness ─────────────────────────────────────────────────────────────

    private static void AssertParked(Pane pane, string what)
    {
        Assert.True(Math.Abs(pane.Scroll.Offset.Y - pane.Parked) < 0.5,
            $"the settings pane sat at y {pane.Parked:0} and {what} moved it to " +
            $"{pane.Scroll.Offset.Y:0}. The owner reads that as the pane jumping back to the top " +
            "(docs/agents/ui/settings.md). Fix it WITHOUT a blanket BringIntoViewOnFocusChange=\"False\" — see " +
            nameof(FocusingARowAboveTheViewport_StillScrollsItIntoView) + ".");
    }

    /// <summary>
    ///     One settings tab, attached, laid out and parked at
    ///     <see cref="ParkedOffset" /> — plus the shell's zero-size focus sink as
    ///     a sibling, because <c>MainWindow</c> really does keep one and it is the
    ///     only zero-bounds focus target in the shell.
    /// </summary>
    private sealed record Pane(Window Window, Control Sink, ScrollViewer Scroll, Visual Content, double Parked)
    {
        public List<ComboBox> Combos { get; } = [];

        /// <summary>Every focusable row control, for the Tab-navigation half: a row is a row whatever it holds.</summary>
        public List<Control> Inputs { get; } = [];

        public double ContentY(Visual v) => v.TranslatePoint(default, Content)!.Value.Y;

        /// <summary>
        ///     A ComboBox that is on screen at the parked offset, WITH a selection.
        ///     The selection is what makes the case real: laid out without a
        ///     DataContext every settings ComboBox has <c>SelectedIndex</c> -1, and
        ///     <c>ComboBox.TryFocusSelectedItem()</c> then hands focus to nobody —
        ///     so the popup never takes focus and the case silently measures the
        ///     wrong thing. In the app every one of these rows is bound and
        ///     selected.
        /// </summary>
        public ComboBox SelectedComboInView()
        {
            var bottom = Parked + Scroll.Viewport.Height - 20;
            var combo = Combos.First(c => ContentY(c) > Parked + 20 && ContentY(c) + c.Bounds.Height < bottom);
            Assert.True(combo.ItemCount > 1,
                "the chosen settings ComboBox has no items to select — the popup would take no focus");
            combo.SelectedIndex = 1;
            Window.UpdateLayout();
            return combo;
        }

        public void Click(Visual target)
        {
            var pt = target.TranslatePoint(new Point(10, 10), Window)!.Value;
            Window.MouseDown(pt, MouseButton.Left);
            Window.MouseUp(pt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }
    }

    private static async Task WithSettingsPane(Action<Pane> assert)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // Settings > Editor, the longest tab, on a fake that selects its dropdown's row.
            var tab = new EditorSettingsView { DataContext = FakeEditorSettingsScreen.Default() };
            var sink = new Control
            {
                Focusable = true,
                Width = 0,
                Height = 0,
                IsTabStop = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            var window = new Window
            {
                Width = PaneWidth,
                Height = PaneHeight,
                Content = new Grid { Children = { sink, tab } }
            };

            window.Show();
            try
            {
                window.UpdateLayout();

                var scroll = tab.GetLogicalDescendants().OfType<ScrollViewer>().First();
                var content = scroll.GetVisualDescendants()
                    .OfType<ScrollContentPresenter>().First().Child!;

                Assert.True(scroll.Extent.Height > scroll.Viewport.Height + ParkedOffset,
                    $"the tab is only {scroll.Extent.Height:0} px tall in a {scroll.Viewport.Height:0} px " +
                    $"viewport, so it cannot be parked at y {ParkedOffset:0} — every case here would assert " +
                    "against a pane that has nowhere to scroll.");

                var combos = tab.GetLogicalDescendants().OfType<ComboBox>().ToList();
                double Y(Visual v) => v.TranslatePoint(default, content)!.Value.Y;
                var target = combos.Where(c => Y(c) >= ParkedOffset).Min(Y);
                var parked = Math.Max(ParkedOffset, target - scroll.Viewport.Height / 3);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height + parked,
                    $"the pane cannot be parked at y {parked:0} in a {scroll.Extent.Height:0} px extent.");

                scroll.Offset = new Vector(0, parked);
                window.UpdateLayout();

                var pane = new Pane(window, sink, scroll, content, parked);
                pane.Combos.AddRange(combos);
                pane.Inputs.AddRange(tab.GetLogicalDescendants().OfType<Control>()
                    .Where(c => c is ComboBox or ToggleSwitch or NumericUpDown or TextBox && c.Focusable));
                assert(pane);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
