using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.Controls;

/// <summary>
///     Tiny practice progress sparkline: session accuracy (0..100%) over the
///     stored history of the current song + arrangement, so improvement
///     across sessions is visible at a glance. Dot brightness encodes the
///     playback speed of the session (dim = slowed practice, full = 100%).
/// </summary>
public sealed class PracticeProgressGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<PracticeSessionRecord>?> SessionsProperty =
        AvaloniaProperty.Register<PracticeProgressGraph, IReadOnlyList<PracticeSessionRecord>?>(nameof(Sessions));

    /// <summary>Bumped by the view model when the profile changes.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<PracticeProgressGraph, int>(nameof(Version));

    private const int MaxPoints = 40;

    // The two colours are RESOLVED, not baked: a pinned hex copy would read as a
    // different product's chart as soon as the app palette changed. `FireSuccess` is
    // the semantic ("this is the good signal") and `FireBorderStrong` is the role (a
    // divider hairline), so the sparkline moves with the palette. A missing token
    // renders visibly grey rather than plausibly right — same rule as
    // `FireUnitState.ColorFrom`.
    private static Color Resolve(Control control, string key, Color fallback)
        => control.TryFindResource(key, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : fallback;

    public IReadOnlyList<PracticeSessionRecord>? Sessions
    {
        get => GetValue(SessionsProperty);
        set => SetValue(SessionsProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionsProperty || change.Property == VersionProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var sessions = Sessions;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (sessions == null || sessions.Count < 2 || w <= 4 || h <= 4) return;

        // Last N sessions, oldest first.
        var start = Math.Max(0, sessions.Count - MaxPoints);
        var count = sessions.Count - start;

        var line = Resolve(this, "FireSuccess", Colors.Gray);
        var baseline = Resolve(this, "FireBorderStrong", Colors.Gray);
        var linePen = new ImmutablePen(new ImmutableSolidColorBrush(line), 1.5);

        context.FillRectangle(new ImmutableSolidColorBrush(baseline), new Rect(0, h - 1, w, 1));

        Point? previous = null;
        for (var i = 0; i < count; i++)
        {
            var record = sessions[start + i];
            var x = count == 1 ? w / 2 : i / (double)(count - 1) * (w - 4) + 2;
            var y = h - 2 - Math.Clamp(record.Accuracy, 0, 1) * (h - 4);
            var point = new Point(x, y);
            if (previous is { } prev) context.DrawLine(linePen, prev, point);
            previous = point;

            // Speed encodes as dot alpha: slowed practice reads dimmer.
            var alpha = 0.35 + 0.65 * Math.Clamp(record.SpeedPercent / 100.0, 0, 1);
            var dot = new ImmutableSolidColorBrush(line, alpha);
            context.DrawEllipse(dot, null, point, 1.8, 1.8);
        }
    }
}
