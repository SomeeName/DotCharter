using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;

namespace DotCharter.UI.Controls;

/// <summary>
/// One card of a <see cref="ColumnFlowPanel" /> (inspector design D): a framed surface whose content scrolls
///     inside the card's own slot. While more of it is below the fold, a "More below" cue sits at the card's bottom
///     edge and the scroller stops above it, so the cue never covers the last visible row. The frame and the cue are
///     its template (<c>Styles/ColumnFlowCardTheme.axaml</c>, app-wide); this class only follows the scroller. At the
///     Tiny tier the panel's pager row stands in for the card's title row and carries the cue
///     (<see cref="CueHosted" />), so the scroller keeps the card's whole height.
/// </summary>
[TemplatePart(ScrollPart, typeof(ScrollViewer))]
[TemplatePart(CuePart, typeof(Control))]
public sealed class ColumnFlowCard : ContentControl
{
    public const string ScrollPart = "PART_Scroll";
    public const string CuePart = "PART_Cue";

    /// <summary>How much content may hide under the fold before the cue shows (px).</summary>
    private const double Slack = 2;

    private ScrollViewer? _scroll;
    private Control? _cue;
    private bool _cueHosted;

    /// <summary>Whether the "More below" cue shows — the test hook.</summary>
    internal bool ShowsCue => _cue?.IsVisible == true;

    /// <summary>The card's scroller, once templated — the test hook.</summary>
    internal ScrollViewer? Scroller => _scroll;

    /// <summary>Content hides under the fold now.</summary>
    internal bool HasMoreBelow { get; private set; }

    /// <summary>The panel shows this card's cue in its pager row: the card draws none and its scroller keeps the
    /// card's whole height.</summary>
    internal bool CueHosted
    {
        get => _cueHosted;
        set
        {
            if (_cueHosted == value) return;
            _cueHosted = value;
            Follow();
        }
    }

    /// <summary><see cref="HasMoreBelow" /> changed.</summary>
    internal event EventHandler? MoreBelowChanged;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _scroll?.ScrollChanged -= OnScrollChanged;
        _scroll = e.NameScope.Find<ScrollViewer>(ScrollPart);
        _cue = e.NameScope.Find<Control>(CuePart);
        _scroll?.ScrollChanged += OnScrollChanged;
        Follow();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => Follow();

    private void Follow()
    {
        if (_scroll is not { } scroll || _cue is not { } cue) return;
        var below = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y > Slack;
        if (HasMoreBelow != below)
        {
            HasMoreBelow = below;
            MoreBelowChanged?.Invoke(this, EventArgs.Empty);
        }

        var drawn = below && !_cueHosted;
        if (cue.IsVisible == drawn) return;
        cue.IsVisible = drawn;
        scroll.Margin = drawn ? new Thickness(0, 0, 0, cue.Height) : default;
    }
}
