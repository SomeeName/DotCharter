using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The owner's "clicking a dropdown in Settings scrolls the pane to the
///     top" (reported 2026-08-03, diagnosed 2026-08-28, never
///     reproduced). This is the discriminating experiment that diagnosis asked
///     for: a scrolled settings tab, then each suspected trigger on its own —
///     the dropdown opening, the dropdown closing, focus leaving for a
///     zero-size sink (what <c>MainWindow.AnchorFocusIfLost</c> does) — and
///     the offset must survive each.
///     <para>
///         Since 0.49.3 popups are in-window (<c>OverlayPopups</c> on), which is
///         exactly the host <c>Avalonia.Headless</c> falls back to, so what this
///         measures is the product's own path now, not a stand-in.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SettingsScrollOnDropdownUiPerfSmokeTests
{
    /// <summary>The offset the pane is parked at: the ComboBox sits 40 px
    /// below the viewport's top, i.e. fully VISIBLE — the owner clicked a
    /// dropdown he could see. A bring-into-view of a visible child is a no-op
    /// by contract, so any movement from here is the bug.</summary>
    private static double _scrolledTo;

    private static (Window Window, ScrollViewer Scroller, ComboBox Combo, Control Sink) Build()
    {
        // Settings > Editor: the longest tab, with its dropdown below the fold.
        var view = new EditorSettingsView { DataContext = FakeEditorSettingsScreen.Default() };
        var sink = new Control { Focusable = true, Width = 0, Height = 0 };
        var root = new Grid { Children = { view, sink } };
        var window = new Window { Width = 1100, Height = 420, Content = root };
        window.Show();
        window.UpdateLayout();

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        var combo = scroller.GetVisualDescendants().OfType<ComboBox>().First();

        var comboTopInContent = combo.TranslatePoint(new Point(0, 0), scroller)!.Value.Y + scroller.Offset.Y;
        var maxOffset = scroller.Extent.Height - scroller.Viewport.Height;
        _scrolledTo = Math.Min(Math.Max(0, comboTopInContent - 40), maxOffset);
        Assert.True(_scrolledTo > 10,
            $"the first ComboBox must sit below the fold for this to mean anything " +
            $"(combo at {comboTopInContent}, max offset {maxOffset})");
        scroller.Offset = new Vector(0, _scrolledTo);
        window.UpdateLayout();
        Assert.Equal(_scrolledTo, scroller.Offset.Y);
        return (window, scroller, combo, sink);
    }

    private static void AssertStillScrolled(Window window, ScrollViewer scroller, string after)
    {
        window.UpdateLayout();
        Assert.True(Math.Abs(scroller.Offset.Y - _scrolledTo) < 0.5,
            $"the settings pane scrolled from {_scrolledTo} to {scroller.Offset.Y} after {after}");
    }

    [Fact]
    public async Task OpeningTheDropdown_DoesNotScrollThePane()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, scroller, combo, _) = Build();
            try
            {
                combo.Focus();
                AssertStillScrolled(window, scroller, "focusing the ComboBox");
                combo.IsDropDownOpen = true;
                AssertStillScrolled(window, scroller, "opening the dropdown");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosingTheDropdown_DoesNotScrollThePane()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, scroller, combo, _) = Build();
            try
            {
                combo.Focus();
                combo.IsDropDownOpen = true;
                window.UpdateLayout();
                combo.IsDropDownOpen = false;
                AssertStillScrolled(window, scroller, "closing the dropdown");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FocusLeavingForTheZeroSizeSink_DoesNotScrollThePane()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, scroller, combo, sink) = Build();
            try
            {
                combo.Focus();
                combo.IsDropDownOpen = true;
                window.UpdateLayout();
                combo.IsDropDownOpen = false;
                window.UpdateLayout();
                // What AnchorFocusIfLost does after every pointer release and
                // 4x/s from the focus guard: focus the zero-size sink.
                Assert.True(sink.Focus(), "the sink did not take focus");
                AssertStillScrolled(window, scroller, "focus moving to the zero-size sink");
                // …and back to the combo, as a click on it would.
                combo.Focus(NavigationMethod.Pointer);
                AssertStillScrolled(window, scroller, "focus returning to the ComboBox by pointer");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
