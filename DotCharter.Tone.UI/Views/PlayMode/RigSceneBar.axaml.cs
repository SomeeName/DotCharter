using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The rig's scene bar: scenes A–H, the active setlist's ◀ ▶ and the MIDI
///     monitor, over <see cref="PedalboardViewModel" />. The code here is the
///     in-place rename only: the field takes the keys when it appears, Enter
///     keeps the name, Esc drops it, and leaving the field keeps it.
/// </summary>
public partial class RigSceneBar : UserControl
{
    private PedalboardViewModel? _rig;

    public RigSceneBar()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnRenameKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnRenameLostFocus, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Watch(DataContext as PedalboardViewModel);
        DetachedFromVisualTree += (_, _) => Watch(null);
        AttachedToVisualTree += (_, _) => Watch(DataContext as PedalboardViewModel);
    }

    private void Watch(PedalboardViewModel? rig)
    {
        if (ReferenceEquals(rig, _rig)) return;
        if (_rig is not null)
            foreach (var slot in _rig.SceneSlots) slot.PropertyChanged -= OnSlotChanged;
        _rig = rig;
        if (_rig is not null)
            foreach (var slot in _rig.SceneSlots) slot.PropertyChanged += OnSlotChanged;
    }

    /// <summary>A rename started: the field is visible now, so it takes the keys.</summary>
    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RigSceneSlotViewModel.IsRenaming) ||
            sender is not RigSceneSlotViewModel { IsRenaming: true } slot)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var box = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "SceneName" && ReferenceEquals(t.DataContext, slot));
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private static RigSceneSlotViewModel? RenamingSlotOf(object? source) =>
        source is TextBox { Name: "SceneName" } box ? box.DataContext as RigSceneSlotViewModel : null;

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (RenamingSlotOf(e.Source) is not { } slot) return;
        if (e.Key == Key.Enter)
        {
            slot.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            slot.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (RenamingSlotOf(e.Source) is { IsRenaming: true } slot)
            slot.CommitRenameCommand.Execute(null);
    }
}
