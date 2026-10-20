using System.Text.RegularExpressions;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The visual lead's ONE rule for both render clocks (#112): the live lead while
///     playing, an ease-out on pause, then exactly 0 — the 3D highway added the live
///     lead unconditionally, so every idle frame of a paused highway landed at a
///     slightly different time and the notes crept and flickered.
/// </summary>
public sealed class RenderLeadEaseTests
{
    [Fact]
    public void WhilePlaying_TheLiveLeadApplies()
    {
        var ease = new RenderLeadEase();
        Assert.Equal(30.0, ease.Resolve(true, 30.0, 1000));
        Assert.Equal(31.5, ease.Resolve(true, 31.5, 1016));
        Assert.False(ease.IsEasing);
    }

    /// <summary>A parked playhead is drawn at its own time, whatever the lead's EMA
    /// does between idle frames — the bug was exactly that it did not.</summary>
    [Fact]
    public void Paused_AfterTheEase_TheLeadIsExactlyZero_WhateverTheLiveLead()
    {
        var ease = new RenderLeadEase();
        ease.Resolve(true, 40.0, 1000);
        ease.Resolve(false, 40.0, 1001);
        for (var i = 0; i < 50; i++)
            Assert.Equal(0.0, ease.Resolve(false, 20.0 + i * 0.37, 1001 + RenderLeadEase.DecayMs + i * 100));
        Assert.False(ease.IsEasing);

        var neverPlayed = new RenderLeadEase();
        Assert.Equal(0.0, neverPlayed.Resolve(false, 45.0, 5));
    }

    /// <summary>On pause the residual eases out as lead × (1 − t)² over DecayMs —
    /// no snap back — and the loop is told to keep drawing until it settles.</summary>
    [Fact]
    public void OnPause_TheResidualEasesOut()
    {
        var ease = new RenderLeadEase();
        ease.Resolve(true, 40.0, 1000);
        Assert.Equal(40.0, ease.Resolve(false, 99.0, 2000), 6);              // the edge frame: t = 0
        Assert.True(ease.IsEasing);
        Assert.Equal(40.0 * 0.25, ease.Resolve(false, 99.0, 2000 + RenderLeadEase.DecayMs / 2), 6);   // t = 0.5
        Assert.Equal(0.0, ease.Resolve(false, 99.0, 2000 + RenderLeadEase.DecayMs));
        Assert.False(ease.IsEasing);
    }

    /// <summary>Both render clocks go through the rule; neither adds the lead itself.</summary>
    [Fact]
    public void BothRenderers_ResolveTheLeadThroughTheSharedRule()
    {
        var driver = StripComments(Preview3DRenderDriverSource.ReadAllText());
        Assert.Contains("_leadEase.Resolve(", driver);
        Assert.DoesNotMatch(@"localTimeMs\s*\+=\s*VisualLeadTiming\.EffectiveLeadMs", driver);
        Assert.Contains("_leadEase.IsEasing", driver);

        var timeline = Code("DotCharter.UI/Controls/ChartRenderer.cs");
        Assert.Contains("_leadEase.Resolve(", timeline);
        Assert.Contains("_leadEase.IsEasing", timeline);
    }

    private static string Code(string relativePath) =>
        StripComments(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }
}
