namespace DotCharter.Rendering.Vulkan.Composed;

/// <summary>Lifecycle state of one exportable composed image.</summary>
public enum ComposedImageState
{
    /// <summary>Available for the next frame (initial state; compositor is
    /// done with the previous contents — its keyed-mutex release to key 0
    /// was confirmed by the completed present task).</summary>
    Free = 0,

    /// <summary>The render thread is recording/submitting into it (BeginFrame
    /// picked it; its keyed-mutex release to key 1 rides the submit).</summary>
    Rendering = 1,

    /// <summary>Submitted and handed to the compositor; waiting for the
    /// present task to complete before it can be reused.</summary>
    AwaitingPresent = 2,

    /// <summary>Its present faulted (device lost, surface torn down mid
    /// flight). Never reused — the pool must be rebuilt.</summary>
    Broken = 3
}

/// <summary>
///     Pure image-pick rule for the composed-output image pool (0.16). Three
///     images mirror the classic triple-buffered swapchain: one being
///     rendered, one queued in the compositor, one free — so the render
///     thread never stalls on a healthy compositor, and a slow compositor
///     back-pressures the loop to its own rate (no unbounded queueing). No
///     Vulkan calls — unit-tested in <c>ComposedImagePoolPolicyTests</c>.
/// </summary>
public static class ComposedImagePoolPolicy
{
    /// <summary>Pool depth. Two is the minimum that overlaps render and
    /// composition; three absorbs one frame of compositor jitter.</summary>
    public const int ImageCount = 3;

    /// <summary>
    ///     Picks the next image to render into: round-robin starting after
    ///     <paramref name="lastAcquired" /> so frames rotate through the pool
    ///     (steady state stays FIFO — the compositor consumes in submission
    ///     order). -1 = nothing free (compositor owns everything still
    ///     pending) — the caller skips the frame and retries.
    /// </summary>
    public static int PickNext(ReadOnlySpan<ComposedImageState> states, int lastAcquired)
    {
        if (states.Length == 0) return -1;
        for (var offset = 1; offset <= states.Length; offset++)
        {
            var i = (lastAcquired + offset) % states.Length;
            if (i < 0) i += states.Length;
            if (states[i] == ComposedImageState.Free) return i;
        }

        return -1;
    }

    /// <summary>True when any image is broken — the whole pool must be
    /// recreated (a broken image never heals; its keyed mutex state is
    /// unknowable).</summary>
    public static bool NeedsRebuild(ReadOnlySpan<ComposedImageState> states)
    {
        foreach (var s in states)
        {
            if (s == ComposedImageState.Broken)
                return true;
        }

        return false;
    }
}
