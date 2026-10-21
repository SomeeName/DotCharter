using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.ViewModels.Editor.Linking;

namespace DotCharter.UI.Views.Editor.Linking;

/// <summary>
///     Places the link hover card next to the pointer and the toast at the top right of the
///     timeline, and dismisses the toast after <see cref="ToastSeconds" /> (not while the pointer
///     is on it). The card follows only <see cref="Timeline" />'s own hover: a chip hovered on a
///     stacked row is in another control's pixels. It also opens the link panel (its attached
///     flyout) for a chip click on any timeline. The shell binds it to
///     <c>ChartEditorViewModel.LinkedMeasures</c>.
/// </summary>
public partial class LinkOverlayView : UserControl
{
    public const double ToastSeconds = 6;

    public static readonly StyledProperty<object?> TimelineProperty =
        AvaloniaProperty.Register<LinkOverlayView, object?>(nameof(Timeline));

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(ToastSeconds) };
    private LinkedMeasuresViewModel? _vm;

    public LinkOverlayView()
    {
        InitializeComponent();
        LinkToast.SizeChanged += (_, _) => PlaceToast();
        LinkHoverCard.SizeChanged += (_, _) => PlaceHoverCard();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (!LinkToast.IsPointerOver) _vm?.DismissToast();
            else _toastTimer.Start();
        };
    }

    /// <summary>The timeline the hover card belongs to.</summary>
    public object? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as LinkedMeasuresViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as LinkedMeasuresViewModel);
    }

    private void Attach(LinkedMeasuresViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Highlight.Changed -= PlaceHoverCard;
            _vm.Highlight.PanelRequested -= OpenPanel;
        }

        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Highlight.Changed += PlaceHoverCard;
            _vm.Highlight.PanelRequested += OpenPanel;
        }
    }

    private void OpenPanel(LinkPanelRequest request)
    {
        if (_vm == null || FlyoutBase.GetAttachedFlyout(this) is not PopupFlyoutBase flyout) return;
        _vm.OpenPanel(request.GroupId, request.Arrangement, request.At);
        LinkPanel.DataContext = _vm.Panel;
        flyout.Closed -= OnPanelClosed;
        flyout.Closed += OnPanelClosed;
        if (request.Source is Control source) flyout.ShowAt(source, showAtPointer: true);
        else flyout.ShowAt(this);
    }

    private void OnPanelClosed(object? sender, EventArgs e) => _vm?.PanelClosed();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LinkedMeasuresViewModel.ToastSerial))
        {
            _toastTimer.Stop();
            _toastTimer.Start();
            Dispatcher.UIThread.Post(PlaceToast, DispatcherPriority.Render);
        }
        else if (e.PropertyName == nameof(LinkedMeasuresViewModel.IsHoverCardVisible))
        {
            PlaceHoverCard();
        }
    }

    private void PlaceHoverCard()
    {
        if (_vm == null) return;
        var highlight = _vm.Highlight;
        var mine = ReferenceEquals(highlight.HoverSource, Timeline);
        LinkHoverCard.Opacity = mine ? 1 : 0;
        if (!mine) return;
        var point = highlight.HoverPoint;
        var width = LinkHoverCard.Bounds.Width > 0 ? LinkHoverCard.Bounds.Width : 260;
        var left = Math.Min(point.X + 14, Math.Max(0, Bounds.Width - width - 4));
        Canvas.SetLeft(LinkHoverCard, left);
        Canvas.SetTop(LinkHoverCard, point.Y + 16);
    }

    private void PlaceToast()
    {
        var width = LinkToast.Bounds.Width > 0 ? LinkToast.Bounds.Width : 420;
        Canvas.SetLeft(LinkToast, Math.Max(4, Bounds.Width - width - 12));
        Canvas.SetTop(LinkToast, 6);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PlaceToast();
    }
}
