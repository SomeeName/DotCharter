using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The chart synth's instrument pickers: guitar and bass model, pickup
///     selector, coil split. Hosted by the Mixer inspector's MIDI synth card and
///     by Play Mode's Amp/FX panel, over <c>TransportViewModel.ChartSynthInstrument</c>.
/// </summary>
public partial class ChartSynthInstrumentView : UserControl
{
    /// <summary>Builds the view from its markup.</summary>
    public ChartSynthInstrumentView() => InitializeComponent();
}
