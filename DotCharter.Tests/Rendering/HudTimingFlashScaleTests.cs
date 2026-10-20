using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The EARLY / LATE and PERFECT / GREAT / GOOD flashes were fixed pixel
///     scales, so at 4K on a 200 % display (the preview renders at physical
///     pixels) they were half the intended size, and in a small docked preview
///     they swamped the highway. They scale with the frame height now, like the
///     HUD's section, phrase, tone and lyric lines.
/// </summary>
public sealed class HudTimingFlashScaleTests
{
    [Fact]
    public void The_flash_is_twice_as_tall_on_a_frame_twice_as_tall()
    {
        var at1080 = FlashHeight(1920, 1080);
        var at2160 = FlashHeight(3840, 2160);

        Assert.True(at1080 > 5, $"the fixture drew no EARLY flash ({at1080}px)");
        Assert.InRange(at2160 / at1080, 1.9, 2.1);
    }

    [Fact]
    public void The_scale_is_unchanged_at_1080_and_clamped_at_the_extremes()
    {
        Assert.Equal(0.42f, HudComponent.HeightRelativeScale(0.42f, 1080), 4);
        Assert.Equal(0.21f, HudComponent.HeightRelativeScale(0.42f, 200), 4);
        Assert.Equal(1.26f, HudComponent.HeightRelativeScale(0.42f, 9000), 4);
    }

    private static float FlashHeight(int width, int height)
    {
        var service = new FakeVulkanRenderService();
        var atlas = new FontAtlas(service, null, 64f);
        atlas.RegisterCharacters("EARLY");
        atlas.RebuildIfNeeded();
        var text = new TextBatcher(service);
        text.SetState(atlas, System.Numerics.Matrix4x4.Identity);

        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            LivePlayActive = true,
            LastTimingDirection = 1, // EARLY
            LastTimingAtMs = 1_000
        };
        new HudComponent().Render(new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = 1_010,
            QuadBatcher = new QuadBatcher(service),
            TextBatcher = text,
            ScreenWidth = width,
            ScreenHeight = height
        });
        text.Flush();

        // The flash is the tallest run of glyphs this pass draws (the stats line
        // is empty here and nothing else is live).
        return service.TextBatchYSpans.Select(s => s.MaxY - s.MinY).DefaultIfEmpty(0).Max();
    }
}
