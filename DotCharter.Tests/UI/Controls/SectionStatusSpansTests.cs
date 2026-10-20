using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     v0.29 minimap section-status strip: pure span math — each statused
///     section owns the stretch to the NEXT section marker or the chart end;
///     untracked sections draw nothing but still terminate their neighbor.
/// </summary>
public sealed class SectionStatusSpansTests
{
    private static SectionStatusSpans.SectionPoint P(double ms, SectionStatus status) => new(ms, status);

    [Fact]
    public void AStatusedSection_SpansToTheNextSectionMarker()
    {
        var spans = SectionStatusSpans.Build(
            [P(1000, SectionStatus.Draft), P(3000, SectionStatus.Done)], totalMs: 10_000);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new SectionStatusSpans.Span(1000, 3000, SectionStatus.Draft), spans[0]);
        Assert.Equal(new SectionStatusSpans.Span(3000, 10_000, SectionStatus.Done), spans[1]);
    }

    [Fact]
    public void AnUntrackedSection_DrawsNothing_ButTerminatesItsNeighbor()
    {
        var spans = SectionStatusSpans.Build(
            [P(1000, SectionStatus.Review), P(4000, SectionStatus.None)], totalMs: 10_000);

        var span = Assert.Single(spans);
        Assert.Equal(4000, span.EndMs, 3); // NOT 10_000 — the untracked section is not part of it
    }

    [Fact]
    public void UnsortedInput_IsHandled()
    {
        var spans = SectionStatusSpans.Build(
            [P(5000, SectionStatus.Done), P(1000, SectionStatus.Todo)], totalMs: 8000);

        Assert.Equal(2, spans.Count);
        Assert.Equal(1000, spans[0].StartMs, 3);
        Assert.Equal(5000, spans[0].EndMs, 3);
    }

    [Fact]
    public void SpansClampToTheChart()
    {
        // A marker past the end (stale beats) yields nothing; a negative start clamps.
        var spans = SectionStatusSpans.Build(
            [P(-500, SectionStatus.Draft), P(12_000, SectionStatus.Done)], totalMs: 10_000);

        var span = Assert.Single(spans);
        Assert.Equal(0, span.StartMs, 3);
        Assert.Equal(10_000, span.EndMs, 3);
        Assert.Equal(SectionStatus.Draft, span.Status);
    }

    [Fact]
    public void NoSections_NoSpans()
        => Assert.Empty(SectionStatusSpans.Build([], totalMs: 10_000));
}
