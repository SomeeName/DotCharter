using Avalonia;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

/// <summary>
///     The three event-ribbon gestures set <c>HasDragged = true</c> on the FIRST
///     pointer move of the press, and <c>EventInteractionTool.OnPointerReleased</c>
///     commits whenever that flag is set — so one pixel of tremor while CLICKING
///     committed a real, undoable edit.
///     <para>
///         None of the three edits is small: a section/phrase marker is re-SNAPPED
///         (<c>DragState.GetDelta</c> snaps the reference to the grid, so the delta
///         is non-zero the moment the marker sits off-grid, which every RS-XML /
///         psarc import produces), a lyric is snapped to the grid or a waveform
///         onset, and a beat is retimed with the segment's BPM recalculated behind
///         it. The highway escapes only because <c>SoundMagnetSnap</c> pulls its
///         snap back onto the very sound under the pointer; nothing magnets a
///         marker, a lyric or a beat.
///     </para>
/// </summary>
public sealed class GestureDragThresholdTests
{
    private static GestureDragThreshold Armed(double x = 100, double y = 20)
    {
        var t = new GestureDragThreshold();
        t.Arm(new Point(x, y));
        return t;
    }

    [Fact]
    public void AFreshlyArmedThreshold_HasNotDragged() => Assert.False(Armed().HasDragged);

    /// <summary>The bug: a pixel of tremor is a CLICK.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 2)]
    [InlineData(-2, 2)]
    public void MovementInsideTheThreshold_IsStillAClick(double dx, double dy)
    {
        var t = Armed();
        Assert.False(t.Observe(new Point(100 + dx, 20 + dy)));
        Assert.False(t.HasDragged);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, -3)]
    [InlineData(40, 0)]
    public void MovementPastTheThreshold_IsADrag(double dx, double dy)
    {
        var t = Armed();
        Assert.True(t.Observe(new Point(100 + dx, 20 + dy)));
        Assert.True(t.HasDragged);
    }

    /// <summary>
    ///     LATCHING. A drag that wanders back inside the threshold is still a drag
    ///     — the user has already watched the preview move, and cancelling on the
    ///     release would throw away an edit they saw happen.
    /// </summary>
    [Fact]
    public void OnceDragged_ComingBackInsideTheThresholdIsStillADrag()
    {
        var t = Armed();
        Assert.True(t.Observe(new Point(160, 20)));
        Assert.True(t.Observe(new Point(101, 20)));
        Assert.True(t.HasDragged);
    }

    [Fact]
    public void ReArming_StartsANewClick()
    {
        var t = Armed();
        t.Observe(new Point(160, 20));
        t.Arm(new Point(300, 40));
        Assert.False(t.HasDragged);
        Assert.False(t.Observe(new Point(301, 40)));
    }

    [Fact]
    public void Reset_DropsTheLatch()
    {
        var t = Armed();
        t.Observe(new Point(160, 20));
        t.Reset();
        Assert.False(t.HasDragged);
    }

    /// <summary>ONE number decides a click from a drag everywhere — the marquee's.</summary>
    [Fact]
    public void TheThreshold_IsTheMarqueesOwn()
    {
        var t = Armed();
        Assert.False(t.Observe(new Point(100 + MarqueeHitTester.DragThresholdPx - 0.001, 20)));
        Assert.True(t.Observe(new Point(100 + MarqueeHitTester.DragThresholdPx, 20)));
    }

    /// <summary>
    ///     The mechanical half: each of the three gestures must gate its move on
    ///     the shared latch and must not carry a private <c>HasDragged = true</c>
    ///     again. This is a source lint because the gestures take a
    ///     <c>ToolContext</c>, which takes a <c>MainWindowViewModel</c> — the
    ///     <c>HighwayDragCloneDeferralTests</c> precedent.
    /// </summary>
    [Theory]
    [InlineData("EventPointDragGesture.cs")]
    [InlineData("LyricDragGesture.cs")]
    [InlineData("BeatDragGesture.cs")]
    public void EveryEventGesture_GatesItsMoveOnTheThreshold(string file)
    {
        var source = Read($"DotCharter.UI/Input/Tools/EventInteractionGestures/{file}")
            .Replace("\r\n", "\n");
        var moved = MethodBody(source, "public void OnMoved(ToolContext context)");

        Assert.Contains("if (!_threshold.Observe(context.Position)) return;", moved);
        Assert.DoesNotContain("HasDragged = true", source);
        Assert.Contains("public bool HasDragged => _threshold.HasDragged;", source);
        Assert.Contains("_threshold.Arm(pressOrigin);", source);
    }

    /// <summary>…and the tool has to hand the gesture the PRESS position, or the
    /// latch measures movement from (0, 0) and every move is a drag again.</summary>
    [Fact]
    public void TheEventTool_HandsEveryGestureThePressPosition()
    {
        var source = Read("DotCharter.UI/Input/Tools/EventInteractionTool.cs");
        foreach (var gesture in new[] { "_beatDrag.Start(", "_eventPointDrag.Start(", "_lyricDrag.Start(" })
        {
            var start = source.IndexOf(gesture, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{gesture} has moved — repoint this lint.");
            var call = source[start..source.IndexOf(';', start)];
            Assert.Contains("context.Position", call);
        }
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
