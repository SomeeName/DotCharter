namespace DotCharter.Tests.Core;

/// <summary>
///     The anchor in effect is chosen by position, never by storage order: a level's anchors
///     are not sorted mid-session, so the last stored qualifying anchor can be an earlier one.
/// </summary>
public sealed class FretAnchorLookupTests
{
    [Fact]
    public void The_latest_anchor_at_or_before_the_point_wins_whatever_the_storage_order()
    {
        var later = new FretAnchor(new FractionalPosition(8), 9);
        var earlier = new FretAnchor(new FractionalPosition(0), 2);
        List<FretAnchor> anchors = [later, earlier, new FretAnchor(new FractionalPosition(12), 5)];

        Assert.Same(later, FretAnchorLookup.InEffectAt(anchors, new FractionalPosition(9)));
        Assert.Same(later, FretAnchorLookup.InEffectAt(anchors, new FractionalPosition(8)));
        Assert.Same(earlier, FretAnchorLookup.InEffectAt(anchors, new FractionalPosition(7)));
        Assert.Null(FretAnchorLookup.InEffectAt([later], new FractionalPosition(7)));
    }
}
