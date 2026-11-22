using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A REVIEW capture of the 2D timeline's bar lines and bar labels around the tempo-map verbs,
///     without a GPU (the renderer of <see cref="HandShapeGhostGemCaptureTests" />). Env-gated:
///     <c>DOTCHARTER_TEMPO_MAP_CAPTURE=&lt;dir outside the repo&gt;</c> writes the grid before, after two
///     Insert Beat at the second beat of bar 1, and after Insert Measures (2) at bar 3. The
///     structural assertions are <see cref="TempoMapMeasureReflowTests" />.
/// </summary>
public sealed class TempoMapReflowCaptureTests
{
    [Fact]
    public void Capture_BarLinesAroundInsertBeatAndInsertMeasures()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_TEMPO_MAP_CAPTURE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "Set DOTCHARTER_TEMPO_MAP_CAPTURE to save the timeline.");

        var h = new ChartPowerToolsTests.Harness();
        Save(h, Path.Combine(dir!, "timeline-before.png"));

        for (var i = 0; i < 2; i++)
        {
            h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[1].Time;
            h.Maintenance.InsertBeatAtPlayhead();
        }
        Save(h, Path.Combine(dir!, "timeline-after-two-insert-beats.png"));

        var m = new ChartPowerToolsTests.Harness();
        m.State.CurrentTimeMs = m.State.ActiveProject!.Beats[9].Time;
        m.Maintenance.InsertMeasuresAtPlayhead(2);
        Save(m, Path.Combine(dir!, "timeline-after-insert-two-measures.png"));
    }

    private static void Save(ChartPowerToolsTests.Harness h, string path) =>
        HandShapeGhostGemCaptureTests.Capture(h.State.ActiveArrangement!, h.State.ActiveArrangement!.Levels[0],
            [.. h.State.ActiveProject!.Beats], path, zoom: 0.05, beatGrid: true);
}
