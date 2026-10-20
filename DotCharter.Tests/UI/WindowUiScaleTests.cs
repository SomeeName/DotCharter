using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The global UI scale is an ACCESSIBILITY setting, and it reached one
///     window out of three.</b>
///     <para>
///         Avalonia has no per-control layout transform, so the whole mechanism
///         is a <c>LayoutTransformControl</c> wrapper — and only
///         <c>MainWindow</c> had one. A user who set 150 % because they cannot
///         read 100 % got Session Health and About at 100 %. The same wrapper
///         carries the RTL <c>FlowDirection</c>, so an RTL community language
///         pack mirrored the shell and left those two the only surfaces facing
///         the other way.
///     </para>
///     <para>
///         The two window shapes need different answers, which is the part
///         worth pinning: a RESIZABLE window keeps its size and reflows scaled
///         content, while a window with a pinned <c>Width</c> and
///         <c>CanResize="False"</c> (About) would just CLIP it — its declared
///         width and minimum have to scale too.
///     </para>
/// </summary>
public sealed class WindowUiScaleTests
{
    [Fact]
    public void IdentityScale_IsANullTransform_NotAOneTimesScale()
    {
        Assert.Null(WindowUiScale.TransformFor(1.0));
        Assert.Null(WindowUiScale.TransformFor(0.9995));
        Assert.Null(WindowUiScale.TransformFor(1.0005));
    }

    [Fact]
    public void ARealScale_IsAUniformScaleTransform()
    {
        var transform = Assert.IsType<ScaleTransform>(WindowUiScale.TransformFor(1.25));
        Assert.Equal(1.25, transform.ScaleX);
        Assert.Equal(1.25, transform.ScaleY);
    }

    [Fact]
    public void ADeclaredLength_ScalesWithTheContent()
    {
        Assert.Equal(800, WindowUiScale.ScaledLength(640, 1.25));
        Assert.Equal(640, WindowUiScale.ScaledLength(640, 1.0));
    }

    /// <summary>A <c>SizeToContent</c> dimension is <c>NaN</c> and must stay
    /// <c>NaN</c>: scaling it would pin a window that is meant to size itself,
    /// and <c>NaN * 1.25</c> is still NaN only by luck — the guard says so.</summary>
    [Fact]
    public void ASizeToContentDimension_IsLeftAlone()
    {
        Assert.True(double.IsNaN(WindowUiScale.ScaledLength(double.NaN, 1.5)));
        Assert.Equal(0, WindowUiScale.ScaledLength(0, 1.5));
        Assert.Equal(-1, WindowUiScale.ScaledLength(-1, 1.5));
    }

    /// <summary>
    ///     The wiring, because the helper is inert without it. Every top-level
    ///     <c>Window</c> under <c>Views/</c> must carry the wrapper and follow
    ///     the language's flow direction — the shell does it inline, the other
    ///     two through <see cref="WindowUiScale.Attach" />.
    /// </summary>
    [Fact]
    public void EveryWindow_CarriesTheScalerAndFollowsTheFlowDirection()
    {
        var windows = UiSourceTree
            .EnumerateFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "*.axaml",
                SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: UiSourceTree.ReadAllText(path)))
            .Where(f => Regex.IsMatch(f.Source, @"^﻿?<Window\b", RegexOptions.Multiline))
            .ToList();

        Assert.True(windows.Count >= 2,
            $"only {windows.Count} top-level Windows found under Views/ — this lint is reading the wrong thing.");

        var unscaled = windows
            .Where(f => !f.Source.Contains("x:Name=\"" + WindowUiScale.ScalerName + "\"", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();
        Assert.True(unscaled.Count == 0,
            "These windows carry no LayoutTransformControl named " + WindowUiScale.ScalerName +
            ", so the global UI scale — an accessibility setting — cannot reach them:\n  "
            + string.Join("\n  ", unscaled));

        var unmirrored = windows
            .Where(f => !f.Source.Contains("BoolConverters.ToFlowDirection", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();
        Assert.True(unmirrored.Count == 0,
            "These windows do not bind FlowDirection to the active language, so an RTL pack mirrors the " +
            "shell and leaves them facing the other way:\n  " + string.Join("\n  ", unmirrored));
    }

    /// <summary>…and the non-shell window must actually call the helper.
    /// The shell keeps its own <c>ApplyUiScale</c> (it also drives full-screen
    /// and the title bar) and is excluded by name.</summary>
    [Theory]
    [InlineData(@"Views\Diagnostics\DiagnosticsWindow.axaml.cs")]
    public void TheNonShellWindows_AttachTheScale(string relativePath)
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relativePath)));
        Assert.Matches(new Regex(@"WindowUiScale\.Attach\(this\)"), source);
    }
}
