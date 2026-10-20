using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Four frame-path wirings in the two render drivers that cannot be driven
///     headlessly (both need a GPU-backed backend) and were each found missing
///     on 2026-09-02. Source lints with comments stripped, the shape
///     <c>FontAtlasProjectSwitchTests.TheProjectSwitchHook_IsWiredInBothRenderers</c>
///     uses for the same two files.
/// </summary>
public sealed class Preview3DDriverWiringTests
{
    private const string Renderer2D = "DotCharter.UI/Controls/ChartRenderer.cs";
    private const string ViewMath = "DotCharter.UI/Input/TimelineViewMath.cs";

    /// <summary>
    ///     The 3D driver never versioned its selection, so
    ///     <c>FrameSnapshot.Update</c> saw the -1 default and re-hashed all six
    ///     lookup sets on EVERY frame — O(selection) × 6 at the fps cap, on the
    ///     render thread, for as long as anything was selected. The 2D renderer
    ///     has versioned it since v0.30.
    /// </summary>
    [Fact]
    public void TheDriver_VersionsItsSelection_WhereItHandsTheSelectionOver()
    {
        var body = DriverCode();
        var hand = body.IndexOf("_updateContext.SelectedNotes = localEditor.SelectedNotes;", StringComparison.Ordinal);
        Assert.True(hand >= 0, "the driver no longer hands the selection over at the expected line — repoint");
        Assert.Contains("_updateContext.SelectionVersion = localEditor.Selection.SelectionVersion;",
            body[hand..(hand + 800)]);
    }

    /// <summary>
    ///     The wake flag is consumed BEFORE the frame is attempted. A declined
    ///     <c>BeginFrame</c> (composed back-pressure, a native acquire hiccup)
    ///     produced nothing, so it must re-arm exactly as the failed-snapshot
    ///     path does — or a paused editor's edit that landed on a decline waits
    ///     for the idle trickle (100 ms by default, 1 s at the floor).
    /// </summary>
    [Fact]
    public void ADeclinedFrame_RearmsTheRenderRequest()
    {
        var body = DriverCode();
        var decline = body.IndexOf("consecutiveDeclines++;", StringComparison.Ordinal);
        Assert.True(decline >= 0, "the decline branch has moved — repoint this lint");
        var sleep = body.IndexOf("Thread.Sleep(declineStallReported", decline, StringComparison.Ordinal);
        Assert.True(sleep > decline, "the decline back-off has moved — repoint this lint");
        Assert.Contains("Interlocked.Exchange(ref _renderRequests, 1);", body[decline..sleep]);
    }

    /// <summary>
    ///     ...and so does a frame that THREW. Same argument one branch over: the
    ///     wake flag is consumed before the attempt, a thrown frame produced
    ///     nothing, and the loop deliberately keeps going (a transient device
    ///     hiccup must not kill it). Missed until 2026-09-04 — only the decline
    ///     branch got the 2026-09-02 fix.
    /// </summary>
    [Fact]
    public void AFrameThatThrew_RearmsTheRenderRequestToo()
    {
        var body = DriverCode();
        var failed = body.IndexOf("consecutiveFailures++;", StringComparison.Ordinal);
        Assert.True(failed >= 0, "the frame-exception branch has moved - repoint this lint");
        var give = body.IndexOf("if (consecutiveFailures >= maxConsecutiveFailures)", failed,
            StringComparison.Ordinal);
        Assert.True(give > failed, "the give-up check has moved - repoint this lint");
        Assert.Contains("Interlocked.Exchange(ref _renderRequests, 1);", body[failed..give]);
    }

    /// <summary>
    ///     The visual lead is WALL ms; the clocks it is added to are SONG ms.
    ///     Both render paths scale it by the playback speed
    ///     (<c>VisualLeadTiming.LeadScale</c>), or 2× draws half a lead behind.
    /// </summary>
    [Fact]
    public void BothRenderPaths_ScaleTheLeadByThePlaybackSpeed()
    {
        Assert.Contains("VisualLeadTiming.EffectiveLeadMs * VisualLeadTiming.LeadScale(audioService.PlaybackSpeed)", DriverCode());
        Assert.DoesNotContain("localTimeMs += VisualLeadTiming.EffectiveLeadMs;", DriverCode());
        var math = Code(ViewMath);
        Assert.Contains("visualLeadMs * Controls.VisualLeadTiming.LeadScale(playbackSpeed)", math);
        Assert.DoesNotContain("return exact + visualLeadMs;", math);
    }

    /// <summary>
    ///     The 2D timeline pre-registers the whole chart's text through the same
    ///     latch the 3D driver uses, BEFORE the windowed per-frame scan and
    ///     before <c>BeginFrame</c> (which is where the atlas rebuilds) — and
    ///     forgets the latch on a project switch beside the seed reset.
    /// </summary>
    [Fact]
    public void The2DRenderer_PreRegistersTheWholeChart_AndResetsTheLatchWithTheAtlas()
    {
        var body = Code(Renderer2D);
        var pre = body.IndexOf("ChartTextPreRegistration.Register(atlas,", StringComparison.Ordinal);
        var windowed = body.IndexOf("var sig = ComputeRegisterSignature();", StringComparison.Ordinal);
        var begin = body.IndexOf("backend.BeginFrame(", StringComparison.Ordinal);
        Assert.True(pre >= 0, "the whole-chart pre-registration is gone from the 2D renderer");
        Assert.True(pre < windowed && windowed < begin,
            "pre-registration must run before the windowed scan, and both before BeginFrame");

        var reset = body.IndexOf("_backend?.FontAtlas?.ResetToSeedCharacters();", StringComparison.Ordinal);
        Assert.True(reset >= 0);
        Assert.Contains("_textRegistration.Reset();", body[reset..(reset + 600)]);
    }

    /// <summary>
    ///     The THIRD site, and the one that is not a frame failure at all: the
    ///     loop re-checks <c>_isRenderable</c> between the gate and the present
    ///     (a hide can land in the milliseconds the snapshot build takes) and
    ///     abandons the iteration. The flag was already consumed, so that
    ///     abandon ate the edit / selection / theme change that woke it.
    /// </summary>
    [Fact]
    public void AnAbandonedIteration_RearmsTheRenderRequestToo()
    {
        var body = DriverCode();
        var abandon = body.IndexOf("if (token.IsCancellationRequested || !_isRenderable)",
            StringComparison.Ordinal);
        Assert.True(abandon >= 0, "the renderable re-check has moved - repoint this lint");
        Assert.Contains("Interlocked.Exchange(ref _renderRequests, 1);", body[abandon..(abandon + 400)]);
    }

    /// <summary>
    ///     …and the other half: the parked loop's self-heal probe writes
    ///     visibility through <c>NotifyEffectiveVisibility</c>, which ARMS a
    ///     frame on the false-to-true flip. A bare <c>_isRenderable</c> write
    ///     healed the latch and then parked, so the stale frame the heal exists
    ///     to replace stayed on screen for a trickle interval.
    /// </summary>
    [Fact]
    public void TheSelfHealProbe_WakesTheLoop_ItDoesNotJustUnlatchIt()
    {
        var body = DriverCode();
        // The USE, not the field declaration - the declaration comes first in
        // the file and a window from it contains none of the probe.
        var probe = body.IndexOf("Interlocked.Exchange(ref _visibilityProbePending, 1)",
            StringComparison.Ordinal);
        Assert.True(probe >= 0, "the visibility self-heal probe is gone - repoint this lint");
        // Wide enough to clear the stripped comment block between the two
        // (comment stripping leaves the blank indented lines behind).
        var window = body[probe..Math.Min(body.Length, probe + 1200)];
        Assert.Contains("NotifyEffectiveVisibility(_visibilityProbe())", window);
        Assert.DoesNotContain("_isRenderable = _visibilityProbe()", window);

        // …and that method is what arms the frame, so the claim above is real.
        var notify = body.IndexOf("public void NotifyEffectiveVisibility(", StringComparison.Ordinal);
        Assert.True(notify > 0);
        Assert.Contains("if (visible) RequestRender();", body[notify..(notify + 300)]);
    }

    /// <summary>
    ///     One frame describes ONE arrangement. <c>RunSnapshotUpdate</c> runs on
    ///     the render thread against a view model the UI thread mutates, and it
    ///     read <c>ActiveArrangement</c> three separate times (tones,
    ///     <c>Arrangement</c>, <c>ChordTemplates</c>) — so an arrangement switch
    ///     landing between them drew the old part's notes against the new part's
    ///     tuning, string count and chord-template table. The editor feed now
    ///     captures its part into locals first.
    /// </summary>
    [Fact]
    public void TheSnapshot_ReadsTheActiveArrangementExactlyOnce()
    {
        var body = DriverCode();
        var start = body.IndexOf("private void RunSnapshotUpdate()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunSnapshotUpdate has moved - repoint this lint");
        var method = body[start..];

        var reads = Regex.Matches(method, @"localEditor\s*\.\s*ActiveArrangement").Count;
        Assert.True(reads == 1,
            $"RunSnapshotUpdate reads localEditor.ActiveArrangement {reads} times; capture it once " +
            "or a mid-frame arrangement switch mixes two parts into one snapshot.");
        Assert.Contains("var arrangement = localEditor.ActiveArrangement;", method);
        Assert.Contains("_updateContext.Arrangement = arrangement;", method);
    }

    /// <summary>
    ///     #656: the Live Play feedback is drained by <c>LivePlayFeedbackDrain</c>, whose
    ///     <c>IsGrade</c> guard <c>LivePlayFeedbackDrainTests</c> pins. That pin is only
    ///     worth anything while the driver drains THROUGH it: once per snapshot update,
    ///     after the frame's <c>LivePlayActive</c> is stamped, and never by dequeuing the
    ///     queue itself.
    /// </summary>
    [Fact]
    public void TheSnapshot_DrainsLivePlayFeedbackThroughTheDrain_Once()
    {
        var driver = Code("DotCharter.UI/Controls/Preview3DRenderDriver.cs");
        var start = driver.IndexOf("private void RunSnapshotUpdate()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunSnapshotUpdate has moved - repoint this lint");
        var method = driver[start..];

        var drains = Regex.Matches(method, @"_livePlayFeedback\s*\.\s*Drain\(").Count;
        Assert.True(drains == 1, $"RunSnapshotUpdate drains the Live Play feedback {drains} times; expected once.");
        var stamp = method.IndexOf("_stagingSnapshot.LivePlayActive = livePlayActive;", StringComparison.Ordinal);
        Assert.True(stamp >= 0 && stamp < method.IndexOf("_livePlayFeedback.Drain(", StringComparison.Ordinal),
            "the drain must run after the frame's LivePlayActive is stamped");
        Assert.Contains("_livePlayFeedback.Drain(localLivePlay?.FeedbackEvents, livePlayActive,", method);
        Assert.DoesNotContain("FeedbackEvents.TryDequeue", driver);
    }

    /// <summary>
    ///     #446: the gate relaxes the idle trickle to 1 Hz only if the loop hands
    ///     it the time since the last WOKEN frame. Pass nothing (the parameter
    ///     defaults to 0) or restart that clock on every render, trickle frames
    ///     included, and the paused editor presents at 10 Hz again — a new
    ///     pane-sized compositor texture ~9 times a second, which
    ///     <c>Preview3DFrameGateTests.AParkedMinute_PresentsAboutOncePerSecond_NotTenTimes</c>
    ///     cannot see from the gate alone.
    /// </summary>
    [Fact]
    public void TheLoop_FeedsTheGateItsSettleClock_AndOnlyAWakeRestartsIt()
    {
        var body = DriverCode();
        var decide = body.IndexOf("Preview3DFrameGate.Decide(", StringComparison.Ordinal);
        Assert.True(decide >= 0, "the loop no longer asks Preview3DFrameGate.Decide - repoint this lint");
        var call = body[decide..body.IndexOf(");", decide, StringComparison.Ordinal)];
        Assert.StartsWith("Preview3DFrameGate.Decide(woke,", call);
        Assert.Contains("_sinceLastWake.Elapsed.TotalMilliseconds", call);
        Assert.Contains("var woke = Preview3DFrameGate.IsWake(", body[..decide]);

        var restarts = Regex.Matches(body, @"_sinceLastWake\.Restart\(\)").Count;
        Assert.True(restarts == 1, $"_sinceLastWake is restarted {restarts} times; only a woken frame may restart it.");
        Assert.Contains("if (woke) _sinceLastWake.Restart();", body);

        // ...and the corner HUD says the rate the loop RUNS, not the configured
        // one: "idle 10 Hz" over a loop that relaxed to 1 Hz is a false reading.
        var format = body.IndexOf("Preview3DStatsLine.Format(", StringComparison.Ordinal);
        Assert.True(format >= 0, "the HUD line is no longer formatted here - repoint this lint");
        var args = body[format..body.IndexOf(");", format, StringComparison.Ordinal)];
        Assert.Contains("Preview3DFrameGate.TrickleHz(Preview3DPacing.IdleFps, _sinceLastWake.Elapsed.TotalMilliseconds)", args);
    }

    /// <summary>The driver and its collaborators (<see cref="Preview3DRenderDriverSource" />),
    /// the driver first, comments stripped.</summary>
    private static string DriverCode() => StripComments(Preview3DRenderDriverSource.ReadAllText());

    private static string Code(string relativePath) =>
        StripComments(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }
}
