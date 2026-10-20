using DotCharter.Rendering.Data;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 0.20 timing-tier live flash: PERFECT / GREAT / GOOD over the highway at
///     the instant of the hit. These pin the two traps the feature exists to avoid:
///     <list type="number">
///         <item>tier None (a miss, and a CQT pull-verified hit — neither has an
///               honest attack time) must NEVER produce a flash, or the HUD would
///               fabricate a timing claim; and</item>
///         <item>the flash is DRIVER-OWNED sticky state re-stamped into the
///               ping-ponged snapshot every frame, so it survives the frames after
///               the judged event was drained rather than strobing away.</item>
///     </list>
///     The decision lives in the pure <see cref="TimingFlashMath"/> (which the
///     render driver and <c>HudComponent</c> both call), so testing it here tests
///     the production path without standing up the Vulkan render loop.
/// </summary>
public sealed class TimingTierFlashTests
{
    // LiveTimingTier ordinals, as the driver/HUD see them once cast to byte.
    private const byte None = (byte)LiveTimingTier.None;      // 0
    private const byte Good = (byte)LiveTimingTier.Good;      // 1
    private const byte Great = (byte)LiveTimingTier.Great;    // 2
    private const byte Perfect = (byte)LiveTimingTier.Perfect;// 3

    private static ExpectedSound Sound(double timeMs = 1000) =>
        new() { TimeMs = timeMs, StringIndex = 0, Fret = 3 };

    // The factory LivePlayViewModel.OnJudged enqueues through, so a change to the
    // wiring shows up as a failing Tier assertion here.
    private static LivePlayFeedbackEvent Feedback(LivePlayEvent ev) => LivePlayFeedbackEvent.ForJudgment(ev);

    // Mirrors the driver's every-frame re-stamp: the flash is blanked whenever
    // Live Play is inactive, otherwise the driver-owned sticky is copied across.
    private static void Stamp(FrameSnapshot snap, bool livePlayActive, byte stickyTier, double stickyAtMs)
    {
        snap.LastTimingTier = livePlayActive ? stickyTier : (byte)0;
        snap.LastTimingTierAtMs = livePlayActive ? stickyAtMs : double.NegativeInfinity;
    }

    // ---- ShouldFlash: the HUD's draw gate ----------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(-1)]
    [InlineData(10_000)]
    public void ShouldFlash_None_NeverFlashes_AtAnyAge(double ageMs)
        => Assert.False(TimingFlashMath.ShouldFlash(None, ageMs));

    [Theory]
    [InlineData(Good)]
    [InlineData(Great)]
    [InlineData(Perfect)]
    public void ShouldFlash_RealTier_TrueInsideWindow_FalseOutside(byte tier)
    {
        Assert.True(TimingFlashMath.ShouldFlash(tier, 0));                            // the instant of the hit
        Assert.True(TimingFlashMath.ShouldFlash(tier, TimingFlashMath.FlashDurationMs)); // inclusive fade edge
        Assert.False(TimingFlashMath.ShouldFlash(tier, -1));                          // seeked back behind the hit
        Assert.False(TimingFlashMath.ShouldFlash(tier, TimingFlashMath.FlashDurationMs + 1)); // fully faded
    }

    // ---- NextSticky: the driver's sticky-update rule -----------------------

    [Fact]
    public void NextSticky_RealTier_ReplacesStickyState()
    {
        var (tier, atMs) = TimingFlashMath.NextSticky(None, double.NegativeInfinity, Perfect, 1005);
        Assert.Equal(Perfect, tier);
        Assert.Equal(1005, atMs);
    }

    [Fact]
    public void NextSticky_None_LeavesTheLiveFlashUntouched()
    {
        // A Great flash is live; a miss (None) arrives right after it.
        var (tier, atMs) = TimingFlashMath.NextSticky(Great, 1005, None, 1100);
        Assert.Equal(Great, tier);   // the miss did not blank the cue...
        Assert.Equal(1005, atMs);    // ...nor reset its clock.
    }

    // ---- Wiring: LivePlayEvent.Tier -> LivePlayFeedbackEvent.Tier ----------

    [Theory]
    [InlineData(LivePlayVerdict.Miss, LiveTimingTier.None)]           // a miss carries no tier
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.None)]            // a CQT pull-verified hit: WHAT, not WHEN
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.Good)]
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.Great)]
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.Perfect)]
    public void Feedback_CarriesTheJudgedTier(LivePlayVerdict verdict, LiveTimingTier tier)
    {
        var ev = new LivePlayEvent(verdict, Sound(), DetectedAtMs: 1005, Confidence: 0.9f, Tier: tier);
        Assert.Equal(tier, Feedback(ev).Tier);
    }

    /// <summary>The two None cases — a miss and a CQT pull-verified hit — must not
    /// leave a flash-eligible snapshot value, even though the CQT case is a genuine
    /// HIT. A tier flashed there would be a lie about the player's timing.</summary>
    [Theory]
    [InlineData(LivePlayVerdict.Miss)]
    [InlineData(LivePlayVerdict.Hit)]
    public void UntimedJudgment_ProducesNoFlash(LivePlayVerdict verdict)
    {
        var fb = Feedback(new LivePlayEvent(verdict, Sound(), DetectedAtMs: 2000, Tier: LiveTimingTier.None));

        // Drain into the driver's sticky state (empty at run start), then stamp the frame.
        var (tier, atMs) = TimingFlashMath.NextSticky(None, double.NegativeInfinity, (byte)fb.Tier, fb.JudgedAtMs);
        var snap = new FrameSnapshot { LivePlayActive = true };
        Stamp(snap, livePlayActive: true, tier, atMs);

        Assert.Equal(None, snap.LastTimingTier);
        Assert.False(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 2001 - snap.LastTimingTierAtMs));
    }

    [Fact]
    public void RealHit_ProducesFlashEligibleSnapshot()
    {
        var fb = Feedback(new LivePlayEvent(
            LivePlayVerdict.Hit, Sound(), DetectedAtMs: 1005, Confidence: 0.95f, Tier: LiveTimingTier.Perfect));

        var (tier, atMs) = TimingFlashMath.NextSticky(None, double.NegativeInfinity, (byte)fb.Tier, fb.JudgedAtMs);
        var snap = new FrameSnapshot { LivePlayActive = true };
        Stamp(snap, livePlayActive: true, tier, atMs);

        Assert.Equal(Perfect, snap.LastTimingTier);
        Assert.True(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 1005 - snap.LastTimingTierAtMs)); // age 0
    }

    /// <summary>The load-bearing re-stamp: the flash is sticky DRIVER state copied
    /// into the ping-ponged snapshot every frame, so it survives the frames after
    /// its event was drained — and a following miss must not blank it.</summary>
    [Fact]
    public void StickyFlash_SurvivesLaterFramesAndAFollowingMiss()
    {
        var tier = None;
        var atMs = double.NegativeInfinity;
        var snap = new FrameSnapshot { LivePlayActive = true };

        // Frame 0 (song t=1005): a Perfect is drained.
        var hit = Feedback(new LivePlayEvent(LivePlayVerdict.Hit, Sound(), 1005, Tier: LiveTimingTier.Perfect));
        (tier, atMs) = TimingFlashMath.NextSticky(tier, atMs, (byte)hit.Tier, hit.JudgedAtMs);
        Stamp(snap, true, tier, atMs);
        Assert.True(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 1005 - snap.LastTimingTierAtMs));

        // Frames 1..N (song t=1300): NO event drained — the sticky is unchanged and
        // re-stamped, so the flash is still up part-way through its fade.
        Stamp(snap, true, tier, atMs);
        Assert.True(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 1300 - snap.LastTimingTierAtMs));

        // A miss lands (song t=1400): None must NOT clear the still-live Perfect.
        var miss = Feedback(new LivePlayEvent(LivePlayVerdict.Miss, Sound(1400), 1400, Tier: LiveTimingTier.None));
        (tier, atMs) = TimingFlashMath.NextSticky(tier, atMs, (byte)miss.Tier, miss.JudgedAtMs);
        Stamp(snap, true, tier, atMs);
        Assert.Equal(Perfect, snap.LastTimingTier);
        Assert.Equal(1005, snap.LastTimingTierAtMs);
        Assert.True(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 1500 - snap.LastTimingTierAtMs)); // age 495 < 600

        // Well past the fade window (song t=1700, age 695): nothing is drawn.
        Assert.False(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 1700 - snap.LastTimingTierAtMs));
    }

    /// <summary>While Live Play is inactive the snapshot byte is forced to 0 every
    /// frame, so a stale tier from a finished run can never flash.</summary>
    [Fact]
    public void InactiveLivePlay_StampsZero()
    {
        var snap = new FrameSnapshot { LivePlayActive = false };
        Stamp(snap, livePlayActive: false, stickyTier: Perfect, stickyAtMs: 1005);

        Assert.Equal(None, snap.LastTimingTier);
        Assert.False(TimingFlashMath.ShouldFlash(snap.LastTimingTier, 0));
    }
}
