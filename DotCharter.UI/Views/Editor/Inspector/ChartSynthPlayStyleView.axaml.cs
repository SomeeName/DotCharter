using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The chart synth's playing style: hand shapes ring, the holds over
///     short gaps and their note values, per guitar and bass. Hosted by the Mixer
///     inspector's MIDI synth card over <c>ChartSynthPlayStyleViewModel</c>.
/// </summary>
public partial class ChartSynthPlayStyleView : UserControl
{
    /// <summary>Builds the view from its markup.</summary>
    public ChartSynthPlayStyleView() => InitializeComponent();
}
