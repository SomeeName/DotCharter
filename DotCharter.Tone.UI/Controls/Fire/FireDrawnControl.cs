using Avalonia;
using Avalonia.Controls;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The plumbing every custom-drawn Fire control repeated: the ONE counted
///     invalidation path (<see cref="Repaint" /> / <see cref="RepaintRequests" />)
///     and a <see cref="FirePaintCache" /> that is dropped on attach and detach
///     and, for a control constructed with <c>followsThemeVariant: true</c>, on
///     every <c>ActualThemeVariantChanged</c> — a subscription taken on attach
///     and released on detach.
///     <para>
///         Drawing, measuring, input and automation stay in the derived control;
///         this type owns no styled property and draws nothing.
///     </para>
/// </summary>
public abstract class FireDrawnControl : Control
{
    private readonly bool _followsThemeVariant;
    private FirePaintCache? _paint;

    protected FireDrawnControl()
        : this(followsThemeVariant: false)
    {
    }

    protected FireDrawnControl(bool followsThemeVariant) => _followsThemeVariant = followsThemeVariant;

    /// <summary>Repaint requests this control has made. Test seam for the
    /// "invalidate only when the rendered state changed" rule.</summary>
    internal int RepaintRequests { get; private set; }

    /// <summary>Whether this control is currently subscribed to
    /// <c>ActualThemeVariantChanged</c> — the test seam for the attach / detach
    /// pairing.</summary>
    internal bool IsFollowingThemeVariant { get; private set; }

    /// <summary>Colours by token, brushes and pens by colour, remembered across
    /// paints. Created on first use, so a control that never draws through it
    /// carries none.</summary>
    private protected FirePaintCache Paint => _paint ??= new FirePaintCache();

    /// <summary>The ONE invalidation path, counted so the repaint gate is
    /// testable.</summary>
    protected void Repaint()
    {
        RepaintRequests++;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The tree above decides what a token resolves to; start each host clean.
        _paint?.Clear();
        if (_followsThemeVariant && !IsFollowingThemeVariant)
        {
            ActualThemeVariantChanged += OnThemeVariantChanged;
            IsFollowingThemeVariant = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (IsFollowingThemeVariant)
        {
            ActualThemeVariantChanged -= OnThemeVariantChanged;
            IsFollowingThemeVariant = false;
        }

        base.OnDetachedFromVisualTree(e);
        _paint?.Clear();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        _paint?.Clear();
        Repaint();
    }
}
