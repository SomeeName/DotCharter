namespace DotCharter.Tests.UI.Input;

/// <summary>
///     P2-3 / P2-5 perf riders, pinned as source lints because both types take
///     a <c>MainWindowViewModel</c> (the <c>PointerGestureBoundaryTests</c>
///     precedent).
///     <para>
///         P2-3: <c>HighwayInteractionTool</c> cloned the whole selection ×4 on
///         every PRESS — work a plain click never used — and, being one
///         instance per coordinator, retained the last drag's clones forever
///         because <c>ResetState</c> never dropped them. The clones are
///         captured on the first actual drag movement now and cleared on reset.
///     </para>
///     <para>
///         P2-5: <c>ToolCoordinator</c> throttled pointer moves with
///         <c>DateTime.UtcNow</c> at 16 ms — the wall clock advances in
///         ~15.6 ms jumps, so the comparison admitted 0 or 2 steps per frame.
///         <c>Stopwatch.GetTimestamp()</c> is the monotonic high-resolution
///         source.
///     </para>
/// </summary>
public sealed class HighwayDragCloneDeferralTests
{
    [Fact]
    public void ThePressPath_ClonesNothing()
    {
        var source = Read("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs");
        var body = MethodBody(source, "private void HandleLeftClick");
        Assert.DoesNotContain(".Clone()", body);
    }

    [Fact]
    public void TheClones_AreCapturedOnTheFirstDragMovement()
    {
        var source = Read("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs");
        var moved = MethodBody(source, "public void OnPointerMoved");
        Assert.Contains("CaptureDragOriginals", moved);

        var capture = MethodBody(source, "private void CaptureDragOriginals");
        foreach (var list in new[]
                 {
                     "_dragOriginalStates", "_dragOriginalChordStates",
                     "_dragOriginalHandShapes", "_dragOriginalAnchors"
                 })
        {
            Assert.Contains(list, capture);
        }
    }

    [Fact]
    public void ResetState_DropsAllFourCloneLists()
    {
        var source = Read("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs");
        var reset = MethodBody(source, "private void ResetState");
        foreach (var list in new[]
                 {
                     "_dragOriginalStates = []", "_dragOriginalChordStates = []",
                     "_dragOriginalHandShapes = []", "_dragOriginalAnchors = []"
                 })
        {
            Assert.Contains(list, reset);
        }
    }

    [Fact]
    public void ThePointerMoveThrottle_UsesStopwatchTimestamps()
    {
        var source = Read("DotCharter.UI/Input/Tools/ToolCoordinator.cs");
        Assert.Contains("Stopwatch.GetTimestamp()", source);
        Assert.DoesNotContain("DateTime.UtcNow.Ticks", source);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var nextPublic = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        if (nextPublic >= 0 && (next < 0 || nextPublic < next)) next = nextPublic;
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
