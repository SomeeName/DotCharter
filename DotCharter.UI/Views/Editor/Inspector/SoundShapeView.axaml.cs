using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Note tab's Shape page: the row-aligned grip grid beside the fretboard. Code-behind holds
///     only view state: the board's fret paging and the grid's keys (Up/Down walk the strings, Left/Right
///     step the finger) while focus is on the rows; in a Note or Grip box Up/Down move to the same box of the
///     string above or below, in the order the rows are drawn.
/// </summary>
public partial class SoundShapeView : UserControl
{
    private GripInspectorViewModel? _grip;

    public SoundShapeView()
    {
        InitializeComponent();
        // Tunnel: a text box takes Up/Down for its caret before a bubbling handler could see them.
        SoundShapeRowList.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        // The body binds its own context (the grip page) off ours, so follow IT, not this control's.
        SoundShapeBody.DataContextChanged += (_, _) =>
        {
            if (VisualRoot != null) Subscribe();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _grip = SoundShapeBody.DataContext as GripInspectorViewModel;
        _grip?.FocusDestination += OnFocusDestination;
    }

    private void Unsubscribe()
    {
        _grip?.FocusDestination -= OnFocusDestination;
        _grip = null;
    }

    private void OnFocusDestination()
    {
        if (SoundShapeBoard.IsEffectivelyVisible) SoundShapeBoard.Focus();
        else SoundShapeRows.Focus();
    }

    private void OnLowerFrets(object? sender, RoutedEventArgs e) => SoundShapeBoard.PageFrets(-1);

    private void OnHigherFrets(object? sender, RoutedEventArgs e) => SoundShapeBoard.PageFrets(1);

    private void OnShowShape(object? sender, RoutedEventArgs e) => SoundShapeBoard.ShowShape();

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down)) return;
        var source = e.Source as Visual;
        var box = source as TextBox ?? source?.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
        if (box?.DataContext is GripRow) e.Handled = FocusAdjacentBox(box, e.Key == Key.Down ? 1 : -1);
    }

    private bool FocusAdjacentBox(TextBox box, int step)
    {
        var column = Grid.GetColumn(box);
        var boxes = SoundShapeRowList.GetVisualDescendants().OfType<TextBox>()
            .Where(candidate => Grid.GetColumn(candidate) == column && candidate.DataContext is GripRow)
            .OrderBy(candidate => candidate.TranslatePoint(default, SoundShapeRowList)?.Y ?? 0).ToList();
        var index = boxes.IndexOf(box) + step;
        while (index >= 0 && index < boxes.Count && !boxes[index].IsEnabled) index += step;
        if (index < 0 || index >= boxes.Count) return false;
        boxes[index].Focus();
        return true;
    }

    private void OnBodyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _grip == null || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is TextBox or ComboBox or AutoCompleteBox or ColumnFlowFretboard) return;
        switch (e.Key)
        {
            case Key.Up:
                _grip.MoveRow(-1);
                break;
            case Key.Down:
                _grip.MoveRow(1);
                break;
            case Key.Left:
                _grip.StepFinger(-1);
                break;
            case Key.Right:
                _grip.StepFinger(1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
