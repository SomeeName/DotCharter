using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The timeline lane menu: the content of the <c>PrewarmableFlyout</c> that a
///     right-click on the main timeline's lane headers opens (<c>MainWindow.axaml</c>,
///     <c>TimelineGutterHost</c>). It edits the <see cref="RibbonVisibility" /> of the
///     track <see cref="Renderer" /> draws, and labels every lane that is ON but has no
///     row with the reason (<see cref="RibbonLaneRules.HiddenReason" />).
///     <para>
///         The flyout's content joins the visual tree only while it is open, so
///         attach = open: that is when it binds the ribbon and subscribes to it, and
///         detach (close) drops both — a kept subscription would root the menu from
///         an app-lifetime ribbon (<c>ViewSubscriptionLeakGuardTests</c>).
///     </para>
/// </summary>
public partial class TimelineLaneMenuView : UserControl
{
    public static readonly StyledProperty<ChartRenderer?> RendererProperty =
        AvaloniaProperty.Register<TimelineLaneMenuView, ChartRenderer?>(nameof(Renderer));

    /// <summary>The "auto-hidden: one tone" hint, set from the markup's localized string.</summary>
    public static readonly StyledProperty<string?> OneToneHintProperty =
        AvaloniaProperty.Register<TimelineLaneMenuView, string?>(nameof(OneToneHint));

    /// <summary>The "auto-hidden: empty" hint, set from the markup's localized string.</summary>
    public static readonly StyledProperty<string?> EmptyHintProperty =
        AvaloniaProperty.Register<TimelineLaneMenuView, string?>(nameof(EmptyHint));

    private RibbonVisibility? _ribbon;

    public TimelineLaneMenuView() => InitializeComponent();

    /// <summary>The timeline whose lanes this menu edits.</summary>
    public ChartRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    public string? OneToneHint
    {
        get => GetValue(OneToneHintProperty);
        set => SetValue(OneToneHintProperty, value);
    }

    public string? EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(Renderer?.CurrentRibbon);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Bind(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Points the menu at <paramref name="ribbon" /> (null: unbind).</summary>
    internal void Bind(RibbonVisibility? ribbon)
    {
        _ribbon?.PropertyChanged -= OnRibbonChanged;
        _ribbon = ribbon;
        _ribbon?.PropertyChanged += OnRibbonChanged;
        DataContext = ribbon;
        RefreshHints();
    }

    private void OnRibbonChanged(object? sender, PropertyChangedEventArgs e) => RefreshHints();

    /// <summary>Writes each lane's "auto-hidden: …" hint from the same rules the
    /// renderer lays the ribbon out with.</summary>
    internal void RefreshHints()
    {
        var content = Renderer?.CurrentRibbonContent();
        Hint(LaneTones, RibbonLanes.Tones, content);
        Hint(LaneSections, RibbonLanes.Sections, content);
        Hint(LanePhrases, RibbonLanes.Phrases, content);
        Hint(LaneLyrics, RibbonLanes.Lyrics, content);
        Hint(LaneLinks, RibbonLanes.Links, content);
        Hint(LaneMeasures, RibbonLanes.MeasureMetadata, content);
    }

    private void Hint(ToggleButton row, RibbonLanes lane, RibbonContent? content)
    {
        var reason = _ribbon is { } ribbon && content is { } c
            ? RibbonLaneRules.HiddenReason(lane, ribbon.Lanes(), ribbon.AutoHideEmpty, c)
            : RibbonLaneHiddenReason.Shown;
        row.Tag = reason switch
        {
            RibbonLaneHiddenReason.OneTone => OneToneHint,
            RibbonLaneHiddenReason.Empty => EmptyHint,
            _ => null
        };
        row.Classes.Set("AutoHidden", reason is RibbonLaneHiddenReason.OneTone or RibbonLaneHiddenReason.Empty);
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e) => _ribbon?.ShowAllLanes();
}
