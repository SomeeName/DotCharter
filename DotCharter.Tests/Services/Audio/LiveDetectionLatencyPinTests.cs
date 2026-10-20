using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The live detector's REPORT DELAY, pinned — and the reason the app does not
///     subtract it anywhere.
///     <para>
///         <c>JuceGuitarAnalyzer.LatencySamples</c> moved from 7,624 to 8,392 at
///         44.1 kHz in the ABI v36 wave (it was short of what the post-refinement
///         reads actually consume). Nothing in the parent read the property, so
///         nothing went red: a +768-sample move landed in live-play scoring with
///         no test anywhere in either tree seeing it. This is that test.
///     </para>
///     <para>
///         <b>It is a report delay, not a timestamp bias, and that distinction is
///         the whole finding.</b> An event fires at capture-stream position
///         <c>P</c> and is EMITTED once the stream reaches <c>P + LatencySamples</c>,
///         but it still carries <c>P</c> — and the judging path dates an attack
///         by mapping <c>P</c> through the capture-sync pair
///         (<c>LivePlayViewModel.Judging</c>), so the age they subtract is MEASURED
///         per event and already contains this delay. A static compensation on top
///         would double-count it. What DOES move with this number is the fallback
///         arm both take when no capture-sync pair is available: it treats the
///         event as happening now and so under-dates it by the whole report delay,
///         which this wave made ~17 ms worse — a pre-existing shape, not a new bug.
///     </para>
/// </summary>
public sealed class LiveDetectionLatencyPinTests
{
    /// <summary>
    ///     The number itself, at both rates the app runs at. A silent move is
    ///     exactly what happened last time; this makes the next one loud.
    /// </summary>
    [Theory]
    [InlineData(44100, 8392)]
    [InlineData(48000, 8704)]
    public void TheAnalyzersReportDelay_IsPinned(int sampleRate, int expectedSamples)
    {
        var analyzer = new JuceGuitarAnalyzer(sampleRate);

        Assert.Equal(expectedSamples, analyzer.LatencySamples);
    }

    /// <summary>
    ///     …and it is a REPORT delay: the judge dates an event from the event's own
    ///     stream position, not from when the event arrived. If this ever stops
    ///     being true, the number above becomes a bias and something has to
    ///     subtract it.
    /// </summary>
    [Fact]
    public void TheSoloJudge_DatesAnEventFromItsOwnStreamPosition()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor",
            "LivePlayViewModel.Judging.cs")));

        Assert.Contains("(capturedSamples - ev.StreamSamplePosition)", source, StringComparison.Ordinal);
        // The age is measured against the capture-sync pair, which is what makes
        // the analyzer's report delay self-compensating.
        Assert.Contains("TryGetInputCaptureSync(out var capturedSamples", source, StringComparison.Ordinal);
        // And nothing subtracts the analyzer's latency a second time.
        Assert.DoesNotContain("LatencySamples", source, StringComparison.Ordinal);
    }
}
