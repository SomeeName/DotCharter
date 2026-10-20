using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14.2 — shared clamping rules for the Settings → Graphics values.
///     Pure, no Vulkan/UI involved.
/// </summary>
public class GraphicsSettingsPolicyTests
{
    [Theory]
    [InlineData(100, 100)] // default passes through
    [InlineData(50, 50)] // lower bound inclusive
    [InlineData(200, 200)] // upper bound inclusive
    [InlineData(49, 50)] // clamped up
    [InlineData(0, 50)] // degenerate config
    [InlineData(-25, 50)]
    [InlineData(201, 200)] // clamped down
    [InlineData(10_000, 200)]
    public void ClampRenderScalePercent_StaysWithinBounds(int input, int expected) => Assert.Equal(expected, GraphicsSettingsPolicy.ClampRenderScalePercent(input));

    [Theory]
    [InlineData(1000, 1000)] // default passes through
    [InlineData(200, 200)]
    [InlineData(3000, 3000)]
    [InlineData(0, 200)]
    [InlineData(-1, 200)]
    [InlineData(60_000, 3000)]
    public void ClampAnchorPreviewLeadMs_StaysWithinBounds(int input, int expected) => Assert.Equal(expected, GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs(input));

    [Theory]
    [InlineData(0, 0)] // 0 = unlimited, canonical
    [InlineData(-1, 0)] // negatives normalize to unlimited
    [InlineData(-500, 0)]
    [InlineData(60, 60)] // sane values pass through
    [InlineData(240, 240)]
    [InlineData(1, 15)] // usability floor: never below the idle trickle
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    public void ClampMaxFps_ZeroIsUnlimited_PositiveHasFloor(int input, int expected) => Assert.Equal(expected, GraphicsSettingsPolicy.ClampMaxFps(input));

    [Theory]
    [InlineData(10, 10)] // default (pre-0.14.2 constant) passes through
    [InlineData(1, 1)] // lower bound inclusive
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(11, 10)] // never above the P6 design cap of 10 Hz
    [InlineData(60, 10)]
    public void ClampIdleFps_StaysWithinOneToTen(int input, int expected) => Assert.Equal(expected, GraphicsSettingsPolicy.ClampIdleFps(input));

    [Theory]
    [InlineData(100, 100)] // default passes through
    [InlineData(80, 80)] // lower bound inclusive (camera pulled fully back)
    [InlineData(120, 120)] // upper bound inclusive (camera pulled fully in)
    [InlineData(79, 80)] // clamped up
    [InlineData(0, 80)] // degenerate config
    [InlineData(-25, 80)]
    [InlineData(121, 120)] // clamped down
    [InlineData(10_000, 120)]
    public void ClampCameraFillPercent_StaysWithinBounds(int input, int expected) => Assert.Equal(expected, GraphicsSettingsPolicy.ClampCameraFillPercent(input));

    [Fact]
    public void CappedFpsFloor_IsAboveTheFastestIdleTrickle() =>
        // Invariant the frame gate relies on: any positive cap interval is
        // shorter than every legal trickle interval, so the cap can never
        // starve the parked-loop safety net.
        Assert.True(GraphicsSettingsPolicy.MinCappedFps > GraphicsSettingsPolicy.MaxIdleFps);

    [Fact]
    public void Defaults_AreInsideTheirOwnBounds()
    {
        Assert.Equal(GraphicsSettingsPolicy.DefaultRenderScalePercent,
            GraphicsSettingsPolicy.ClampRenderScalePercent(GraphicsSettingsPolicy.DefaultRenderScalePercent));
        Assert.Equal(GraphicsSettingsPolicy.DefaultAnchorPreviewLeadMs,
            GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs(GraphicsSettingsPolicy.DefaultAnchorPreviewLeadMs));
        Assert.Equal(GraphicsSettingsPolicy.DefaultCameraFillPercent,
            GraphicsSettingsPolicy.ClampCameraFillPercent(GraphicsSettingsPolicy.DefaultCameraFillPercent));
    }
}
