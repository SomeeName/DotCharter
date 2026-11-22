using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 (inspector design D): the Note footer prints the playhead as time only, <c>m:ss.cc</c>. It must raise a
///     change itself (a computed label nothing raises keeps the time the view first bound, the 1:37 bug of
///     <c>InspectorPlayheadLabelTests</c>), and it is coalesced like <c>PlayheadLabel</c>: it follows the playhead
///     while stopped, holds still during playback and catches up once when playback stops.
/// </summary>
public sealed class InspectorPlayheadClockTests
{
    [Theory]
    [InlineData(0, "0:00.00")]
    [InlineData(-250, "0:00.00")]
    [InlineData(84_370, "1:24.37")]
    [InlineData(84_379, "1:24.37")]
    [InlineData(605_010, "10:05.01")]
    public void TheLabel_IsMinutesSecondsAndHundredths(double ms, string expected) =>
        Assert.Equal(expected, InspectorPlayheadClock.Format(ms));

    [Fact]
    public void TheLabel_FollowsThePlayheadWhileStopped_HoldsDuringPlayback_AndCatchesUpOnStop()
    {
        var state = new ProjectState();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        using var clock = new InspectorPlayheadClock(state, transport);
        var raised = 0;
        clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InspectorPlayheadClock.TimeLabel)) raised++;
        };

        state.CurrentTimeMs = 84_370;
        Assert.Equal(1, raised);
        Assert.Equal("1:24.37", clock.TimeLabel);

        transport.IsPlaying = true;
        var afterStart = raised;
        state.CurrentTimeMs = 90_000;
        state.CurrentTimeMs = 91_000;
        Assert.Equal(afterStart, raised);

        transport.IsPlaying = false;
        Assert.Equal(afterStart + 1, raised);
        Assert.Equal("1:31.00", clock.TimeLabel);

        clock.Dispose();
        state.CurrentTimeMs = 100;
        Assert.Equal(afterStart + 1, raised);
    }
}
