using Avalonia.Media;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The hover placement GHOST must read as an insertion point, not as a
///     committed note. At the old flat 50% alpha the preview looked like a
///     paler note that was already there, and the live audit watched stray
///     clicks "silently" mutate the chart — the feedback existed, it just was
///     not distinct. The ghost's FILL drops to a wash while its rim keeps the
///     50% alpha, so the gem reads hollow.
/// </summary>
public sealed class PreviewGhostStyleTests
{
    [Fact]
    public void APreviewFill_DropsToAWash_KeepingItsColour()
    {
        var fill = Color.FromArgb(255, 200, 100, 50);
        var ghost = HighwayNoteHeadRenderer.GhostFill(fill, isPreview: true);

        Assert.Equal(HighwayNoteHeadRenderer.PreviewFillAlpha, ghost.A);
        Assert.Equal((fill.R, fill.G, fill.B), (ghost.R, ghost.G, ghost.B));
    }

    [Fact]
    public void ACommittedFill_PassesThroughUntouched()
    {
        var fill = Color.FromArgb(255, 200, 100, 50);
        Assert.Equal(fill, HighwayNoteHeadRenderer.GhostFill(fill, isPreview: false));
    }

    /// <summary>The wash must sit FAR below the ghost rim's 128 — a fill at
    /// rim alpha is exactly the paler-copy look this replaces.</summary>
    [Fact]
    public void TheWash_IsFarBelowTheGhostRimAlpha()
    {
        Assert.True(HighwayNoteHeadRenderer.PreviewFillAlpha <= 64,
            "PreviewFillAlpha crept toward the rim's 128 — the ghost stops reading as hollow.");
    }

    /// <summary>Source lint: the gem fills actually route through the helper —
    /// the renderer needs a GPU-backed batcher, so the wiring is pinned at the
    /// source level like the other Input lints.</summary>
    [Fact]
    public void TheHeadRenderer_RoutesItsFillsThroughGhostFill()
    {
        var source = UiSourceTree.ReadAllText(RepoPath(
            "DotCharter.UI/Controls/Layers/Helpers/HighwayNoteHeadRenderer.cs"));
        Assert.Contains("gemTop = GhostFill(gemTop, data.IsPreview);", source);
        Assert.Contains("gemBottom = GhostFill(gemBottom, data.IsPreview);", source);
        Assert.Contains("darkColor = GhostFill(darkColor, data.IsPreview);", source);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
