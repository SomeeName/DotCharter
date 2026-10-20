using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Both composed hosts must take their visual size and their swapchain
///     size from ONE <c>ComposedSurfaceGeometry.Measure</c> call.</b>
///
///     <para>
///         <c>ComposedSurfaceGeometryTests</c> proves the arithmetic; this
///         proves it is the arithmetic the app runs. A source lint, for the
///         same reason <c>Preview3DComposedAttachTests</c> is one: the failure
///         is a bilinear resample — it renders perfectly, throws nothing, and
///         needs a GPU, a fractional shell scale and a pair of eyes to see. The
///         regression it guards is a one-line edit back to
///         <c>visual.Size = Bounds</c>, which would look like a simplification.
///     </para>
/// </summary>
public class ComposedSurfaceWiringTests
{
    private const string Canvas3D = "DotCharter.UI/Controls/Preview3DComposedCanvas.cs";
    private const string Backend2D = "DotCharter.UI/Controls/Timeline2D/VulkanTimelineBackend.cs";

    [Theory]
    [InlineData(Canvas3D)]
    [InlineData(Backend2D)]
    public void TheHost_SizesTheVisualFromTheMeasuredPixelSize_NotFromBounds(string path)
    {
        var source = Code(path);

        Assert.Contains("ComposedSurfaceGeometry.Measure(", source);
        Assert.Contains("size.DipWidth", source);
        Assert.Contains("size.DipHeight", source);

        // The pre-fix pairing, in both of its spellings. Either one back in the
        // file means the destination rectangle is fractional again.
        Assert.DoesNotContain("new Vector(Bounds.Width, Bounds.Height)", source);
        Assert.DoesNotContain("new Vector(logicalWidth, logicalHeight)", source);
        Assert.DoesNotContain("Math.Ceiling(Bounds.Width", source);
        Assert.DoesNotContain("Math.Ceiling(logicalWidth", source);
    }

    /// <summary>The flip's axis is half the surface height, so it has to be
    /// half of the DERIVED height — mirroring about a half-pixel line shifts
    /// every texel by half a pixel, which is the same blur one hop over.</summary>
    [Theory]
    [InlineData(Canvas3D)]
    [InlineData(Backend2D)]
    public void TheCounterFlip_CentersOnTheDerivedHeight(string path)
    {
        var source = Code(path);
        Assert.Contains("size.DipHeight / 2.0", source);
        Assert.DoesNotContain("Bounds.Height / 2.0", source);
        Assert.DoesNotContain("logicalHeight / 2.0", source);
    }

    /// <summary>A whole-pixel surface at a fractional origin is still
    /// resampled, so both hosts nudge onto the grid.</summary>
    [Theory]
    [InlineData(Canvas3D)]
    [InlineData(Backend2D)]
    public void TheHost_SnapsItsSurfaceOntoThePixelGrid(string path)
    {
        var source = Code(path);
        Assert.Contains("VisualPixelScale.DeviceOrigin(", source);
        Assert.Contains("ComposedSurfaceGeometry.SnapOffset(", source);
    }

    private static string Code(string relativePath)
    {
        var source = UiSourceTree.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Comments quote the old arithmetic deliberately ("the pre-fix pair
        // was…"), so a lint that reads them would fail on its own explanation.
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }
}
