using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DotCharter.UI.Controls;

/// <summary>
///     Drag one Shapes card onto another to merge it into the target. A card wears four commands
///     (begin, over, drop, end) and nothing else: the pointer's press-move-release becomes those
///     moments, and the panel's view model decides what each means.
///     <para>
///         A press on the card's blank area or its diagram arms a drag; a name box, a tick or an
///         action button never does. Moving past a few pixels begins it and takes the pointer, so a
///         drag that began on the diagram never also places the shape. The card under the pointer
///         is found by hit test, because the pointer is captured by the card the drag began on.
///         Release drops on the card under the pointer; release anywhere else, or Esc, ends the drag.
///         Near the top or bottom of the list the scroll viewer follows the pointer.
///     </para>
/// </summary>
public static class ShapeCardDrag
{
    /// <summary>Executed when a drag begins on the card.</summary>
    public static readonly AttachedProperty<ICommand?> BeginProperty =
        AvaloniaProperty.RegisterAttached<Border, ICommand?>("Begin", typeof(ShapeCardDrag));

    /// <summary>Executed with <c>true</c> when the drag moves onto the card and <c>false</c> when it leaves.</summary>
    public static readonly AttachedProperty<ICommand?> OverProperty =
        AvaloniaProperty.RegisterAttached<Border, ICommand?>("Over", typeof(ShapeCardDrag));

    /// <summary>Executed when the drag is released over the card.</summary>
    public static readonly AttachedProperty<ICommand?> DropProperty =
        AvaloniaProperty.RegisterAttached<Border, ICommand?>("Drop", typeof(ShapeCardDrag));

    /// <summary>Executed on the card the drag began on when it ends, wherever it was released.</summary>
    public static readonly AttachedProperty<ICommand?> EndProperty =
        AvaloniaProperty.RegisterAttached<Border, ICommand?>("End", typeof(ShapeCardDrag));

    /// <summary>How far the pointer travels before a press becomes a drag, in device-independent pixels.</summary>
    private const double Threshold = 6;

    /// <summary>The band at the list's top and bottom edge where a drag scrolls it, and the fastest step per move.</summary>
    private const double ScrollBand = 28;

    private const double ScrollStep = 24;

    private static readonly ConditionalWeakTable<Border, Session> Sessions = [];

    static ShapeCardDrag() => BeginProperty.Changed.AddClassHandler<Border>((card, _) => Sessions.GetValue(card, c => new Session(c)));

    public static ICommand? GetBegin(Border card) => card.GetValue(BeginProperty);

    public static void SetBegin(Border card, ICommand? value) => card.SetValue(BeginProperty, value);

    public static ICommand? GetOver(Border card) => card.GetValue(OverProperty);

    public static void SetOver(Border card, ICommand? value) => card.SetValue(OverProperty, value);

    public static ICommand? GetDrop(Border card) => card.GetValue(DropProperty);

    public static void SetDrop(Border card, ICommand? value) => card.SetValue(DropProperty, value);

    public static ICommand? GetEnd(Border card) => card.GetValue(EndProperty);

    public static void SetEnd(Border card, ICommand? value) => card.SetValue(EndProperty, value);

    private static void Run(ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    /// <summary>One card's press, move and release, and the drag they add up to.</summary>
    private sealed class Session
    {
        private readonly Border _card;
        private Point _start;
        private IPointer? _pointer;
        private bool _armed;
        private bool _dragging;
        private Border? _over;

        internal Session(Border card)
        {
            _card = card;
            card.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
            card.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, true);
            card.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
            card.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
            card.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble, true);
        }

        private static bool StartsDrag(Visual? source, Visual card)
        {
            // Only the blank card and the diagram button arm a drag; a text box, a tick or an action keeps its own press.
            foreach (var visual in source?.GetSelfAndVisualAncestors() ?? [])
            {
                if (ReferenceEquals(visual, card)) return true;
                if (visual is TextBox or CheckBox) return false;
                if (visual is Button { Name: not "ShapePlace" }) return false;
            }

            return true;
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (GetBegin(_card) is null || !e.GetCurrentPoint(_card).Properties.IsLeftButtonPressed) return;
            _armed = StartsDrag(e.Source as Visual, _card);
            if (!_armed) return;
            _pointer = e.Pointer;
            _start = e.GetPosition(TopLevel.GetTopLevel(_card));
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!_armed || TopLevel.GetTopLevel(_card) is not { } top) return;
            var at = e.GetPosition(top);
            if (!_dragging)
            {
                if (Math.Abs(at.X - _start.X) < Threshold && Math.Abs(at.Y - _start.Y) < Threshold) return;
                _dragging = true;
                e.Pointer.Capture(_card);
                Run(GetBegin(_card));
            }

            e.Handled = true;
            ScrollWith(e);
            var over = CardAt(top, at);
            if (ReferenceEquals(over, _over)) return;
            if (_over is not null) Run(GetOver(_over), false);
            _over = over;
            if (_over is not null) Run(GetOver(_over), true);
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_dragging)
            {
                _armed = false;
                return;
            }

            var target = TopLevel.GetTopLevel(_card) is { } top ? CardAt(top, e.GetPosition(top)) : null;
            Finish();
            if (target is not null) Run(GetDrop(target));
            Run(GetEnd(_card));
            e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_dragging || e.Key != Key.Escape) return;
            Finish();
            Run(GetEnd(_card));
            e.Handled = true;
        }

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (!_dragging || !ReferenceEquals(e.Source, _card)) return;
            Finish();
            Run(GetEnd(_card));
        }

        /// <summary>Ends the gesture's own state and gives the pointer back, before any command runs.</summary>
        private void Finish()
        {
            _dragging = false;
            _armed = false;
            _over = null;
            _pointer?.Capture(null);
            _pointer = null;
        }

        /// <summary>The card (any border wearing a Drop command) under the pointer.</summary>
        private static Border? CardAt(TopLevel top, Point at) =>
            (top.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => GetDrop(b) is not null);

        private void ScrollWith(PointerEventArgs e)
        {
            if (_card.FindAncestorOfType<ScrollViewer>() is not { } scroll) return;
            var y = e.GetPosition(scroll).Y;
            var step = y < ScrollBand ? -(ScrollBand - y) / ScrollBand
                : y > scroll.Bounds.Height - ScrollBand ? (y - (scroll.Bounds.Height - ScrollBand)) / ScrollBand
                : 0;
            if (step == 0) return;
            var offset = scroll.Offset.Y + Math.Clamp(step, -1, 1) * ScrollStep;
            scroll.Offset = scroll.Offset.WithY(Math.Clamp(offset, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
    }
}
