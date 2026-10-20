using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     <see cref="ChartTextRegistrationLatch" /> is <c>Preview3DRenderDriver</c>'s
///     font-atlas latch. It exists as its own type because its two references
///     must be WEAK: it lives as long as the 3D canvas, and the collections it
///     latches are the OPEN song's own — the active arrangement's
///     <c>EventPoints</c> and the open vocal track's <c>Phrases</c>. Held
///     strongly it kept every lyric word and every section marker of every song
///     the session ever previewed.
///     <para>
///         Both halves are pinned: the collectability probe (the house pattern
///         from <see cref="ProjectSwitchForgetsTheClosedProjectTests" /> —
///         <see cref="WeakReference" /> plus a collect, set up in its own frame),
///         and the behaviour the latch exists for, so the fix cannot be
///         "satisfied" by a latch that always says yes.
///     </para>
/// </summary>
public sealed class ChartTextRegistrationLatchTests
{
    [Fact]
    public void AnAcceptedPair_IsNotRegisteredAgain()
    {
        var latch = new ChartTextRegistrationLatch();
        var lyrics = new object();
        var events = new object();

        Assert.True(latch.NeedsRegistration(lyrics, 3, events, 5));
        latch.Accept(lyrics, 3, events, 5);

        Assert.False(latch.NeedsRegistration(lyrics, 3, events, 5));
    }

    [Fact]
    public void AChangedInstanceOrCount_IsRegisteredAgain()
    {
        var latch = new ChartTextRegistrationLatch();
        var lyrics = new object();
        var events = new object();
        latch.Accept(lyrics, 3, events, 5);

        Assert.True(latch.NeedsRegistration(new object(), 3, events, 5));
        Assert.True(latch.NeedsRegistration(lyrics, 4, events, 5));
        Assert.True(latch.NeedsRegistration(lyrics, 3, new object(), 5));
        Assert.True(latch.NeedsRegistration(lyrics, 3, events, 6));
    }

    /// <summary>
    ///     A registration that FAILED must not latch, or the atlas never gets
    ///     the characters and the hitch this type exists to remove comes back as
    ///     a permanently missing glyph instead.
    /// </summary>
    [Fact]
    public void WithoutAccept_TheSamePairIsStillPending()
    {
        var latch = new ChartTextRegistrationLatch();
        var lyrics = new object();
        var events = new object();

        Assert.True(latch.NeedsRegistration(lyrics, 3, events, 5));
        Assert.True(latch.NeedsRegistration(lyrics, 3, events, 5));
    }

    /// <summary>The one that was the leak: what the latch accepted must not
    /// outlive the caller's own reference to it.</summary>
    [Fact]
    public void TheLatchedCollections_AreCollectableOnceTheChartIsClosed()
    {
        var latch = new ChartTextRegistrationLatch();
        var (lyrics, events) = LatchThenAbandon(latch);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(lyrics.IsAlive,
            "the closed chart's lyric collection is still reachable through the atlas latch.");
        Assert.False(events.IsAlive,
            "the closed chart's event-point collection is still reachable through the atlas latch.");
    }

    /// <summary>
    ///     …and a collected target must read as CHANGED, never as a match — a
    ///     latch that answered "same" against a dead reference would skip the
    ///     registration the new chart needs.
    /// </summary>
    [Fact]
    public void AfterTheLatchedCollectionsAreGone_TheNextPairNeedsRegistration()
    {
        var latch = new ChartTextRegistrationLatch();
        LatchThenAbandon(latch);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(latch.NeedsRegistration(new object(), 3, new object(), 5));
    }

    /// <summary>Kept out of the caller's frame so the objects it creates cannot
    /// be held alive by a stack slot the JIT has not yet reused.</summary>
    private static (WeakReference Lyrics, WeakReference Events) LatchThenAbandon(
        ChartTextRegistrationLatch latch)
    {
        var lyrics = new object();
        var events = new object();
        latch.Accept(lyrics, 3, events, 5);
        return (new WeakReference(lyrics), new WeakReference(events));
    }

    /// <summary>The 2D timeline latches a THIRD collection (tone badges): a
    /// changed tone set re-registers even when lyrics and events are unchanged.</summary>
    [Fact]
    public void TheThreeCollectionForm_TracksTonesToo()
    {
        var latch = new ChartTextRegistrationLatch();
        var lyrics = new object();
        var events = new object();
        var tones = new object();

        Assert.True(latch.NeedsRegistration(lyrics, 1, events, 2, tones, 3));
        latch.Accept(lyrics, 1, events, 2, tones, 3);
        Assert.False(latch.NeedsRegistration(lyrics, 1, events, 2, tones, 3));

        Assert.True(latch.NeedsRegistration(lyrics, 1, events, 2, tones, 4));
        Assert.True(latch.NeedsRegistration(lyrics, 1, events, 2, new object(), 3));
        // …and the two-collection form the 3D driver uses is untouched by tones.
        Assert.False(latch.NeedsRegistration(lyrics, 1, events, 2));
    }

    /// <summary>A project switch empties the atlas back to its seed set; the
    /// latch must forget with it, or a new project whose collections happen to
    /// be the same instances at the same counts never gets registered.</summary>
    [Fact]
    public void Reset_ForgetsEverythingAccepted()
    {
        var latch = new ChartTextRegistrationLatch();
        var lyrics = new object();
        var events = new object();
        var tones = new object();
        latch.Accept(lyrics, 1, events, 2, tones, 3);
        Assert.False(latch.NeedsRegistration(lyrics, 1, events, 2, tones, 3));

        latch.Reset();

        Assert.True(latch.NeedsRegistration(lyrics, 1, events, 2, tones, 3));
        Assert.True(latch.NeedsRegistration(lyrics, 1, events, 2));
    }
}
