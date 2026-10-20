namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     The ONE place the text stream's size is decided, so the CPU staging
///     array and the GPU's per-frame chunk cannot disagree.
///     <para>
///         They did. <c>TextBatcher</c> staged 50,000 vertices and flushed only
///         when that array filled, but <c>MaxTextVertexBufferSize</c> was
///         <c>50000 * 9 * 4</c> for the WHOLE buffer, which
///         <c>BeginFrame</c> then splits across <c>MaxFramesInFlight</c> — so a
///         frame's chunk held 900,000 bytes = 25,000 vertices, exactly HALF the
///         batcher's capacity. <c>DrawTextBatch</c>'s chunk guard therefore
///         DROPPED the entire batch, and the only report was a
///         <c>Debug.WriteLine</c>, which is compiled out of Release. A batch
///         that hit the batcher's own auto-flush threshold was a 100% loss;
///         past ~6,250 glyph quads in a frame, further text batches started
///         disappearing. Worst case is a ja-JP lyric frame plus chord names
///         plus a split highway carrying two HUDs.
///     </para>
///     <para>
///         The buffer is sized per FRAME now, so the batcher's auto-flush
///         reaches the GPU. 50,000 vertices × 36 B × 2 frames = 3.6 MB of
///         persistently-mapped host-visible memory.
///     </para>
/// </summary>
public static class TextVertexBudget
{
    /// <summary><c>sizeof(TextVertex)</c> — Vector3 + Vector4 + Vector2.
    /// Asserted against the real struct by <c>TextVertexBudgetTests</c>,
    /// because a field added to <see cref="TextVertex" /> silently re-opens the
    /// mismatch this type exists to close.</summary>
    public const int VertexSizeBytes = 36;

    /// <summary>Vertices ONE frame may stage — the batcher's array length and
    /// the per-frame GPU chunk, by construction the same number.</summary>
    public const int PerFrameVertexCount = 50_000;

    /// <summary>Mirrors <c>VulkanRenderer.MaxFramesInFlight</c>; the chunking in
    /// <c>BeginFrame</c> divides by it.</summary>
    public const int FramesInFlight = VulkanRenderer.MaxFramesInFlight;

    /// <summary>Total size of the persistently-mapped text vertex buffer.</summary>
    public const ulong TotalBufferSizeBytes =
        (ulong)PerFrameVertexCount * VertexSizeBytes * FramesInFlight;
}
