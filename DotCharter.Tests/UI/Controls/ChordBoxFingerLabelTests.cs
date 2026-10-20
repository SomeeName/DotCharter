using Avalonia.Media;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The finger number the chord box draws on each pip (#453): plain white.
///     Near-black text did not read on the red, blue and orange pips; a dark
///     outline drawn from offset copies smeared the digit at 11 px, so there is
///     none. The rendered half (white pixels on every pip) is
///     <c>Perf/ChordBoxFingerLabelUiPerfSmokeTests</c>.
/// </summary>
public class ChordBoxFingerLabelTests
{
    [Fact]
    public void TheLabel_IsWhite() =>
        Assert.Equal(Colors.White, ChordBoxControl.FingerTextBrush.Color);
}
