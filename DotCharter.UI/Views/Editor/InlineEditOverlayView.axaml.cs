using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The in-place editors over the timeline: fret-hand position, section and lyric text.
///     They are in-window cards on a <see cref="Canvas" />, never a popup,
///     hung at a song time through <see cref="InlineEditPlacement" /> so a panned view keeps them on their
///     row. Enter commits, Escape cancels, and focus leaving the card (a click elsewhere) cancels: nothing
///     is written that the user did not confirm. The cards are hidden until a tool opens one, so the shell
///     prewarms them (<see cref="IPrewarmableSurfaces" />).
/// </summary>
public partial class InlineEditOverlayView : UserControl, IPrewarmableSurfaces
{
    /// <summary>Card width assumed before its first layout.</summary>
    private const double FallbackCardWidth = 232;

    public static readonly StyledProperty<ChartRenderer?> RendererProperty =
        AvaloniaProperty.Register<InlineEditOverlayView, ChartRenderer?>(nameof(Renderer));

    private InlineEditViewModel? _vm;

    public InlineEditOverlayView()
    {
        InitializeComponent();
        FhpEditorCard.AddHandler(KeyDownEvent, OnFhpKeyDown, RoutingStrategies.Tunnel);
        SectionEditorCard.AddHandler(KeyDownEvent, OnSectionKeyDown, RoutingStrategies.Tunnel);
        LyricEditorCard.AddHandler(KeyDownEvent, OnLyricKeyDown, RoutingStrategies.Tunnel);
        LyricEditorCard.LostFocus += (_, _) => Dispatcher.UIThread.Post(CancelWhenFocusLeft, DispatcherPriority.Input);
        LyricEditorCard.SizeChanged += (_, _) => Reposition();
        FhpEditorCard.LostFocus += (_, _) => Dispatcher.UIThread.Post(CancelWhenFocusLeft, DispatcherPriority.Input);
        SectionEditorCard.LostFocus += (_, _) => Dispatcher.UIThread.Post(CancelWhenFocusLeft, DispatcherPriority.Input);
        SectionSuggestionList.PointerReleased += OnSuggestionClicked;
        FhpEditorCard.SizeChanged += (_, _) => Reposition();
        SectionEditorCard.SizeChanged += (_, _) => Reposition();
    }

    /// <summary>The timeline the editors hang over.</summary>
    public ChartRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    /// <inheritdoc />
    public IReadOnlyList<Control> HiddenSurfaces() => [.. new Control[] { FhpEditorCard, SectionEditorCard, LyricEditorCard }.Where(c => !c.IsVisible)];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(DataContext as InlineEditViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Watch(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) != null) Watch(DataContext as InlineEditViewModel);
    }

    private void Watch(InlineEditViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            _vm.Fhp.PropertyChanged -= OnEditorChanged;
            _vm.Section.PropertyChanged -= OnEditorChanged;
            _vm.Lyric.PropertyChanged -= OnEditorChanged;
            _vm.State.PropertyChanged -= OnStateChanged;
            Renderer?.RemoveHandler(KeyDownEvent, OnTimelineKeyDown);
            _vm.Lyric.Cancel();
            if (ReferenceEquals(_vm.Host, Renderer)) _vm.Host = null;
        }

        _vm = vm;
        if (_vm == null) return;
        _vm.Fhp.PropertyChanged += OnEditorChanged;
        _vm.Section.PropertyChanged += OnEditorChanged;
        _vm.Lyric.PropertyChanged += OnEditorChanged;
        _vm.State.PropertyChanged += OnStateChanged;
        Renderer?.AddHandler(KeyDownEvent, OnTimelineKeyDown, RoutingStrategies.Tunnel);
        _vm.Host = Renderer;
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "IsOpen") return;
        Dispatcher.UIThread.Post(() =>
        {
            Reposition();
            if (_vm?.Fhp.IsOpen == true) Focus(FhpFretBox);
            else if (_vm?.Section.IsOpen == true) Focus(SectionNameBox);
            else if (_vm?.Lyric.IsOpen == true) Focus(LyricTextBox);
        }, DispatcherPriority.Render);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.ActiveProject)) _vm?.Lyric.Cancel();
        if (_vm is { Fhp.IsOpen: true } or { Section.IsOpen: true } or { Lyric.IsOpen: true }) Reposition();
    }

    private static void Focus(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Hangs each open card at its song time, mapped through the one view anchor.</summary>
    internal void Reposition()
    {
        if (_vm == null || Renderer is not { } renderer) return;
        var origin = renderer.TranslatePoint(default, this) ?? default;
        var width = renderer.Bounds.Width;
        var state = _vm.State;
        if (_vm.Fhp.IsOpen)
        {
            var cardWidth = FhpEditorCard.Bounds.Width > 0 ? FhpEditorCard.Bounds.Width : FallbackCardWidth;
            var x = InlineEditPlacement.Left(_vm.Fhp.PlacementMs, state.TimelineViewAnchorEffectiveMs, state.PixelsPerMs, width, cardWidth);
            var y = InlineEditPlacement.FhpTop(renderer.ViewportHeight, renderer.TopMargin, renderer.BottomMargin);
            var cardHeight = FhpEditorCard.Bounds.Height;
            Canvas.SetLeft(FhpEditorCard, origin.X + x);
            Canvas.SetTop(FhpEditorCard, origin.Y + Math.Max(0, Math.Min(y, renderer.ViewportHeight - cardHeight)));
        }

        if (_vm.Section.IsOpen)
        {
            var cardWidth = SectionEditorCard.Bounds.Width > 0 ? SectionEditorCard.Bounds.Width : FallbackCardWidth;
            var x = InlineEditPlacement.Left(_vm.Section.PlacementMs, state.TimelineViewAnchorEffectiveMs, state.PixelsPerMs, width, cardWidth);
            var y = InlineEditPlacement.SectionPopoverTop(renderer.TopRibbon);
            Canvas.SetLeft(SectionEditorCard, origin.X + x);
            Canvas.SetTop(SectionEditorCard, origin.Y + y);
        }

        if (_vm.Lyric.IsOpen)
        {
            var cardWidth = LyricEditorCard.Bounds.Width > 0 ? LyricEditorCard.Bounds.Width : FallbackCardWidth;
            var x = InlineEditPlacement.Left(_vm.Lyric.PlacementMs, state.TimelineViewAnchorEffectiveMs, state.PixelsPerMs, width, cardWidth);
            Canvas.SetLeft(LyricEditorCard, origin.X + x);
            Canvas.SetTop(LyricEditorCard, origin.Y + renderer.TopRibbon.LyricY);
        }
    }

    private void OnTimelineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.F2 or Key.Enter)) return;
        if (Renderer?.Editor?.Selection.SelectedLyric is not { } lyric || _vm == null) return;
        _vm.ShowLyric(0, lyric);
        e.Handled = true;
    }

    private void OnLyricKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Escape)
        {
            _vm.Lyric.Cancel();
            ReturnFocus();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Tab)
        {
            _vm.Lyric.Text = LyricTextBox.Text ?? "";
            var direction = e.Key == Key.Tab ? (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1) : 0;
            if (_vm.Lyric.Commit(direction))
            {
                if (_vm.Lyric.IsOpen) Focus(LyricTextBox);
                else ReturnFocus();
            }

            e.Handled = true;
        }
    }

    private void OnFhpKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        switch (e.Key)
        {
            case Key.Enter:
                // The bindings may still hold the previous keystroke's text.
                _vm.Fhp.FretText = FhpFretBox.Text ?? "";
                _vm.Fhp.WidthText = FhpWidthBox.Text ?? "";
                if (_vm.Fhp.Commit()) ReturnFocus();
                else Focus(string.IsNullOrWhiteSpace(FhpFretBox.Text) ? FhpFretBox : FhpWidthBox);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Fhp.Cancel();
                ReturnFocus();
                e.Handled = true;
                break;
        }
    }

    private void OnSectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        switch (e.Key)
        {
            case Key.Enter when e.Source is not Button:
                CommitSection();
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Section.Cancel();
                ReturnFocus();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when e.Source == SectionNameBox:
                var count = SectionSuggestionList.ItemCount;
                if (count == 0) break;
                var next = SectionSuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                SectionSuggestionList.SelectedIndex = Math.Clamp(next, 0, count - 1);
                SectionSuggestionList.ScrollIntoView(SectionSuggestionList.SelectedIndex);
                e.Handled = true;
                break;
        }
    }

    private void CommitSection()
    {
        if (_vm == null) return;
        _vm.Section.Name = SectionNameBox.Text ?? "";
        if (SectionSuggestionList.SelectedItem is SectionSuggestion picked) _vm.Section.Name = picked.Key;
        if (_vm.Section.Commit()) ReturnFocus();
    }

    private void OnSuggestionClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || SectionSuggestionList.SelectedItem is not SectionSuggestion) return;
        CommitSection();
    }

    private void SectionMore_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.ShowFullEventDialog();
        ReturnFocus();
    }

    /// <summary>A click anywhere else takes focus off the card: the edit is dropped, never half-written.</summary>
    private void CancelWhenFocusLeft()
    {
        if (_vm == null) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (_vm.Fhp.IsOpen && !IsWithin(focused, FhpEditorCard)) _vm.Fhp.Cancel();
        if (_vm.Section.IsOpen && !IsWithin(focused, SectionEditorCard)) _vm.Section.Cancel();
        if (_vm.Lyric.IsOpen && !IsWithin(focused, LyricEditorCard)) _vm.Lyric.Cancel();
    }

    /// <summary>
    ///     Whether Escape belongs to an inline editor: focus is inside one of the cards,
    ///     which are visible only while their draft is open. The window tunnel asks this
    ///     (<c>GlobalChordGate.AllowsEscape</c>) and yields, so the card's own handler
    ///     cancels the draft instead of the shell clearing the selection.
    /// </summary>
    internal static bool ClaimsEscape(object? focused) =>
        focused is Visual visual && visual.FindAncestorOfType<InlineEditOverlayView>(true) is not null;

    private static bool IsWithin(Visual? visual, Visual card) => visual != null && (ReferenceEquals(visual, card) || card.IsVisualAncestorOf(visual));

    private void ReturnFocus() => Renderer?.Focus();
}
