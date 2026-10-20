using System.Text.RegularExpressions;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
/// #300: the toolbar's undo/redo history previews opened on <c>PointerEntered</c>
/// with no dwell, so sweeping the pointer across the toolbar opened and closed
/// two real popups per pass. A hover now arms <see cref="HoverDwell{T}" />; the
/// preview opens only if the pointer is still there when the dwell elapses.
/// </summary>
public sealed class HistoryPreviewDwellTests
{
    private readonly object _undo = new();
    private readonly object _redo = new();

    [Fact]
    public void A_sweep_across_both_buttons_opens_nothing()
    {
        var dwell = new HoverDwell<object>();
        Assert.False(dwell.Enter(_undo, aPopupIsOpen: false));
        dwell.Exit(_undo);
        Assert.False(dwell.Enter(_redo, aPopupIsOpen: false));
        dwell.Exit(_redo);

        Assert.Null(dwell.Elapsed());
    }

    [Fact]
    public void Resting_past_the_dwell_opens_the_button_under_the_pointer()
    {
        var dwell = new HoverDwell<object>();
        dwell.Enter(_undo, aPopupIsOpen: false);
        dwell.Exit(_undo);
        dwell.Enter(_redo, aPopupIsOpen: false);

        Assert.Same(_redo, dwell.Elapsed());
        Assert.Null(dwell.Elapsed());
    }

    [Fact]
    public void With_a_preview_open_moving_to_the_other_button_switches_at_once()
    {
        var dwell = new HoverDwell<object>();
        Assert.True(dwell.Enter(_redo, aPopupIsOpen: true));
        Assert.Null(dwell.Pending);
    }

    [Fact]
    public void Leaving_another_button_does_not_cancel_the_pending_one()
    {
        var dwell = new HoverDwell<object>();
        dwell.Enter(_redo, aPopupIsOpen: false);
        dwell.Exit(_undo);
        Assert.Same(_redo, dwell.Elapsed());
    }

    /// <summary>The toolbar goes through the dwell: its hover handler never shows a flyout itself.</summary>
    [Fact]
    public void The_toolbar_hover_handler_arms_the_dwell_instead_of_opening()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell",
            "ToolbarView.axaml.cs"));
        var body = Regex.Match(src,
            @"void HistoryButton_PointerEntered\(.*?\)\s*\{(?<b>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(body.Success, "HistoryButton_PointerEntered not found — the lint is vacuous");
        Assert.Contains("_historyDwell.Enter(", body.Groups["b"].Value);
        Assert.DoesNotContain("ShowAttachedFlyout", body.Groups["b"].Value);
    }
}
