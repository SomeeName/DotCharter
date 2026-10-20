using DotCharter.Tone.UI.Views.PlayMode.RigEditors;

namespace DotCharter.Tests.UI;

/// <summary>
///     The amp face's two columns (owner, 2026-09-08: "layout is still quite
///     weird" — a head, a floating 300 px card and a gap before the rack): the
///     gear keeps its 1180 px design width and the capture library takes
///     whatever is left, down to a 300 px floor in a narrow dock.
/// </summary>
public sealed class RigAmpFaceLayoutTests
{
    [Theory]
    [InlineData(1650, 452)]   // the owner's dock: 1650 − 1180 − 18
    [InlineData(2000, 802)]
    [InlineData(1480, 300)]   // exactly the floor: 1480 − 1198 = 282 → 300
    [InlineData(830, 300)]    // the narrow compare viewport: the head gives way instead
    public void TheCaptureCard_TakesTheWidthTheGearLeaves(double host, double expected)
        => Assert.Equal(expected, RigAmpFaceView.CaptureWidthFor(host));
}
