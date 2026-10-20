using Avalonia.Controls;
using DotCharter.Tone.UI.ViewModels.Editor;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The shared plugin rack: the guitar VST3 chain over any
///     <see cref="IVstRack" />. DotCharter's Tools popover and Amp/FX panel and
///     RigLab's Plugins tab all host THIS view, so they offer the same rows, the
///     same reorder gesture and the same add picker.
/// </summary>
/// <remarks>
///     <para>
///         The drag reorder (<see cref="VstChainDragController" />) is gated on
///         ATTACH and disposed on detach, like every pointer controller in this
///         app: a popover realized by a cancelled prewarm open is never attached,
///         so it holds no handlers.
///     </para>
///     <para>
///         A pick in the add flyout closes it. The host appends the plugin and
///         writes the selection back to null in the same turn, so the list
///         never shows a stale highlight either.
///     </para>
/// </remarks>
public partial class VstRackView : UserControl
{
    private VstChainDragController? _chainDrag;

    public VstRackView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
            _chainDrag ??= new VstChainDragController(VstRackSlots, () => DataContext as IVstRack);
        DetachedFromVisualTree += (_, _) =>
        {
            _chainDrag?.Dispose();
            _chainDrag = null;
        };
    }

    private void OnAddPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0) VstRackAddButton.Flyout?.Hide();
    }
}
