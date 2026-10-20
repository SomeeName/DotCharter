using System.Numerics;
using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>0.14.2 — pure energy → background colour mapping.</summary>
public class CrowdBackgroundPaletteTests
{
    [Fact]
    public void Energy_IsClamped()
    {
        Assert.Equal(CrowdBackgroundPalette.Compute(0f), CrowdBackgroundPalette.Compute(-3f));
        Assert.Equal(CrowdBackgroundPalette.Compute(1f), CrowdBackgroundPalette.Compute(42f));
    }

    [Fact]
    public void HigherEnergy_BrightensTheSky_AndTheHorizonGlow()
    {
        var (lowTop, lowHorizon) = CrowdBackgroundPalette.Compute(0f);
        var (highTop, highHorizon) = CrowdBackgroundPalette.Compute(1f);

        Assert.True(highTop.R > lowTop.R);
        Assert.True(highTop.B > lowTop.B);
        Assert.True(highHorizon.A > lowHorizon.A);
    }

    [Fact]
    public void SkyTop_IsAlwaysOpaque()
    {
        Assert.Equal(255, CrowdBackgroundPalette.Compute(0f).Top.A);
        Assert.Equal(255, CrowdBackgroundPalette.Compute(1f).Top.A);
    }

    [Fact]
    public void Neutral_SitsBetweenTheExtremes()
    {
        var low = CrowdBackgroundPalette.Compute(0f);
        var neutral = CrowdBackgroundPalette.Compute(CrowdBackgroundPalette.NeutralEnergy);
        var high = CrowdBackgroundPalette.Compute(1f);

        Assert.InRange(neutral.Top.B, low.Top.B + 1, high.Top.B - 1);
        Assert.InRange(neutral.Horizon.A, low.Horizon.A + 1, high.Horizon.A - 1);
    }

    [Fact]
    public void AWorldPoint_ProjectsIntoThePixelSpaceOfTheBackgroundPass()
    {
        // Identity MVP: NDC == the point, so (0, 0) is the frame centre and
        // clip y = -1 is the TOP row (Vulkan clip space points y down, as the
        // background pass's ortho does).
        Assert.Equal(new Vector2(800, 450),
            CrowdBackgroundPalette.ProjectToScreen(Matrix4x4.Identity, new Vector3(0, 0, 0.5f), 1600, 900));
        Assert.Equal(new Vector2(0, 0),
            CrowdBackgroundPalette.ProjectToScreen(Matrix4x4.Identity, new Vector3(-1, -1, 0.5f), 1600, 900));

        // Behind the camera (w <= 0) there is no sensible centre.
        var flip = Matrix4x4.CreateScale(1, 1, 1) with { M44 = -1 };
        Assert.Null(CrowdBackgroundPalette.ProjectToScreen(flip, new Vector3(0, 0, 0.5f), 1600, 900));
    }
}
