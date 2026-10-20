using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Mixer tab: Levels, Stems, Playback FX, EQ &amp; filters and the MIDI synth, one section
/// at a time behind the icon-only rail (<see cref="InspectorSectionRail" />).
///     <para>
///         Double-click-to-reset is not a code-behind <c>DoubleTapped</c>
///         handler that forwards the slider's NAME to
///         <c>TransportViewModel.ResetMixerParameter</c>. It is
///         <c>Controls/ResetOnDoubleClick</c> — the same command, the same
///         name as its parameter, declared on each slider — because the EQ
///         module's four sliders needed the identical gesture and a second
///         code-behind copy of it is how one of the two silently stops working.
///     </para>
/// </summary>
public partial class MixerInspectorView : UserControl
{
    public MixerInspectorView()
    {
        InitializeComponent();
        Rail = new InspectorSectionRail(this, this.FindControl<StackPanel>("MixerRailEntries")!);
    }

    /// <summary>The section rail — the test hook.</summary>
    internal InspectorSectionRail Rail { get; }
}
