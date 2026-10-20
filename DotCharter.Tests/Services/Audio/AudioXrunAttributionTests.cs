using DotCharter.Tone.Audio.Diagnostics;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The owner's original request for audio-health logging was "I get some
///     [dropouts] and don't know the cause". The managed
///     <see cref="AudioDropoutDetector" /> could only ever report the SYMPTOM —
///     the render clock went stale — because from outside the callback "someone
///     stalled us" and "we ran long" look identical.
///
///     <para>
///         ABI v30's counters separate them, and these pin the separation. All
///         pure: constructed snapshots, no clock, no device, no engine.
///     </para>
/// </summary>
public sealed class AudioXrunAttributionTests
{
    private static XrunStats Snap(
        long callbacks = 0, long late = 0, long overruns = 0, long nearMisses = 0,
        int? deviceXruns = null, long lastCallbackNs = 0, long budgetNs = 0,
        long maxCallbackNs = 0) => new()
        {
            DeviceOpen = true,
            DeviceRunning = true,
            Callbacks = callbacks,
            LateCallbacks = late,
            DeadlineOverruns = overruns,
            NearMisses = nearMisses,
            DeviceXruns = deviceXruns,
            LastCallbackNs = lastCallbackNs,
            BlockBudgetNs = budgetNs,
            MaxCallbackNs = maxCallbackNs
        };

    /// <summary>The block budget the owner's report was measured at: 128 frames
    /// at 48 kHz is 2.667 ms, so ~11,250 callbacks fill one 30 s heartbeat.</summary>
    private const long Budget128At48K = 2_666_667;

    // --------------------------------------------------------------- the delta

    [Fact]
    public void AQuietWindow_HasNoDropoutsAndNoCause()
    {
        var w = AudioXrunAttribution.Delta(Snap(callbacks: 1000), Snap(callbacks: 6000));

        Assert.False(w.HasDropouts);
        Assert.Equal(DropoutCause.None, w.Cause);
        Assert.Equal(5000, w.Callbacks);
    }

    /// <summary>
    ///     THE POINT. Late callbacks with NO overruns of our own means something
    ///     outside the process stalled the audio thread — telling the user to
    ///     lighten their tone rig would send them to fix the wrong thing.
    /// </summary>
    [Fact]
    public void LateWithoutOverruns_BlamesTheMachineNotTheRig()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000, late: 2),
            Snap(callbacks: 6000, late: 9));

        Assert.Equal(7, w.LateCallbacks);
        Assert.Equal(0, w.DeadlineOverruns);
        Assert.Equal(DropoutCause.Machine, w.Cause);

        var text = AudioXrunAttribution.Describe(w, Snap(), 48000, 256);
        Assert.Contains("OUTSIDE this app", text);
        Assert.DoesNotContain("too heavy", text);
    }

    /// <summary>
    ///     The mirror: our callbacks overran their own budget one-for-one with the
    ///     audible dropouts, so the rig IS the attributed cause — and here the
    ///     overruns are frequent enough (600 of 5,000 blocks = 12%) to justify the
    ///     advice as well. Both halves have to hold before the loud line prints;
    ///     see <see cref="ARareOverrunRate_NeverTellsTheUserToRemoveGear" /> for
    ///     the same attribution WITHOUT the rate.
    /// </summary>
    [Fact]
    public void SustainedOverrunsTrackingTheLates_BlameOurOwnRig()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000),
            Snap(callbacks: 6000, late: 600, overruns: 600));

        Assert.Equal(DropoutCause.OurRig, w.Cause);
        Assert.True(w.OverrunsAreSustained);

        var text = AudioXrunAttribution.Describe(w, Snap(), 48000, 256);
        Assert.Contains("too heavy", text);
        Assert.Contains("Raise the buffer size", text);
    }

    [Fact]
    public void OverrunsExplainingSomeButNotMost_BlamesBoth()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000),
            Snap(callbacks: 6000, late: 10, overruns: 4));

        Assert.Equal(DropoutCause.Both, w.Cause);
    }

    /// <summary>A single incidental overrun among many outside stalls is not the
    /// rig's fault — below the share threshold it stays the machine.</summary>
    [Fact]
    public void OneIncidentalOverrunAmongManyStalls_StaysTheMachine()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000),
            Snap(callbacks: 6000, late: 20, overruns: 1));

        Assert.Equal(DropoutCause.Machine, w.Cause);
    }

    // ------------------------------------------------------- the two sentinels

    /// <summary>
    ///     "The driver cannot report xruns" and "the driver reported none" are
    ///     opposite claims, and the absent one must survive the subtraction as
    ///     absent rather than becoming a 0 somebody prints.
    /// </summary>
    [Fact]
    public void ADriverThatCannotReport_StaysAbsentAcrossTheDelta()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000, deviceXruns: null),
            Snap(callbacks: 6000, deviceXruns: null));

        Assert.Null(w.DeviceXruns);
        Assert.Contains("driver cannot report", AudioXrunAttribution.Describe(w, Snap(), 48000, 256));
    }

    [Fact]
    public void ADriverThatDoesReport_SubtractsAndIsNamed()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 1000, deviceXruns: 3),
            Snap(callbacks: 6000, deviceXruns: 5));

        Assert.Equal(2, w.DeviceXruns);
        Assert.True(w.HasDropouts); // the driver alone is enough
        Assert.Contains("driver reported 2", AudioXrunAttribution.Describe(w, Snap(), 48000, 256));
    }

    /// <summary>
    ///     The engine REBASES every counter when the device (re)starts — a device
    ///     change, a 48 kHz NAM request or a watchdog recovery. Subtracting across
    ///     that would go negative and read as a window that healed itself.
    /// </summary>
    [Fact]
    public void ADeviceRestartRebasesTheCounters_AndIsSaidSoRatherThanGoingNegative()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 500_000, late: 40, overruns: 12),
            Snap(callbacks: 120, late: 1)); // engine rebased under us

        Assert.True(w.CountersRebased);
        Assert.Equal(1, w.LateCallbacks);
        Assert.Equal(120, w.Callbacks);
        Assert.True(w.LateCallbacks >= 0 && w.Callbacks >= 0);
        Assert.Contains("rebased by a device restart", AudioXrunAttribution.Describe(w, Snap(), 48000, 256));
    }

    // ------------------------------------------------------------ the load line

    /// <summary>Before the first block the budget is 0, and an unguarded divide
    /// would print an infinite load for a device that has not started.</summary>
    [Fact]
    public void LoadIsOmittedUntilABlockHasBeenRendered()
    {
        var w = AudioXrunAttribution.Delta(Snap(), Snap(late: 1));

        Assert.DoesNotContain("block budget", AudioXrunAttribution.Describe(w, Snap(lastCallbackNs: 900), 48000, 256));
        Assert.Contains("% of its block budget",
            AudioXrunAttribution.Describe(w, Snap(lastCallbackNs: 4_000_000, budgetNs: 5_333_000), 48000, 256));
    }

    /// <summary>Near misses are a load warning, not a dropout, so they must never
    /// make a quiet window look like a bad one.</summary>
    [Fact]
    public void NearMissesAreNotDropouts()
    {
        var w = AudioXrunAttribution.Delta(Snap(), Snap(callbacks: 5000, nearMisses: 40));

        Assert.False(w.HasDropouts);
        Assert.Equal(DropoutCause.None, w.Cause);
    }

    // ------------------------------------------ the line the owner actually read
    //
    // Everything below pins one fault in the message the owner saw during plain
    // song playback with no live input and nothing but playback running:
    //
    //   "Audio health: 6 audible dropout(s) at 48000 Hz / 128 frames, and 16 of
    //    our own callbacks overran their block budget — OUR audio processing is
    //    too heavy for this buffer size. Raise the buffer size or remove DSP/VST
    //    gear, last callback used 11% of its block budget, driver cannot report
    //    its own xruns."
    //
    // Self-contradictory (11% of budget while "too heavy"), and the verdict was
    // drawn from 16 overruns in ~11,250 blocks — 0.14%.

    /// <summary>The exact reported window, as one helper so every test below
    /// argues about the same numbers: 6 lates and 16 overruns across 11,250
    /// callbacks (one 30 s heartbeat at 48 kHz / 128 frames), with the sampled
    /// callback sitting at 11% of its budget.</summary>
    private static AudioXrunAttribution.Window TheReportedWindow() =>
        AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 111_250, late: 6, overruns: 16));

    private static XrunStats TheReportedSnapshot(long maxCallbackNs = 0) =>
        Snap(lastCallbackNs: 293_333, budgetNs: Budget128At48K, maxCallbackNs: maxCallbackNs);

    /// <summary>
    ///     THE REPORTED BUG. 16 overruns in ~11,250 blocks is 0.14% — one block in
    ///     seven hundred — and the line told the owner his rig was too heavy and to
    ///     remove DSP/VST gear. Gear heavy enough to matter overruns MOST blocks;
    ///     it cannot render 11,234 of them on time and then be the reason the other
    ///     16 ran long. The attribution may stay <see cref="DropoutCause.OurRig" />
    ///     (the overruns do out-number the lates), but the advice may not print.
    /// </summary>
    [Fact]
    public void ARareOverrunRate_NeverTellsTheUserToRemoveGear()
    {
        var w = TheReportedWindow();

        Assert.Equal(DropoutCause.OurRig, w.Cause);   // attribution unchanged…
        Assert.False(w.OverrunsAreSustained);          // …severity is the new gate

        var text = AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128);

        Assert.DoesNotContain("remove DSP/VST gear", text);
        Assert.DoesNotContain("too heavy", text);
        Assert.DoesNotContain("Raise the buffer size", text);

        // …and it still reports honestly what happened.
        Assert.Contains("6 audible dropout(s)", text);
        Assert.Contains("16 of 11250 callback(s)", text);
        Assert.Contains("0.14% of blocks", text);
    }

    /// <summary>
    ///     The floor is a RATE, not a count: the same 16 overruns in a window of
    ///     800 blocks (2%) is a rig that really is too heavy for the buffer, and
    ///     the advice must still reach that user. A floor that silenced the line
    ///     everywhere would be a worse bug than the one it fixed.
    /// </summary>
    [Fact]
    public void ASustainedOverrunRate_StillTellsTheUserToRemoveGear()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 100_800, late: 6, overruns: 16));

        Assert.True(w.OverrunsAreSustained);

        var text = AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128);
        Assert.Contains("too heavy", text);
        Assert.Contains("remove DSP/VST gear", text);
        Assert.Contains("2.0% of blocks", text);
    }

    /// <summary>
    ///     The floor sits where it does for a physical reason, so pin the shape of
    ///     the curve rather than the constant alone: just under it stays quiet,
    ///     just on it speaks. A future edit that "rounds" the threshold to
    ///     something convenient has to come past this.
    /// </summary>
    [Theory]
    [InlineData(99, false)]    // 0.99% of 10,000 blocks — below the floor
    [InlineData(100, true)]    // exactly 1%
    [InlineData(5000, true)]   // half of all blocks: unmistakably the rig
    public void TheAdviceFollowsTheOverrunRate_NotTheOverrunCount(long overruns, bool expectAdvice)
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 110_000, late: overruns, overruns: overruns));

        Assert.Equal(expectAdvice, w.OverrunsAreSustained);
        Assert.Equal(expectAdvice,
            AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128).Contains("remove DSP/VST gear"));
    }

    /// <summary>
    ///     A window with no callback count cannot yield a rate, and the two ways
    ///     to get that wrong are both fatal: dividing by zero, or treating the
    ///     un-computable rate as 0 (silently acquitting) or as huge (silently
    ///     convicting). It must stay <c>null</c>, keep its attribution, say out
    ///     loud that there is no rate, and give no advice.
    /// </summary>
    [Fact]
    public void AWindowWithNoCallbackCount_HasNoRateAndGivesNoAdvice()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 0),
            Snap(callbacks: 0, late: 6, overruns: 16));

        Assert.Equal(0, w.Callbacks);
        Assert.Null(w.OverrunRate);                  // not 0, not infinity
        Assert.False(w.OverrunsAreSustained);
        Assert.Equal(DropoutCause.OurRig, w.Cause);  // NOT silently reclassified

        var text = AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128);
        Assert.DoesNotContain("remove DSP/VST gear", text);
        Assert.Contains("no callback count", text);
        Assert.Contains("16 of our callbacks", text);
    }

    /// <summary>
    ///     The window counts are DELTAS over ~30 s; <see cref="XrunStats.Load" />
    ///     is ONE callback sampled when the heartbeat fired, possibly 30 s after
    ///     every event the window describes. Printed side by side with no label
    ///     they read as a contradiction ("16 callbacks overran" … "11% of budget")
    ///     and a reader has no way to know they describe different callbacks. The
    ///     line must SAY which is which, and in that order.
    /// </summary>
    [Fact]
    public void TheWindowDeltasAndTheSampledCallback_AreLabelledAsDifferentMeasurements()
    {
        var text = AudioXrunAttribution.Describe(
            TheReportedWindow(), TheReportedSnapshot(), 48000, 128);

        var window = text.IndexOf("This window:", StringComparison.Ordinal);
        var sampled = text.IndexOf("Sampled now:", StringComparison.Ordinal);
        Assert.True(window >= 0, $"no window label in: {text}");
        Assert.True(sampled > window, $"no sampled-now label after the window section in: {text}");

        // The counts belong to the window half, the instantaneous load to the other.
        Assert.True(text.IndexOf("16 of 11250", StringComparison.Ordinal) < sampled);
        Assert.True(text.IndexOf("11% of its block budget", StringComparison.Ordinal) > sampled);

        // …and it stays ONE log line.
        Assert.DoesNotContain("\n", text);
    }

    /// <summary>
    ///     <see cref="XrunStats.MaxCallbackNs" /> is the ONLY number in the
    ///     snapshot that can support or refute "too heavy", and it went unprinted
    ///     for the whole life of this type while the line made the claim anyway.
    ///     Print it as a share of the block budget — and label it, because it is a
    ///     high-water mark since the counters were reset, not a window statistic.
    /// </summary>
    [Fact]
    public void ThePeakCallbackLoad_IsPrintedAndLabelledAsAHighWaterMark()
    {
        // 4.0 ms against a 2.667 ms block: the peak DID overrun, by half a block.
        var text = AudioXrunAttribution.Describe(
            TheReportedWindow(), TheReportedSnapshot(maxCallbackNs: 4_000_000), 48000, 128);

        Assert.Contains("150%", text);
        Assert.Contains("high-water mark, not this window", text);
    }

    /// <summary>
    ///     …and the peak is read off the CURRENT snapshot, never differenced
    ///     across the window. Two high-water marks subtract to "how much higher the
    ///     record got", which is 0 for every window that did not beat it — so a
    ///     window whose peak was set earlier would report a 0% peak and quietly
    ///     refute a real overload. <see cref="AudioXrunAttribution.Window" />
    ///     deliberately carries no peak for the same reason.
    /// </summary>
    [Fact]
    public void ThePeakIsNotDifferencedAcrossTheWindow()
    {
        var previous = Snap(callbacks: 100_000, maxCallbackNs: 4_000_000);
        var current = Snap(callbacks: 111_250, late: 6, overruns: 16,
            lastCallbackNs: 293_333, budgetNs: Budget128At48K, maxCallbackNs: 4_000_000);

        var text = AudioXrunAttribution.Describe(
            AudioXrunAttribution.Delta(previous, current), current, 48000, 128);

        // The mark itself, not the (zero) difference between the two marks — and
        // not omitted, which is the other shape a differenced peak would take.
        Assert.Contains("longest callback since the counters were reset used 150%", text);
    }

    /// <summary>
    ///     Near misses — callbacks over 80% of budget but inside it — used to print
    ///     ONLY when the verdict was <see cref="DropoutCause.None" />, which is
    ///     backwards: they are the evidence FOR "the rig is close to the edge", and
    ///     were suppressed on exactly the two verdicts that make that claim.
    /// </summary>
    [Theory]
    [InlineData(600, 600, DropoutCause.OurRig)]
    [InlineData(600, 200, DropoutCause.Both)]
    public void NearMissesAreShownOnTheVerdictsThatBlameTheRig(long late, long overruns, DropoutCause expected)
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 110_000, late: late, overruns: overruns, nearMisses: 47));

        Assert.Equal(expected, w.Cause);
        Assert.Contains("47 came within 80% of their block",
            AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128));
    }

    /// <summary>
    ///     An overrun is measured as WALL CLOCK from callback entry to exit
    ///     (<c>recordCallbackTiming</c> in <c>AudioEngine.h</c>), so an OS
    ///     preemption inside our callback is counted as our overrun and delays the
    ///     next callback — producing the very one-for-one ratio that attributes the
    ///     dropout to us. The line may say our callback ran long; it may not
    ///     present that as proof the DSP is expensive.
    /// </summary>
    [Fact]
    public void TheOverrunWording_SaysWallClock_NotThatOurDspIsProvenExpensive()
    {
        var heavy = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 110_000, late: 600, overruns: 600));

        var text = AudioXrunAttribution.Describe(heavy, TheReportedSnapshot(), 48000, 128);
        Assert.Contains("wall clock", text);
        Assert.Contains("a stall INSIDE ours counts as ours", text);
    }

    /// <summary>
    ///     The machine verdict fires for two different windows — no overruns at
    ///     all, and a few overruns that cannot account for the lates — and the
    ///     message used to assert "our callbacks never overran their own budget"
    ///     for both. It is a false statement of fact in the second case, in the one
    ///     line a user consults when deciding what to change.
    /// </summary>
    [Fact]
    public void AMachineVerdictWithSomeOverruns_DoesNotClaimWeNeverRanLong()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 110_000, late: 20, overruns: 1));

        Assert.Equal(DropoutCause.Machine, w.Cause);
        Assert.True(w.DeadlineOverruns > 0);

        var text = AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128);
        Assert.Contains("OUTSIDE this app", text);
        Assert.DoesNotContain("never ran long", text);
        Assert.Contains("1 of 10000 callback(s)", text);
    }

    /// <summary>The other machine window — genuinely zero overruns — keeps the
    /// stronger statement, because there it is true.</summary>
    [Fact]
    public void AMachineVerdictWithNoOverruns_SaysSoPlainly()
    {
        var w = AudioXrunAttribution.Delta(
            Snap(callbacks: 100_000),
            Snap(callbacks: 110_000, late: 20));

        var text = AudioXrunAttribution.Describe(w, TheReportedSnapshot(), 48000, 128);
        Assert.Contains("never ran long", text);
        Assert.Contains("none of 10000 callback(s)", text);
    }
}
