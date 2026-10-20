using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The English head is an OPTIONAL 303 MB download blended on top of the
///     multilingual one, and <c>LyricModel.IsInstalled</c> is a bare presence
///     check — so the ensemble path is taken the moment two files EXIST. Until
///     v0.44 its load and encode sat in the same try as the rest of the run, so
///     a truncated <c>model.onnx</c>, an unsupported opset or a CUDA OOM with
///     both sessions alive returned <c>LyricAutoSyncOutcome.Failed</c> to a user
///     whose primary model was fine. `docs/agents/services/lyric-autosync.md` states the
///     opposite outright: "one model missing degrades to the other alone".
///     <para>
///     A behavioural test cannot reach this — the primary is 340 MB and outside
///     the gate — so the decision is a seam of its own and is tested there,
///     with a source lint keeping the call site on it.
///     </para>
/// </summary>
public sealed class LyricSecondaryModelDegradationTests
{
    private static CtcEmissions Emissions(float first) =>
        new([first, -1f, -2f, -3f], FrameCount: 2, Width: 2, SecondsPerFrame: 0.02);

    [Fact]
    public void WhenTheSecondaryModelFailsToLoad_TheRunStillAlignsWithThePrimary()
    {
        var primary = Emissions(-0.5f);
        Exception? reported = null;

        var result = LyricAutoSyncService.BlendOptionalSecondary(
            primary,
            _ => throw new InvalidOperationException("model.onnx is truncated"),
            ex => reported = ex);

        // The PRIMARY instance, not an equal one — nothing may be half-blended.
        Assert.Same(primary, result);
        Assert.IsType<InvalidOperationException>(reported);
        Assert.Contains("truncated", reported!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheSecondaryModelWorks_ItsOpinionIsUsed()
    {
        var primary = Emissions(-0.5f);
        var blended = Emissions(-0.25f);

        var result = LyricAutoSyncService.BlendOptionalSecondary(primary, _ => blended, _ => Assert.Fail(
            "a successful blend must not report a degradation"));

        Assert.Same(blended, result);
    }

    /// <summary>Cancelling the run is not the second model failing, and must
    /// still stop the run rather than quietly finishing on the primary.</summary>
    [Fact]
    public void ACancelledRunStillCancels()
    {
        var reported = 0;

        Assert.Throws<OperationCanceledException>(() => LyricAutoSyncService.BlendOptionalSecondary(
            Emissions(-0.5f),
            _ => throw new OperationCanceledException(),
            _ => reported++));

        Assert.Equal(0, reported);
    }

    /// <summary>
    ///     Keeps the three cases above from being about a helper nothing calls:
    ///     the ensemble branch must route through it, and must not have its own
    ///     bare <c>LyricAcousticModel.Load</c> for the secondary beside it.
    /// </summary>
    [Fact]
    public void TheEnsembleBranchRoutesThroughTheSeam()
    {
        var src = File.ReadAllText(RepoPath("DotCharter.Services/Audio/Lyrics/LyricAutoSyncService.cs"));

        var branch = src.IndexOf("selection.Secondary is { } secondary", StringComparison.Ordinal);
        Assert.True(branch > 0, "the ensemble branch is gone — this guard would pass vacuously.");

        var call = src.IndexOf("BlendOptionalSecondary(", branch, StringComparison.Ordinal);
        Assert.True(call > 0, "the ensemble branch no longer degrades through BlendOptionalSecondary.");

        // The blend itself must be inside that lambda, so a failure of EITHER
        // half (load, encode, blend) lands in the same catch.
        var blend = src.IndexOf("CtcEmissionBlender.Blend(", branch, StringComparison.Ordinal);
        Assert.True(blend > call, "the blend escaped the degradation seam.");
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
