using System.Globalization;
using JuceSharp;

namespace DotCharter.Tone.Audio.Diagnostics;

/// <summary>Who caused the dropouts in a window.</summary>
public enum DropoutCause
{
    /// <summary>Nothing audible happened.</summary>
    None,

    /// <summary>
    ///     Our own audio callback did not finish inside the block it was
    ///     rendering, often enough to account for the audible dropouts.
    ///
    ///     <para>
    ///         This says the callback took too long — it does NOT prove the DSP
    ///         inside it is expensive. The engine measures WALL CLOCK from
    ///         callback entry to exit (<c>recordCallbackTiming</c> in
    ///         <c>AudioEngine.h</c>), so an OS preemption in the middle of our
    ///         callback is counted as our overrun AND delays the next callback's
    ///         start — landing at exactly the one-for-one ratio this value is
    ///         derived from. "Remove gear" only follows when the overruns are also
    ///         FREQUENT; see <see cref="AudioXrunAttribution.SustainedOverrunRate" />.
    ///     </para>
    /// </summary>
    OurRig,

    /// <summary>The callback was late and our own overruns cannot account for it:
    /// something OUTSIDE the process stalled the audio thread. The user's fix is
    /// elsewhere — power plan, another app, a driver.</summary>
    Machine,

    /// <summary>Both: our callbacks are running long AND stalls are arriving from
    /// outside.</summary>
    Both
}

/// <summary>
///     Turns two <see cref="XrunStats" /> snapshots into "what happened, and whose
///     fault was it" — the question the audio-health log was always meant to
///     answer and could not.
///
///     <para>
///         The managed <see cref="AudioDropoutDetector" /> can only see that the
///         render clock went stale, which is the symptom. The engine's counters
///         separate the two causes, because the callback knows whether it was late
///         to start (someone stalled us) or slow to finish (we ran long) — and
///         those have opposite fixes.
///     </para>
///     <para>
///         <b>Three timebases meet in <see cref="Describe" /> and the reader must
///         be able to tell them apart.</b> <see cref="Window" /> holds DELTAS over
///         the poll window (30 s — <c>HealthHeartbeatMs</c> in
///         <c>AudioHealthMonitor</c>).
///         <see cref="XrunStats.Load" /> is ONE callback sampled at poll time,
///         which may be 30 s after every event the window describes.
///         <see cref="XrunStats.MaxCallbackNs" /> is a HIGH-WATER mark since the
///         counters were last reset, so it is older still. They are never in
///         disagreement — they describe different callbacks — so the line must not
///         print them as one measurement, which is how "16 of our callbacks
///         overran, remove your DSP/VST gear" would come to sit in the same sentence as
///         "the last callback used 11% of its block budget".
///     </para>
///     <para>
///         Pure and delta-based so it is unit-tested with constructed snapshots
///         and no clock, no device and no engine.
///     </para>
/// </summary>
public static class AudioXrunAttribution
{
    /// <summary>The share of late callbacks that must be explained by our own
    /// overruns before the rig is blamed at all. Below this the overruns are
    /// incidental and the stalls came from outside.</summary>
    public const double OurRigShare = 0.25;

    /// <summary>
    ///     The share of a window's callbacks that must overrun before the log
    ///     tells the user to raise the buffer or remove gear: 1 block in 100.
    ///
    ///     <para>
    ///         The floor comes from what "the rig is too heavy" physically means.
    ///         Removing gear lowers the STEADY per-block cost, and a steady cost
    ///         above the block budget overruns essentially EVERY block, while a
    ///         steady cost just under it overruns whatever fraction of blocks the
    ///         run-to-run jitter pushes over the line. Neither shape can express
    ///         itself as one overrun in several hundred: at that rate the other
    ///         several hundred blocks finished on time with the same gear loaded,
    ///         so the gear is not what made those few run long.
    ///     </para>
    ///     <para>
    ///         1 % is also roughly where a listener would call the result
    ///         recurring rather than a tick: at 48 kHz / 128 frames a block is
    ///         2.67 ms, so 1 % is an overrun every ~0.27 s; at 48 kHz / 1024 frames
    ///         (21.3 ms) it is one every ~2.1 s.
    ///     </para>
    ///     <para>
    ///         The report that prompted the floor: 16 overruns across ~11,250
    ///         callbacks in one 30 s window — 0.14 %, seven times below this, with
    ///         the sampled callback sitting at 11 % of its budget — and the line
    ///         told the owner to remove DSP/VST gear during plain song playback
    ///         with no live input and nothing but playback running.
    ///     </para>
    /// </summary>
    public const double SustainedOverrunRate = 0.01;

    /// <summary>
    ///     What changed between two snapshots.
    ///
    ///     <para>
    ///         Everything here is a DELTA over the window. <see cref="XrunStats.MaxCallbackNs" />
    ///         deliberately has no member here: it is a high-water mark, and the
    ///         difference between two high-water marks is not "the peak in this
    ///         window" — it is "how much higher the all-time peak got", which is 0
    ///         for every window that failed to beat the record and is not a peak
    ///         when it does. <see cref="Describe" /> reads the peak straight off
    ///         the current snapshot and LABELS it as since-reset.
    ///     </para>
    /// </summary>
    public readonly record struct Window
    {
        /// <summary>Audible dropouts in the window.</summary>
        public long LateCallbacks { get; init; }

        /// <summary>Callbacks that did not finish inside their own block, in the
        /// window. Measured as wall clock across the callback, so a stall INSIDE
        /// our callback is counted here as ours.</summary>
        public long DeadlineOverruns { get; init; }

        /// <summary>Callbacks over 80% of budget but inside it.</summary>
        public long NearMisses { get; init; }

        /// <summary>Callbacks observed in the window.</summary>
        public long Callbacks { get; init; }

        /// <summary>Driver-reported dropouts in the window, or <c>null</c> when
        /// this driver cannot report them at all.</summary>
        public int? DeviceXruns { get; init; }

        /// <summary>
        ///     True when the engine's counters were REBASED under us — a device
        ///     (re)start, a sample-rate change or a watchdog recovery zeroes them.
        ///
        ///     <para>
        ///         The window is still reported, but its counts are "since the
        ///         rebase", not since the previous poll. Without this flag a device
        ///         change reads as a sudden drop to zero, which is indistinguishable
        ///         from a window that genuinely went quiet.
        ///     </para>
        /// </summary>
        public bool CountersRebased { get; init; }

        /// <summary>Whether anything audible happened.</summary>
        public bool HasDropouts => LateCallbacks > 0 || DeviceXruns > 0;

        /// <summary>
        ///     The share of this window's callbacks that overran, or <c>null</c>
        ///     when there is no callback count to divide by and the rate is
        ///     therefore not computable.
        ///
        ///     <para>
        ///         Null, never 0: "none of 11,250 blocks overran" and "we do not
        ///         know how many blocks ran" are opposite claims, and the second
        ///         must be able to neither convict nor acquit the rig.
        ///     </para>
        /// </summary>
        public double? OverrunRate => Callbacks > 0 ? (double)DeadlineOverruns / Callbacks : null;

        /// <summary>
        ///     Whether the overruns are frequent enough to be the STEADY cost of
        ///     the rig rather than a handful of stalls — the gate on the loud
        ///     "raise the buffer size or remove gear" advice. See
        ///     <see cref="SustainedOverrunRate" />.
        ///
        ///     <para>
        ///         An unknown rate is not sustained. It does not reclassify
        ///         <see cref="Cause" /> either: the attribution still says which
        ///         callbacks were responsible; this only says whether that is
        ///         enough to send the user shopping for a smaller rig.
        ///     </para>
        /// </summary>
        public bool OverrunsAreSustained => OverrunRate is { } rate && rate >= SustainedOverrunRate;

        /// <summary>
        ///     Who to blame. See <see cref="DropoutCause" />.
        ///
        ///     <para>
        ///         This answers WHICH callbacks were responsible, not HOW BAD it
        ///         was — one overrun that stalls the one block after it is
        ///         <see cref="DropoutCause.OurRig" /> by this test, which is
        ///         correct attribution and terrible advice. The severity question
        ///         is <see cref="OverrunsAreSustained" />, and only that gates the
        ///         advice.
        ///     </para>
        /// </summary>
        public DropoutCause Cause
        {
            get
            {
                if (!HasDropouts) return DropoutCause.None;
                if (DeadlineOverruns <= 0) return DropoutCause.Machine;
                if (LateCallbacks <= 0) return DropoutCause.OurRig;

                // An overrun usually makes the NEXT block's callback start late, so
                // when the overruns track the lates one-for-one, ours is the best
                // explanation available — not a proof, because a preemption inside
                // our callback produces exactly the same pair. When the lates far
                // outrun them, most stalls came from outside and only some were
                // ours.
                var share = (double)DeadlineOverruns / LateCallbacks;
                if (share >= 1.0) return DropoutCause.OurRig;
                return share >= OurRigShare ? DropoutCause.Both : DropoutCause.Machine;
            }
        }
    }

    /// <summary>
    ///     The delta from <paramref name="previous" /> to <paramref name="current" />.
    ///
    ///     <para>
    ///         A counter going DOWN is not corruption — the engine rebases every
    ///         counter when the device (re)starts, and it deliberately ignores the
    ///         first four callbacks of a session so a device change does not report
    ///         a phantom dropout. That case is reported as the current values with
    ///         <see cref="Window.CountersRebased" /> set, never as a negative delta.
    ///     </para>
    /// </summary>
    public static Window Delta(XrunStats previous, XrunStats current)
    {
        var rebased = current.Callbacks < previous.Callbacks
                      || current.LateCallbacks < previous.LateCallbacks
                      || current.DeadlineOverruns < previous.DeadlineOverruns;

        if (rebased)
        {
            return new Window
            {
                Callbacks = current.Callbacks,
                LateCallbacks = current.LateCallbacks,
                DeadlineOverruns = current.DeadlineOverruns,
                NearMisses = current.NearMisses,
                DeviceXruns = current.DeviceXruns,
                CountersRebased = true
            };
        }

        return new Window
        {
            Callbacks = current.Callbacks - previous.Callbacks,
            LateCallbacks = current.LateCallbacks - previous.LateCallbacks,
            DeadlineOverruns = current.DeadlineOverruns - previous.DeadlineOverruns,
            NearMisses = current.NearMisses - previous.NearMisses,
            // Absent stays absent. A driver that cannot report must never
            // contribute a 0 that reads as "and the driver saw none".
            DeviceXruns = current.DeviceXruns is { } now && previous.DeviceXruns is { } before
                ? Math.Max(0, now - before)
                : current.DeviceXruns
        };
    }

    /// <summary>
    ///     The line to put in the log — what the window counted, what that does
    ///     and does not justify, and the two out-of-window numbers said to be out
    ///     of window.
    ///
    ///     <para>
    ///         One line, three labelled sections in the order a reader needs them:
    ///         <c>This window:</c> the deltas; then the verdict; then
    ///         <c>Sampled now:</c> the instantaneous load and the since-reset peak.
    ///         That peak (<see cref="XrunStats.MaxCallbackNs" />) is the only
    ///         number in the snapshot that can support or refute "too heavy", and
    ///         it went unprinted for the whole life of this type while the line
    ///         made the claim anyway.
    ///     </para>
    /// </summary>
    public static string Describe(Window window, XrunStats current, int sampleRate, int blockFrames)
    {
        var where = $"{sampleRate} Hz / {blockFrames} frames";

        // --- what the WINDOW counted (deltas, every one of them) --------------
        var counted = window.LateCallbacks > 0
            ? $"{window.LateCallbacks} audible dropout(s) at {where}"
            : window.DeviceXruns > 0
                ? $"no late callbacks of ours at {where}"
                : $"no audible dropouts at {where}";

        var over = window.DeadlineOverruns > 0
            ? $"{window.DeadlineOverruns} of {CallbackCount(window)} did not finish inside their own block"
              + RateSuffix(window)
            : $"none of {CallbackCount(window)} failed to finish inside its own block";

        // Near misses belong on EVERY verdict, not only on the quiet
        // one. Callbacks over 80% of budget are the evidence FOR "the rig is close
        // to the edge" — suppressing them on the verdicts that claim it left the
        // claim standing on nothing at all.
        var near = window.NearMisses > 0
            ? $", and {window.NearMisses} came within 80% of their block without overrunning"
            : "";

        // An overrun is wall clock across the callback, so it means "our callback
        // ran long", never "our DSP is expensive". Say so wherever we blame it.
        const string wallClock = " (an overrun is wall clock across the callback, so a stall INSIDE ours counts as ours)";

        var verdict = window.Cause switch
        {
            DropoutCause.OurRig or DropoutCause.Both when window.OverrunsAreSustained =>
                $"Our callbacks are missing their deadline on {Percent(window.OverrunRate!.Value)} of blocks — "
                + "sustained, so OUR audio processing is too heavy for this buffer size. Raise the buffer size or "
                + "remove DSP/VST gear"
                + (window.Cause == DropoutCause.Both ? "; the stalls it does not explain came from outside the app" : "")
                + wallClock,

            // No callback count ⇒ no rate ⇒ nothing to say about severity. It must
            // not fall through to either the loud arm or the reassuring one: an
            // un-computable rate can neither convict nor acquit the rig.
            DropoutCause.OurRig or DropoutCause.Both when window.OverrunRate is null =>
                "How often that happened is unknown without a callback count, so this says nothing yet about "
                + "whether the rig is too heavy"
                + wallClock,

            DropoutCause.OurRig or DropoutCause.Both =>
                $"That is {OneIn(window)}, far too rare to be the steady cost of the rig — gear heavy enough to "
                + "matter overruns most blocks, not a few. Occasional stalling, NOT gear to remove"
                + wallClock,

            DropoutCause.Machine when window.DeadlineOverruns > 0 =>
                "Our own overruns are too few to account for those, so something OUTSIDE this app stalled the audio "
                + "thread. Look at power plan, other processes, or the audio driver",

            DropoutCause.Machine =>
                "Our callbacks never ran long, so something OUTSIDE this app stalled the audio thread. "
                + "Look at power plan, other processes, or the audio driver",

            _ => ""
        };

        // --- the two numbers that are NOT from this window --------------------
        var sampled = new List<string>();
        if (current.Load is { } load)
            sampled.Add($"last callback used {Percent(load)} of its block budget");
        if (current.BlockBudgetNs > 0 && current.MaxCallbackNs > 0)
        {
            sampled.Add("longest callback since the counters were reset used "
                        + $"{Percent((double)current.MaxCallbackNs / current.BlockBudgetNs)} "
                        + "(a high-water mark, not this window)");
        }

        var driver = window.DeviceXruns is { } dx
            ? $"the driver reported {dx} of its own"
            : "the driver cannot report its own xruns";

        var tail = sampled.Count > 0
            ? $"Sampled now: {string.Join(", ", sampled)}, {driver}."
            : $"{char.ToUpperInvariant(driver[0])}{driver[1..]}.";

        var rebase = window.CountersRebased
            ? " [counters were rebased by a device restart during this window]"
            : "";

        var parts = new List<string> { $"This window: {counted}; {over}{near}." };
        if (verdict.Length > 0) parts.Add(verdict + ".");
        parts.Add(tail);

        return $"Audio health: {string.Join(" ", parts)}{rebase}";
    }

    /// <summary>The window's callback count in words, so an absent count reads as
    /// absent instead of as "no blocks ran".</summary>
    private static string CallbackCount(Window window) =>
        window.Callbacks > 0 ? $"{window.Callbacks} callback(s)" : "our callbacks (count unavailable)";

    /// <summary>The overrun rate as a parenthesised suffix, or an explicit "not
    /// computable" — never a silent omission that reads as a low rate.</summary>
    private static string RateSuffix(Window window) =>
        window.OverrunRate is { } rate ? $" ({Percent(rate)} of blocks)" : " (no callback count, so no rate)";

    /// <summary>"one block in 703" — the rate in the form that makes rarity
    /// obvious. A percentage that rounds to "0%" hides the very fact this clause
    /// exists to state.</summary>
    private static string OneIn(Window window) =>
        window.OverrunRate is { } rate && rate > 0
            ? $"about one block in {Math.Round(1.0 / rate).ToString("F0", CultureInfo.InvariantCulture)}"
            : "a rate we cannot compute";

    /// <summary>
    ///     A share as a percentage, invariant, with enough digits to stay a
    ///     number: 0.14 % of blocks rounded to "0%" is the same lie the old line
    ///     told by never printing the rate at all.
    /// </summary>
    private static string Percent(double share)
    {
        var pct = share * 100.0;
        var digits = pct >= 10.0 ? 0 : pct >= 1.0 ? 1 : 2;
        return pct.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";
    }
}
