using Avalonia.Media;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
///     #309: <see cref="ChartRenderContext.StringColors" /> was an auto-property initialised from
///     <see cref="ChartTheme.BaseStringColors" />, so each 2D timeline captured the palette ARRAY
///     once, at construction. <c>ChartTheme.Apply</c> writes that array in place today, which
///     hid it — but the field is a public, assignable static, and a theme swap that assigned a
///     new array would have recoloured every surface except the timelines already on screen.
/// </summary>
public sealed class RenderContextStringColorsTests
{
    [Fact]
    public void AnExistingTimeline_FollowsAPaletteArraySwap()
    {
        var rc = new ChartRenderContext();
        var original = ChartTheme.BaseStringColors;
        // The SAME colours in a new array: the reference is what is under test, and identical
        // contents keep any test reading the palette in parallel unaffected.
        var swapped = (Color[])original.Clone();
        try
        {
            ChartTheme.BaseStringColors = swapped;
            Assert.Same(swapped, rc.StringColors);
        }
        finally
        {
            ChartTheme.BaseStringColors = original;
        }

        Assert.Same(original, rc.StringColors);
    }
}
