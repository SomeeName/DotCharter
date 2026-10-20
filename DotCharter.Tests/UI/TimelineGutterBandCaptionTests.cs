using DotCharter.UI.Controls;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     The gutter's bottom-band captions (SHAPES / FHP / TIME, 0.59). The
///     bottom margin stacks the hand-shape bar, the FHP lane and the time
///     ruler; one TIME caption used to span all three, leaving the two EDITING
///     bands the ToolCoordinator routes clicks to unlabeled — a click in the
///     anonymous FHP band is what the live audit reported as "clicking the
///     ruler created a yellow region". Each caption slot must sit INSIDE the
///     zone whose clicks it names (<see cref="TimelineZones" />), or the label
///     promises one behaviour and the click delivers another.
/// </summary>
public sealed class TimelineGutterBandCaptionTests
{
    private const double H = 600;
    private const double Bottom = 58; // UILayoutConstants.HighwayBottomMargin
    private const double LaneBottom = H - Bottom;

    [Fact]
    public void EachCaptionSlot_SitsInsideItsOwnZone()
    {
        var slots = TimelineGutterControl.BottomBandCaptionSlots(LaneBottom, Bottom);
        Assert.True(slots.ShowBands);

        var shapesCentre = slots.ShapesY + slots.ShapesH / 2;
        var fhpCentre = slots.FhpY + slots.FhpH / 2;
        var timeCentre = slots.TimeY + slots.TimeH / 2;

        Assert.True(TimelineZones.IsHandShapeBand(shapesCentre, H, Bottom),
            "The SHAPES caption must sit in the hand-shape routing band.");
        Assert.True(TimelineZones.IsFhpBand(fhpCentre, H, Bottom),
            "The FHP caption must sit in the FHP routing band.");
        Assert.True(TimelineZones.IsTimeRuler(timeCentre, H),
            "The TIME caption must sit in the ruler strip.");

        // …and in none of the others.
        Assert.False(TimelineZones.IsFhpBand(shapesCentre, H, Bottom));
        Assert.False(TimelineZones.IsHandShapeBand(fhpCentre, H, Bottom));
        Assert.False(TimelineZones.IsHandShapeBand(timeCentre, H, Bottom));
        Assert.False(TimelineZones.IsFhpBand(timeCentre, H, Bottom));
    }

    [Fact]
    public void ASqueezedMargin_HidesTheBandCaptions_RatherThanMislabeling()
    {
        // A margin too small for the FHP band to clear the ruler: the editing
        // captions stand down; TIME stays.
        var squeezed = TimelineGutterControl.BottomBandCaptionSlots(laneBottom: H - 30, bottomMargin: 30);
        Assert.False(squeezed.ShowBands);
        Assert.Equal(H - TimelineZones.TimeRulerHeightPx, squeezed.TimeY);
    }

    /// <summary>Both resx files must carry the caption keys — the gutter is
    /// custom-drawn, so the axaml localization sweep never sees them and a
    /// missing key renders as the raw key string.</summary>
    [Theory]
    [InlineData("DotCharter.Localization/Resources.resx")]
    [InlineData("DotCharter.Localization/Resources.de-DE.resx")]
    public void TheCaptionKeys_ExistInBothResxFiles(string resx)
    {
        var content = File.ReadAllText(RepoPath(resx));
        foreach (var key in new[] { "Timeline_Gutter_Shapes", "Timeline_Gutter_Fhp", "Timeline_Gutter_Time" })
        {
            Assert.True(content.Contains($"\"{key}\"", StringComparison.Ordinal),
                $"Loc key '{key}' missing from {resx}.");
        }
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
