using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The song lists' `L R B V` arrangement-chip cluster, drawn as ONE control.
///     <para>
///         Building this as an <c>ItemsControl</c> with a
///         horizontal panel and a `Border.FireTag.Square` + <c>TextBlock</c> per
///         chip would cost ~8 elements per row, rebuilt whenever a recycled row re-binds
///         (the <c>ItemsControl</c> compares <c>ItemsSource</c> by reference, and
///         a different song's chip list is a different instance). On a filter
///         Reset that is every realized row, and the pass cost is DIFFUSE
///         element count, not one hot binding — see
///         <c>docs/agents/ui/song-browser.md</c>. One drawn control makes a
///         rebind an <c>InvalidateMeasure</c> and realization a single element.
///     </para>
///     <para>
///         Look-parity with the class-built tag is a CONTRACT: the geometry
///         constants mirror the `FireTag Square` tokens (pinned by
///         <c>FireChipStripTests</c>) and every colour resolves from the SAME
///         dictionary keys the class styles use (`FireBorderStrong` /
///         `FireTextMuted`, accent `FireAccentBorderFaint` / `FireAccentBright`)
///         through <see cref="FireUnitState.ColorFrom" /> — no literal colour, so
///         a missing token renders visibly grey rather than plausibly right.
///     </para>
///     <para>
///         The per-chip tooltips (the full path words) fold into the HOST's one
///         <c>ToolTip.Tip</c> + <c>AutomationProperties.Name</c> — set both at
///         the call site from <c>ArrangementSummary</c>.
///     </para>
/// </summary>
public sealed class FireChipStrip : FireDrawnControl
{
    /// <summary>One chip per entry, printing that entry (an initial like "L").
    /// Reference-stable per row by contract — the item's projection is computed
    /// once (see <c>PsarcSongListItem</c> / <c>PlaySongItem</c>).</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> InitialsProperty =
        AvaloniaProperty.Register<FireChipStrip, IReadOnlyList<string>?>(nameof(Initials));

    /// <summary>Accent identity (Play Mode's library rows) vs the base muted tag
    /// (the editor browser's rows).</summary>
    public static readonly StyledProperty<bool> AccentProperty =
        AvaloniaProperty.Register<FireChipStrip, bool>(nameof(Accent));

    public static readonly StyledProperty<double> ChipSizeProperty =
        AvaloniaProperty.Register<FireChipStrip, double>(
            nameof(ChipSize), FireChipStripGeometry.DefaultChipSize);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<FireChipStrip, double>(
            nameof(Gap), FireChipStripGeometry.DefaultGap);

    public IReadOnlyList<string>? Initials
    {
        get => GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    public bool Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double ChipSize { get => GetValue(ChipSizeProperty); set => SetValue(ChipSizeProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
        => new(FireChipStripGeometry.RowWidth(Initials?.Count ?? 0, ChipSize, Gap), ChipSize);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var initials = Initials;
        if (initials is not { Count: > 0 }) return;

        var accent = Accent;
        var opacity = FireUnitState.OpacityFor(this);
        var border = FireUnitState.ColorFrom(this, accent ? "FireAccentBorderFaint" : "FireBorderStrong");
        var ink = FireUnitState.ColorFrom(this, accent ? "FireAccentBright" : "FireTextMuted");
        var pen = new Pen(new SolidColorBrush(FireUnitState.At(border, opacity * (border.A / 255.0))));
        var inkBrush = new SolidColorBrush(FireUnitState.At(ink, opacity * (ink.A / 255.0)));
        var typeface = this.TryFindResource("FireFontMono", out var font) && font is FontFamily family
            ? new Typeface(family)
            : Typeface.Default;

        for (var i = 0; i < initials.Count; i++)
        {
            var rect = FireChipStripGeometry.ChipRect(i, ChipSize, Gap);
            // Stroke on the half-pixel so the 1 px border is crisp, the same
            // place a Border's edge lands.
            context.DrawRectangle(null, pen, rect.Deflate(0.5),
                FireChipStripGeometry.CornerRadius, FireChipStripGeometry.CornerRadius);

            var text = new FormattedText(initials[i], CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, FireChipStripGeometry.FontSize, inkBrush);
            context.DrawText(text, new Point(
                rect.X + (rect.Width - text.Width) / 2,
                rect.Y + (rect.Height - text.Height) / 2));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var p = change.Property;
        if (p == InitialsProperty || p == ChipSizeProperty || p == GapProperty)
        {
            // A different chip COUNT changes the width; measure covers both.
            InvalidateMeasure();
            Repaint();
        }
        else if (p == AccentProperty || p == FireUnitState.IsBypassedProperty ||
                 p == IsEffectivelyEnabledProperty)
        {
            Repaint();
        }
    }
}
