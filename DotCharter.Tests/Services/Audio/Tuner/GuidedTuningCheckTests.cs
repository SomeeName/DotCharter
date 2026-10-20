using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class GuidedTuningCheckTests
{
    private const double TickMs = 100;

    private static Arrangement StandardGuitar()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        return arrangement;
    }

    private static GuidedTuningCheck Check(Arrangement? arrangement = null)
        => new(arrangement ?? StandardGuitar()) { StabilityHoldMs = 600 };

    /// <summary>Feeds enough spot-on readings of the CURRENT target to
    /// confirm it (hold 600 ms at 100 ms ticks needs 7 readings), starting
    /// at <paramref name="startMs"/>. Returns the time after the last tick.</summary>
    private static double ConfirmCurrent(GuidedTuningCheck check, double startMs)
    {
        var frequency = check.CurrentTarget.FrequencyHz;
        var t = startMs;
        for (var i = 0; i < 7; i++, t += TickMs)
            check.Process(frequency, t);
        return t;
    }

    [Fact]
    public void InitialState_LowestStringIsCurrent_RestWaiting()
    {
        var check = Check();

        Assert.Equal(0, check.CurrentStringIndex);
        Assert.Equal(TuningCheckStringState.Detecting, check.StateOf(0));
        for (var i = 1; i < 6; i++)
            Assert.Equal(TuningCheckStringState.Waiting, check.StateOf(i));
        Assert.False(check.IsComplete);
        Assert.Equal("E2", check.CurrentTarget.NoteName);
    }

    [Fact]
    public void HappyPath_ConfirmsAllSixInOrder_ThenCompletes()
    {
        var check = Check();
        var confirmed = new List<int>();
        var completed = 0;
        check.StringConfirmed += confirmed.Add;
        check.Completed += () => completed++;

        double t = 0;
        for (var s = 0; s < 6; s++)
            t = ConfirmCurrent(check, t);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, confirmed);
        Assert.True(check.IsComplete);
        Assert.Equal(1, completed);
        for (var i = 0; i < 6; i++)
            Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(i));
    }

    // Boundary values (±5/±10) are asserted just inside the band — the cents
    // round-trip through pow/log2 carries float noise either side of exact.
    [Theory]
    [InlineData(0.0, TuningCheckStringState.InTune)]
    [InlineData(4.95, TuningCheckStringState.InTune)]
    [InlineData(-4.95, TuningCheckStringState.InTune)]
    [InlineData(7.0, TuningCheckStringState.Close)]
    [InlineData(-9.9, TuningCheckStringState.Close)]
    [InlineData(9.95, TuningCheckStringState.Close)]
    [InlineData(25.0, TuningCheckStringState.Detecting)]
    [InlineData(-120.0, TuningCheckStringState.Detecting)]
    public void Bands_ClassifyByCents(double cents, TuningCheckStringState expected)
    {
        var check = Check();
        var frequency = check.CurrentTarget.FrequencyHz * Math.Pow(2, cents / 1200.0);

        check.Process(frequency, 0);

        Assert.Equal(expected, check.StateOf(0));
    }

    [Fact]
    public void Confirmation_RequiresTheCumulativeHold()
    {
        var check = Check();
        var frequency = check.CurrentTarget.FrequencyHz;

        // 6 readings = 500 ms of credit (the first only anchors) — not enough.
        for (var i = 0; i < 6; i++)
            check.Process(frequency, i * TickMs);
        Assert.Equal(TuningCheckStringState.InTune, check.StateOf(0));

        // The 7th crosses 600 ms.
        check.Process(frequency, 6 * TickMs);
        Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(0));
        Assert.Equal(1, check.CurrentStringIndex);
    }

    [Fact]
    public void OutOfBandReading_ResetsTheHold()
    {
        var check = Check();
        var inTune = check.CurrentTarget.FrequencyHz;
        var off = inTune * Math.Pow(2, 15.0 / 1200.0);

        for (var i = 0; i < 5; i++) check.Process(inTune, i * TickMs); // 400 ms credit
        check.Process(off, 500); // player kept turning — stale hold

        // Needs the full hold again: 7 fresh readings.
        for (var i = 0; i < 6; i++)
        {
            check.Process(inTune, 600 + i * TickMs);
            Assert.Equal(TuningCheckStringState.InTune, check.StateOf(0));
        }

        check.Process(inTune, 1200);
        Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(0));
    }

    [Fact]
    public void HoldSurvivesSilenceBetweenPlucks_ButTheGapIsNotCredited()
    {
        var check = Check();
        var frequency = check.CurrentTarget.FrequencyHz;

        for (var i = 0; i < 4; i++) check.Process(frequency, i * TickMs); // 300 ms credit
        check.ProcessSilence(); // string decayed

        // Next pluck 5 s later: the gap must not count, the 300 ms must remain.
        check.Process(frequency, 5300); // re-anchor, no credit
        for (var i = 1; i <= 2; i++) check.Process(frequency, 5300 + i * TickMs); // +200 ms = 500 ms
        Assert.Equal(TuningCheckStringState.InTune, check.StateOf(0));

        check.Process(frequency, 5600); // +100 ms = 600 ms
        Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(0));
    }

    [Fact]
    public void LongGapWithoutSilenceReport_IsCappedPerReading()
    {
        var check = Check();
        var frequency = check.CurrentTarget.FrequencyHz;

        check.Process(frequency, 0);     // anchor
        check.Process(frequency, 5000);  // credit capped at 250 ms, not 5000
        Assert.Equal(TuningCheckStringState.InTune, check.StateOf(0));
        Assert.NotEqual(TuningCheckStringState.Confirmed, check.StateOf(0));
    }

    [Fact]
    public void WrongString_IsFlagged_AndDoesNotAdvanceAnything()
    {
        var check = Check();
        TuningCheckProgress last = default;
        check.ProgressChanged += p => last = p;

        // Current is E2 (82.41 Hz); the player plucks the A string (110 Hz).
        check.Process(110.0, 0);

        Assert.True(last.WrongStringSuspected);
        Assert.Equal(TuningCheckStringState.Detecting, check.StateOf(0));
        Assert.Equal(0, check.CurrentStringIndex);
    }

    [Fact]
    public void OneStringAtATime_AnInTuneReadingForAnotherStringNeverConfirmsIt()
    {
        // Patent-relevant behavior (US8334449B2): readings are judged ONLY
        // against the CURRENT string. A perfect G3 while checking E2 must
        // not touch string 3's state.
        var check = Check();
        var g3 = 196.0;

        for (var i = 0; i < 20; i++) check.Process(g3, i * TickMs);

        Assert.Equal(TuningCheckStringState.Waiting, check.StateOf(3));
        Assert.Equal(0, check.CurrentStringIndex);
        Assert.False(check.IsComplete);
    }

    [Fact]
    public void Skip_MarksAndAdvances_AndSkippedCountsTowardCompletion()
    {
        var check = Check();
        var completed = 0;
        check.Completed += () => completed++;

        check.SkipCurrentString();
        Assert.Equal(TuningCheckStringState.Skipped, check.StateOf(0));
        Assert.Equal(1, check.CurrentStringIndex);

        double t = 0;
        for (var s = 1; s < 6; s++) t = ConfirmCurrent(check, t);

        Assert.True(check.IsComplete);
        Assert.Equal(1, completed);
        Assert.Equal(TuningCheckStringState.Skipped, check.StateOf(0));
    }

    [Fact]
    public void SkippingEveryString_Completes()
    {
        var check = Check();
        var completed = 0;
        check.Completed += () => completed++;

        for (var s = 0; s < 6; s++) check.SkipCurrentString();

        Assert.True(check.IsComplete);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void SelectString_RevisitsASkippedString()
    {
        var check = Check();
        check.SkipCurrentString();

        check.SelectString(0);
        Assert.Equal(0, check.CurrentStringIndex);
        Assert.Equal(TuningCheckStringState.Detecting, check.StateOf(0));

        // The abandoned string 1 returns to Waiting.
        Assert.Equal(TuningCheckStringState.Waiting, check.StateOf(1));

        ConfirmCurrent(check, 0);
        Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(0));
        Assert.Equal(1, check.CurrentStringIndex);
    }

    [Fact]
    public void SelectString_RecheckingAConfirmedString_ReopensTheCheck()
    {
        var check = Check();
        double t = 0;
        for (var s = 0; s < 6; s++) t = ConfirmCurrent(check, t);
        Assert.True(check.IsComplete);

        check.SelectString(2);
        Assert.False(check.IsComplete);
        Assert.Equal(TuningCheckStringState.Detecting, check.StateOf(2));

        var completed = 0;
        check.Completed += () => completed++;
        ConfirmCurrent(check, t);
        Assert.True(check.IsComplete);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Restart_ResetsEverything()
    {
        var check = Check();
        ConfirmCurrent(check, 0);
        check.SkipCurrentString();

        check.Restart();
        Assert.Equal(0, check.CurrentStringIndex);
        Assert.False(check.IsComplete);
        Assert.Equal(TuningCheckStringState.Detecting, check.StateOf(0));
        for (var i = 1; i < 6; i++)
            Assert.Equal(TuningCheckStringState.Waiting, check.StateOf(i));
    }

    [Fact]
    public void ProgressEvents_CarryCentsAndHoldProgress()
    {
        var check = Check();
        var frames = new List<TuningCheckProgress>();
        check.ProgressChanged += frames.Add;

        var slightlyFlat = check.CurrentTarget.FrequencyHz * Math.Pow(2, -3.0 / 1200.0);
        for (var i = 0; i < 4; i++) check.Process(slightlyFlat, i * TickMs);

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Equal(0, f.StringIndex));
        Assert.All(frames, f => Assert.InRange(f.CentsOff, -3.5, -2.5));
        Assert.True(frames[^1].HoldProgress > frames[0].HoldProgress);
        Assert.Equal(0.5, frames[^1].HoldProgress, 2); // 300 of 600 ms
    }

    [Fact]
    public void ConfirmedFrame_ReportsFullHoldProgress()
    {
        var check = Check();
        TuningCheckProgress last = default;
        check.ProgressChanged += p => last = p;

        ConfirmCurrent(check, 0);
        Assert.Equal(TuningCheckStringState.Confirmed, last.State);
        Assert.Equal(1.0, last.HoldProgress, 3);
    }

    [Fact]
    public void DropDArrangement_ChecksAgainstTheDroppedTarget()
    {
        var dropD = StandardGuitar();
        dropD.TuningPitches[0].Step = -2;

        var check = new GuidedTuningCheck(dropD) { StabilityHoldMs = 600 };
        Assert.Equal("D2", check.CurrentTarget.NoteName);

        // A player still on E2 reads ~+200¢ off the D2 target → wrong-string zone.
        TuningCheckProgress last = default;
        check.ProgressChanged += p => last = p;
        check.Process(82.41, 0);
        Assert.True(last.WrongStringSuspected);

        ConfirmCurrent(check, 100);
        Assert.Equal(TuningCheckStringState.Confirmed, check.StateOf(0));
    }

    [Fact]
    public void CapoArrangement_ExposesTheCapoFretForTheUiInstruction()
    {
        var capoed = StandardGuitar();
        capoed.Capo = 2;

        var check = new GuidedTuningCheck(capoed);
        Assert.Equal(2, check.CapoFret);
        Assert.Equal("E2", check.CurrentTarget.NoteName); // default flow: open targets
    }

    [Fact]
    public void CompletedCheck_IgnoresFurtherReadings()
    {
        var check = Check();
        double t = 0;
        for (var s = 0; s < 6; s++) t = ConfirmCurrent(check, t);

        var frames = 0;
        check.ProgressChanged += _ => frames++;
        check.Process(check.CurrentTarget.FrequencyHz, t + 100);

        Assert.Equal(0, frames);
        Assert.True(check.IsComplete);
    }

    [Fact]
    public void InvalidConstruction_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GuidedTuningCheck(Array.Empty<TuningCheckTarget>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check().SelectString(6));
    }

    [Fact]
    public void Process_IsAllocationFree_AfterWarmup()
    {
        var check = Check();
        var counter = 0;
        check.ProgressChanged += _ => counter++; // subscription allocates once, before measuring

        var inTune = check.CurrentTarget.FrequencyHz;
        var off = inTune * 1.02;
        check.Process(inTune, 0);
        check.Process(off, 100);
        check.ProcessSilence();

        // The timestamps keep advancing across batches — the check is
        // time-driven, so replaying the same clock would measure a different
        // path than the one that runs live.
        var nowMs = 200.0;
        AllocationProbe.AssertAllocationFree("GuidedTuningCheck.Process", () =>
        {
            for (var i = 0; i < 50; i++)
            {
                check.Process(i % 2 == 0 ? inTune : off, nowMs);
                if (i % 7 == 0) check.ProcessSilence();
                nowMs += 10;
            }
        });

        Assert.True(counter > 0);
    }
}
