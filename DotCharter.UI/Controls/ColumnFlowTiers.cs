namespace DotCharter.UI.Controls;

/// <summary>How wide a card body is (inspector design D): it decides how many columns the cards take.</summary>
public enum ColumnFlowTier
{
    /// <summary>Under a 480 px pane: one card at a time, every card on the pager.</summary>
    Tiny,

    /// <summary>A 480-619 px pane (530 is the design's primary size): the first card and a pager beside it.</summary>
    Small,

    /// <summary>A 620-899 px pane: the first card and the rest stacked beside it (paged while the body is short).</summary>
    Usual,

    /// <summary>A pane of 900 px or more: a column per card.</summary>
    Wide
}

/// <summary>
/// The width and height steps of <see cref="ColumnFlowPanel" />, pure so the gate pins them. The design measured
///     its steps on the PANE (480 / 620 / 900 px); the panel only knows its own width, which is the pane less the
///     body's two 8 px margins, so the steps here are 16 px lower.
/// </summary>
public static class ColumnFlowTiers
{
    /// <summary>The two margins of the card body (px), between the pane width and the body width.</summary>
    public const double BodyMargins = 16;

    /// <summary>The body width at which a pane leaves <see cref="ColumnFlowTier.Tiny" /> (px).</summary>
    public const double SmallWidth = 480 - BodyMargins;

    /// <summary>The body width at which a pane becomes <see cref="ColumnFlowTier.Usual" /> (px).</summary>
    public const double UsualWidth = 620 - BodyMargins;

    /// <summary>The body width at which a pane becomes <see cref="ColumnFlowTier.Wide" /> (px).</summary>
    public const double WideWidth = 900 - BodyMargins;

    /// <summary>Under this body height (px) the cards beside the first one share a pager instead of stacking.</summary>
    public const double ShortHeight = 320;

    /// <summary>The tier of a body this wide; an unmeasured (infinite) body counts as the primary Small size.</summary>
    public static ColumnFlowTier ForWidth(double width) => width switch
    {
        double.PositiveInfinity or double.NaN => ColumnFlowTier.Small,
        >= WideWidth => ColumnFlowTier.Wide,
        >= UsualWidth => ColumnFlowTier.Usual,
        >= SmallWidth => ColumnFlowTier.Small,
        _ => ColumnFlowTier.Tiny
    };

    /// <summary>Whether a body this tall is short; an unmeasured (infinite) body is not.</summary>
    public static bool IsShort(double height) => height < ShortHeight;
}
