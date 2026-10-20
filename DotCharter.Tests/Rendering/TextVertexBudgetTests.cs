using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Media;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>The text staging array and the GPU's per-frame chunk must be the same
///     number.</b>
///     <para>
///         <c>TextBatcher</c> allocated <c>new TextVertex[50000]</c> and flushed
///         only when that array filled. But <c>sizeof(TextVertex)</c> is 36 B
///         and <c>MaxTextVertexBufferSize</c> was <c>50000 * 9 * 4</c> for the
///         WHOLE buffer, which <c>BeginFrame</c> splits across
///         <c>MaxFramesInFlight</c> — 900,000 B = <b>25,000</b> vertices a
///         frame, exactly half the batcher's capacity. <c>DrawTextBatch</c>'s
///         chunk guard therefore DROPPED the whole batch, reporting it with a
///         <c>Debug.WriteLine</c> that is compiled out of Release.
///     </para>
///     <para>
///         Practically: past ~6,250 glyph quads in a frame further text batches
///         started disappearing, and a batch that hit the batcher's own flush
///         threshold was a 100% loss — a ja-JP lyric frame plus chord names plus
///         a split highway carrying two HUDs.
///     </para>
/// </summary>
public sealed class TextVertexBudgetTests
{
    /// <summary>The whole point: the batcher may never stage more than one
    /// frame's chunk holds. <b>This failed before the fix</b> (50,000 vs
    /// 25,000), which is its vacuity proof.</summary>
    [Fact]
    public void TheBatcherNeverStagesMoreThanAFrameChunkHolds()
    {
        var batcher = new TextBatcher(new FakeVulkanRenderService());

        Assert.True(batcher.Capacity <= VulkanRenderer.MaxTextVerticesPerFrame,
            $"TextBatcher stages {batcher.Capacity} vertices but a frame's text chunk holds only " +
            $"{VulkanRenderer.MaxTextVerticesPerFrame}. Its auto-flush is guaranteed to be dropped " +
            "by DrawTextBatch's chunk guard — text silently missing on screen.");

        // …and not needlessly smaller either: a batcher below the chunk just
        // flushes more often than it has to.
        Assert.Equal(VulkanRenderer.MaxTextVerticesPerFrame, batcher.Capacity);
        Assert.Equal(TextVertexBudget.PerFrameVertexCount, batcher.Capacity);
    }

    /// <summary>
    ///     The budget's byte size is a hand-written constant, so it has to be
    ///     checked against the real struct — a field added to
    ///     <see cref="TextVertex" /> would silently re-open the mismatch.
    /// </summary>
    [Fact]
    public void TheVertexSizeConstantMatchesTheStruct()
        => Assert.Equal(TextVertexBudget.VertexSizeBytes, Marshal.SizeOf<TextVertex>());

    /// <summary>The frames-in-flight the budget divides by must be the number
    /// <c>BeginFrame</c> actually chunks with.</summary>
    [Fact]
    public void TheBudgetChunksByTheRealFramesInFlight()
        => Assert.Equal(VulkanRenderer.MaxFramesInFlight, TextVertexBudget.FramesInFlight);

    /// <summary>
    ///     <b><c>Flush</c> must DISCARD its staging when it declines to draw.</b>
    ///     Returning with the count intact left staged glyphs alive across the
    ///     next <c>SetState</c>, so they were drawn with a different MVP and a
    ///     different atlas — in-world quads re-emitted under the ortho HUD
    ///     matrix, silently. That is the shape that cost three releases of
    ///     invisible chord names.
    /// </summary>
    [Fact]
    public void AFlushThatDeclinesToDrawDoesNotCarryItsGlyphsIntoTheNextState()
    {
        var service = new FakeVulkanRenderService
        {
            // An atlas with real glyphs but NO texture — Flush declines on it.
            LoadTextureReturnsNull = true
        };
        var textureless = new FontAtlas(service, "Arial", 32f);
        textureless.RebuildIfNeeded();
        Assert.Null(textureless.Texture);
        Assert.True(textureless.TryGetGlyph('W', out _), "the fixture atlas packed no glyphs");

        service.LoadTextureReturnsNull = false;
        var usable = new FontAtlas(service, "Arial", 32f);
        usable.RebuildIfNeeded();
        Assert.NotNull(usable.Texture);

        var batcher = new TextBatcher(service);
        var worldMvp = Matrix4x4.CreateTranslation(1, 2, 3);
        var hudMvp = Matrix4x4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);

        // Five glyphs staged against the world matrix, on an atlas that cannot
        // be drawn. 5 × 4 = 20 vertices.
        batcher.SetState(textureless, worldMvp);
        batcher.DrawText2D("WWWWW", Vector2.Zero, Colors.White);
        batcher.Flush();
        Assert.Empty(service.TextBatches);

        // Now the HUD pass, on the usable atlas: exactly its own one glyph.
        batcher.SetState(usable, hudMvp);
        batcher.DrawText2D("W", Vector2.Zero, Colors.White);
        batcher.Flush();

        var batch = Assert.Single(service.TextBatches);
        Assert.Equal(4, batch.Count);
        Assert.Equal(hudMvp, batch.Mvp);
        Assert.Same(usable.Texture, batch.Texture);
    }
}
