using System.Globalization;
using DotCharter.Services.Editing.Highway;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

public sealed class TimeNudgeStatusTests
{
    [Fact]
    public void A_move_reports_the_count_and_the_signed_milliseconds()
    {
        var loc = FakeLocalization.English();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var (text, severity) = TimeNudgeStatus.Describe(new TimeMoveResult(TimeMoveOutcome.Moved, 8, 10), loc);

            Assert.Equal("Moved 8 items +10 ms", text);
            Assert.Equal(StatusSeverity.Info, severity);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(TimeMoveOutcome.Clamped, "first beat")]
    [InlineData(TimeMoveOutcome.AtStart, "first beat")]
    [InlineData(TimeMoveOutcome.Collision, "existing item")]
    [InlineData(TimeMoveOutcome.HandShapeOverlap, "hand shape")]
    [InlineData(TimeMoveOutcome.NothingSelected, "Nothing selected")]
    public void A_refusal_or_clamp_says_why_as_a_warning(TimeMoveOutcome outcome, string fragment)
    {
        var (text, severity) = TimeNudgeStatus.Describe(new TimeMoveResult(outcome, 2, -5), FakeLocalization.English());

        Assert.Contains(fragment, text);
        Assert.Equal(StatusSeverity.Warning, severity);
    }
}
