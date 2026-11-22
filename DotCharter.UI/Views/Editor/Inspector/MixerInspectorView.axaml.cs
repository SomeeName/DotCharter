using Avalonia.Controls;
using Avalonia.Interactivity;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Mixer tab in inspector design D: the Stems / EQ / MIDI section strip over one section's cards at a time,
///     all bound to <c>InspectorViewModel</c> (the section is <c>MixerSections</c>). The levels, track pan, speed
///     and pitch live in the toolbar's Mix and Tempo popovers.
///     <para>
///         Double-click-to-reset is not a code-behind <c>DoubleTapped</c> handler: it is
///         <c>Controls/ResetOnDoubleClick</c>, declared on each fader with the value the row's reset writes, so the
///         stem strips and the EQ's sliders share one gesture.
///     </para>
/// </summary>
public partial class MixerInspectorView : UserControl
{
    public MixerInspectorView() => InitializeComponent();

    /// <summary>The three section bodies, so the inspector's tab rewarmer realizes the hidden ones too.</summary>
    internal IReadOnlyList<Control> Bodies => [MixerStemsBody, MixerEqBody, MixerMidiBody];

    /// <summary>The VST instrument picker is the Practice tools popover's: this link opens it.</summary>
    private void OpenVstInstrumentPicker(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel shell) shell.RequestPopover(TransportPopover.Tools);
    }
}
