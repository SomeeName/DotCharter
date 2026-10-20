namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
/// #282: "Add Measures at Start" extended the grid backwards at the first beat's
/// tempo, so on a grid that starts near 0:00 the new beats landed BEFORE the audio,
/// which Rocksmith's beat list cannot hold. The owner's rule (2026-10-07): add only
/// the whole measures that fit, say so, and add nothing when none fit.
/// The harness grid is 120 BPM 4/4, so one measure is 2,000 ms.
/// </summary>
public sealed class AddMeasuresAtStartClampTests
{
    private static ChartPowerToolsTests.Harness HarnessStartingAt(double firstBeatMs)
    {
        var h = new ChartPowerToolsTests.Harness();
        foreach (var beat in h.State.ActiveProject!.Beats) beat.Time += firstBeatMs;
        return h;
    }

    [Fact]
    public void AGridStartingAtZero_GetsNothing_AndSaysWhy()
    {
        var h = HarnessStartingAt(0);
        var before = h.State.ActiveProject!.Beats.Count;
        var node = h.History.CurrentNodeId;

        h.Maintenance.AddMeasuresAtStart(2);

        Assert.Equal(before, h.State.ActiveProject!.Beats.Count);
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.Equal(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }

    [Fact]
    public void OnlyTheMeasuresThatFit_AreAdded()
    {
        var h = HarnessStartingAt(4500);
        var before = h.State.ActiveProject!.Beats.Count;

        h.Maintenance.AddMeasuresAtStart(3);

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(before + 2 * 4, beats.Count);
        Assert.All(beats, b => Assert.True(b.Time >= 0, $"beat at {b.Time} ms is before 0:00"));
        Assert.Equal(500, beats[0].Time, 3);
        Assert.Equal(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }

    [Fact]
    public void ARequestThatFits_IsAddedInFull()
    {
        var h = HarnessStartingAt(4500);
        var before = h.State.ActiveProject!.Beats.Count;

        h.Maintenance.AddMeasuresAtStart(1);

        Assert.Equal(before + 4, h.State.ActiveProject!.Beats.Count);
        Assert.NotEqual(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1999, 0)]
    [InlineData(2000, 1)]
    [InlineData(4500, 2)]
    public void MeasuresThatFit_AreCountedFromTheFirstBeat(double firstBeatMs, int expected)
    {
        var h = HarnessStartingAt(firstBeatMs);
        Assert.Equal(expected, TempoMapBeats.MeasuresThatFitBeforeFirstBeat(h.State.ActiveProject!));
    }
}
