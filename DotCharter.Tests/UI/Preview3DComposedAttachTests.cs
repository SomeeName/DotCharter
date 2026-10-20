using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The composed 3D host's attach/detach seam. It cannot be driven headlessly
///     — <c>Avalonia.Headless</c> has no compositor GPU interop, so the setup
///     returns before it ever builds a pump — so this is a source lint, the same
///     shape <c>Preview3DDriverWiringTests</c> uses for the frame path.
///     <para>
///         <b>What it pins (2026-09-04).</b> <c>InitializeCompositionAsync</c>
///         awaits <c>TryGetCompositionGpuInterop</c>, and a float/redock or a
///         pop-out round trip detaches and re-attaches inside that await. The
///         old guard was a plain <c>_attached</c> bool, which cannot tell "still
///         attached" from "detached and attached AGAIN": the first setup resumed,
///         found the SECOND attach's surface, built a second
///         <c>ComposedPresentPump</c> over it and overwrote the field. An
///         overwritten pump is never <c>Shutdown()</c>, so its imported GPU
///         images leak and two pumps drive one <c>CompositionDrawingSurface</c>.
///     </para>
/// </summary>
public sealed class Preview3DComposedAttachTests
{
    private const string Canvas = "DotCharter.UI/Controls/Preview3DComposedCanvas.cs";

    /// <summary>A bool cannot count attaches; the guard has to be a generation
    /// the setup carries, bumped on BOTH sides of the round trip.</summary>
    [Fact]
    public void BothAttachAndDetach_BumpTheAttachGeneration()
    {
        var body = Code(Canvas);

        var attach = Body(body, "protected override void OnAttachedToVisualTree");
        Assert.Contains("var generation = unchecked(++_attachGeneration);", attach);
        Assert.Contains("InitializeCompositionAsync(generation);", attach);

        var detach = Body(body, "protected override void OnDetachedFromVisualTree");
        Assert.Contains("_attachGeneration++;", detach);

        Assert.DoesNotContain("private bool _attached;", body);
    }

    /// <summary>
    ///     The check must sit between the await and the pump. Anything else —
    ///     checking before the await, or after the pump exists — leaves the
    ///     orphan behind.
    /// </summary>
    [Fact]
    public void TheResumedSetup_ChecksItsGeneration_BeforeItBuildsAPump()
    {
        var body = Code(Canvas);
        var start = body.IndexOf("private async void InitializeCompositionAsync(", StringComparison.Ordinal);
        Assert.True(start > 0,
            "InitializeCompositionAsync no longer takes the attach generation — repoint this lint.");

        var method = body[start..];
        var await_ = method.IndexOf("await compositor.TryGetCompositionGpuInterop()", StringComparison.Ordinal);
        var guard = method.IndexOf("generation != _attachGeneration", StringComparison.Ordinal);
        var pump = method.IndexOf("new ComposedPresentPump(", StringComparison.Ordinal);

        Assert.True(await_ > 0, "the interop await has moved — repoint this lint.");
        Assert.True(pump > 0, "the pump is no longer built here — repoint this lint.");
        Assert.True(guard > await_,
            "the generation check must come AFTER the await — before it, it can only ever pass.");
        Assert.True(guard < pump,
            "a stale continuation must bail out BEFORE it builds a second pump over the live surface.");
    }

    /// <summary>Exactly one pump field write per setup, and the detach shuts the
    /// pump down — the two halves that make the orphan impossible rather than
    /// merely unlikely.</summary>
    [Fact]
    public void TheDetach_ShutsThePumpDown_AndReleasesTheSurface()
    {
        var detach = Body(Code(Canvas), "protected override void OnDetachedFromVisualTree");
        Assert.Contains("_pump?.Shutdown();", detach);
        Assert.Contains("_pump = null;", detach);
        Assert.Contains("_surface?.Dispose();", detach);
        Assert.Contains("_surface = null;", detach);
        Assert.Contains("ElementComposition.SetElementChildVisual(this, null);", detach);
    }

    /// <summary>
    ///     Render size is PIXELS on this path, and a DPI change moves the
    ///     SCALING while leaving the DIP <c>Bounds</c> alone — the window keeps
    ///     its DIP size and Windows resizes the HWND underneath. <c>Bounds</c>
    ///     was the only input the canvas listened to, so a pop-out dragged to a
    ///     150 % monitor (or a display-scale change mid-session) kept rendering
    ///     at the old pixel size and let the compositor rescale it. Headless
    ///     pins <c>RenderScaling</c> at 1 and offers no way to move it, so this
    ///     is a lint: subscribe on attach, unsubscribe on detach, recompute.
    /// </summary>
    [Fact]
    public void TheCanvas_FollowsATopLevelScalingChange_AndUnhooksOnDetach()
    {
        var body = Code(Canvas);

        var attach = Body(body, "protected override void OnAttachedToVisualTree");
        // Either `if (_scalingSource != null) _scalingSource.X += …` or `_scalingSource?.X += …`.
        Assert.Matches(@"_scalingSource\??\.ScalingChanged \+= OnTopLevelScalingChanged;", attach);

        var detach = Body(body, "protected override void OnDetachedFromVisualTree");
        Assert.Matches(@"_scalingSource\??\.ScalingChanged -= OnTopLevelScalingChanged;", detach);
        Assert.Contains("_scalingSource = null;", detach);

        Assert.Contains("OnTopLevelScalingChanged(object? sender, EventArgs e) => UpdateSizes();", body);
        // …and the recompute has to actually READ the scaling, or following the
        // event buys nothing. Since the interface-scale wave (2026-09-12) the
        // read is VisualPixelScale.For(this) — the window's RenderScaling folded
        // with every layout transform above the canvas, which is what sizes the
        // swapchain correctly under the global UI scale — so the lint follows
        // the helper and, one hop further, pins that the helper still reads
        // RenderScaling itself.
        Assert.Contains("VisualPixelScale.For(this)", Body(body, "private void UpdateSizes()"));
        Assert.Contains("RenderScaling", Body(Code("DotCharter.UI/Controls/VisualPixelScale.cs"), "public static double For(Visual? visual)"));
    }

    /// <summary>The method text from its signature to the next member at class
    /// indentation — enough to order statements inside it.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"'{signature}' not found in {Canvas} — repoint this lint.");
        var end = source.IndexOf("\n    protected", start + signature.Length, StringComparison.Ordinal);
        var end2 = source.IndexOf("\n    private", start + signature.Length, StringComparison.Ordinal);
        if (end < 0 || (end2 >= 0 && end2 < end)) end = end2;
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string Code(string relativePath)
    {
        var source = UiSourceTree.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }
}
