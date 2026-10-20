using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
/// Render hot path uses RenderStringCache to avoid per-frame string formatting.
/// These tests pin the cache contract: identical inputs return the *same string
/// instance* (not just equal), so the GC has no work to do across frames.
/// </summary>
public class RenderStringCacheTests
{
    [Fact]
    public void MeasureLabel_SameInputs_ReturnsSameInstance()
    {
        var a = RenderStringCache.MeasureLabel(12, 120.5, 4, 4);
        var b = RenderStringCache.MeasureLabel(12, 120.5, 4, 4);
        Assert.Same(a, b);
    }

    [Fact]
    public void MeasureLabel_FormatsExpected()
    {
        var s = RenderStringCache.MeasureLabel(7, 95.25, 6, 8);
        Assert.Contains("M7", s);
        Assert.Contains("95.25", s);
        Assert.Contains("6/8", s);
    }

    [Fact]
    public void MeasureLabel_BpmQuantizedToHundredth_HitsSameCacheEntry()
    {
        // 120.001 quantizes identically to 120.00 → must hit the same cache entry.
        var a = RenderStringCache.MeasureLabel(1, 120.001, 4, 4);
        var b = RenderStringCache.MeasureLabel(1, 120.0, 4, 4);
        Assert.Same(a, b);
    }

    [Fact]
    public void MeasureLabel_DifferentInputs_ReturnDifferentStrings()
    {
        var a = RenderStringCache.MeasureLabel(1, 120.0, 4, 4);
        var b = RenderStringCache.MeasureLabel(2, 120.0, 4, 4);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fps_BoundedRange_PreallocatedSameInstance() => Assert.Same(RenderStringCache.Fps(60), RenderStringCache.Fps(60));

    [Fact]
    public void Fret_BoundedRange_PreallocatedSameInstance() => Assert.Same(RenderStringCache.Fret(5), RenderStringCache.Fret(5));

    [Fact]
    public void MmSsFf_QuantizedTo10Ms_SameInstance() =>
        // Both 12345 and 12349 ms round to the same 10-ms bucket → same cached instance.
        Assert.Same(RenderStringCache.MmSsFf(12345), RenderStringCache.MmSsFf(12349));

    /// <summary>
    ///     The 10 ms bucket means one new key per 10 ms of SONG, so the old
    ///     `Count &lt; 16384` refusal stopped caching 163.84 s (2:44) in and every
    ///     later frame formatted a fresh <see cref="TimeSpan" /> on three callers
    ///     (PlayheadLayer, CursorLayer, StatusBarViewModel). The cap is a
    ///     clear-and-continue now, so the dictionary keeps working for the whole
    ///     song. Structural assertion only — nothing here reads a clock.
    /// </summary>
    [Fact]
    public void MmSsFf_PastTheOldCap_StillCachesInsteadOfFormattingEveryCall()
    {
        // 30,000 distinct 10-ms keys = 300 s of song, well past the 16,384 cap.
        for (var k = 0; k < 30_000; k++) RenderStringCache.MmSsFf(k * 10.0);

        const double recent = 29_990 * 10.0;
        var first = RenderStringCache.MmSsFf(recent);
        // A different key displaces the one-entry memo, so the second lookup has
        // to come out of the DICTIONARY — which is the half that used to die.
        RenderStringCache.MmSsFf(29_991 * 10.0);
        var second = RenderStringCache.MmSsFf(recent);

        Assert.Same(first, second);
    }

    [Fact]
    public void MmSsFf_TheCacheStaysBounded_ByClearingRatherThanRefusing()
    {
        for (var k = 0; k < 30_000; k++) RenderStringCache.MmSsFf(k * 10.0);

        // Guards the OTHER way of "keep caching past the cap" — letting the
        // dictionary grow without bound. (The case above is what proves the
        // clear-and-continue; this one only pins the ceiling.)
        Assert.InRange(RenderStringCache.MmSsFfCacheCount, 1, 16_384);
    }

    [Fact]
    public void MmSsFf_KeepsAOneEntryMemoInFrontOfTheDictionary()
    {
        // At 60 fps the 10-ms key only moves every ~6 frames, so most frames are
        // served by the memo and never touch the ConcurrentDictionary at all.
        var a = RenderStringCache.MmSsFf(7_777);
        Assert.Equal(777, RenderStringCache.MmSsFfMemoKey);

        var b = RenderStringCache.MmSsFf(7_771);
        Assert.Same(a, b);
        Assert.Equal(777, RenderStringCache.MmSsFfMemoKey);

        RenderStringCache.MmSsFf(9_999);
        Assert.Equal(999, RenderStringCache.MmSsFfMemoKey);
    }
}
