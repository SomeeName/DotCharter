using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

public sealed class ArrangementTunerLogicTests
{
    private static ArrangementTunerLogic Loaded(
        ArrangementType type = ArrangementType.Lead,
        int stringCount = 6,
        params int[] tuningSteps)
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(type, stringCount, tuningSteps);
        return logic;
    }

    [Fact]
    public void Load_StandardGuitar_TargetsE2ToE4()
    {
        var logic = Loaded();

        Assert.Equal(new[] { 40, 45, 50, 55, 59, 64 }, logic.Targets.Select(t => t.Midi));
        Assert.Equal(new[] { "E2", "A2", "D3", "G3", "B3", "E4" }, logic.Targets.Select(t => t.NoteName));
    }

    [Fact]
    public void Load_DropD_LowersOnlyTheLowString()
    {
        var logic = Loaded(tuningSteps: [-2, 0, 0, 0, 0, 0]);

        Assert.Equal(38, logic.Targets[0].Midi);
        Assert.Equal("D2", logic.Targets[0].NoteName);
        Assert.Equal(45, logic.Targets[1].Midi);
    }

    [Fact]
    public void Load_StandardBass_TargetsE1ToG2()
    {
        var logic = Loaded(ArrangementType.Bass, 4);

        Assert.Equal(new[] { 28, 33, 38, 43 }, logic.Targets.Select(t => t.Midi));
        Assert.Equal(new[] { "E1", "A1", "D2", "G2" }, logic.Targets.Select(t => t.NoteName));
    }

    [Fact]
    public void Load_ZeroStringCount_FallsBackPerInstrument()
    {
        Assert.Equal(6, Loaded(ArrangementType.Lead, 0).Targets.Count);
        Assert.Equal(4, Loaded(ArrangementType.Bass, 0).Targets.Count);
    }

    [Fact]
    public void Load_FromArrangement_UsesTuningPitchesAndStringCount()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        foreach (var step in new[] { -2, -2, -2, -2 }) arr.TuningPitches.Add(new TuningOffset(step));

        var logic = new ArrangementTunerLogic();
        logic.Load(arr);

        // Bass D standard: D1 G1 C2 F2.
        Assert.Equal(new[] { 26, 31, 36, 41 }, logic.Targets.Select(t => t.Midi));
    }

    [Fact]
    public void CentsFrom_ExactAndSharpFrequencies()
    {
        Assert.Equal(0.0, ArrangementTunerLogic.CentsFrom(440.0, 69), 6);
        // 445 Hz vs A4: 1200 * log2(445/440) ≈ +19.56 cents.
        Assert.Equal(19.56, ArrangementTunerLogic.CentsFrom(445.0, 69), 1);
        // One full semitone flat.
        Assert.Equal(-100.0, ArrangementTunerLogic.CentsFrom(ArrangementTunerLogic.FrequencyOf(68), 69), 4);
    }

    [Fact]
    public void NearestString_PicksTheClosestTarget()
    {
        var logic = Loaded();

        Assert.Equal(1, logic.NearestString(110.0));  // A2
        Assert.Equal(0, logic.NearestString(80.0));   // slightly flat E2
        Assert.Equal(5, logic.NearestString(340.0));  // sharp E4
    }

    [Fact]
    public void Process_ConfirmsAfterCumulativeHold()
    {
        var logic = Loaded();
        var e2 = ArrangementTunerLogic.FrequencyOf(40);

        var frame = logic.Process(e2, 300);
        Assert.True(frame.InTune);
        Assert.False(frame.StringJustConfirmed);
        Assert.False(logic.IsStringTuned(0));

        // Cumulative: the second in-tune stretch pushes past the 500 ms hold.
        frame = logic.Process(e2, 300);
        Assert.True(frame.StringJustConfirmed);
        Assert.True(logic.IsStringTuned(0));
        Assert.False(logic.AllTuned);
    }

    [Fact]
    public void Process_OutOfTuneReadingResetsConfirmation()
    {
        var logic = Loaded();
        var e2 = ArrangementTunerLogic.FrequencyOf(40);

        logic.Process(e2, 600);
        Assert.True(logic.IsStringTuned(0));

        // ~+53 cents on the same string: detuned again — confirmation drops.
        var frame = logic.Process(85.0, 66);
        Assert.Equal(0, frame.NearestString);
        Assert.True(frame.InRange);
        Assert.False(frame.InTune);
        Assert.False(logic.IsStringTuned(0));

        // And the hold must restart from zero, not resume.
        frame = logic.Process(e2, 300);
        Assert.False(frame.StringJustConfirmed);
        Assert.False(logic.IsStringTuned(0));
    }

    /// <summary>A stray note far from every string (1 kHz, well above the
    /// high E) is reported but never un-confirms the string it lands nearest.</summary>
    [Fact]
    public void Process_StrayNoteFarFromEveryString_DoesNotResetAConfirmedString()
    {
        var logic = Loaded();
        logic.Process(ArrangementTunerLogic.FrequencyOf(64), 600);
        Assert.True(logic.IsStringTuned(5));

        var frame = logic.Process(1000.0, 66);

        Assert.Equal(5, frame.NearestString);
        Assert.False(frame.InRange);
        Assert.False(frame.InTune);
        Assert.True(logic.IsStringTuned(5));
    }

    /// <summary>An octave-off reading (E1 under the low E2) is NOT folded onto the
    /// string — that would credit a string from a note the player did not tune —
    /// and it never resets it either.</summary>
    [Fact]
    public void Process_OctaveOffReading_NeitherConfirmsNorResets()
    {
        var logic = Loaded();
        var e2 = ArrangementTunerLogic.FrequencyOf(40);
        var e1 = ArrangementTunerLogic.FrequencyOf(28);

        // Not folded: a whole second of E1 confirms nothing.
        var frame = logic.Process(e1, 1000);
        Assert.Equal(0, frame.NearestString);
        Assert.False(frame.InRange);
        Assert.False(logic.IsStringTuned(0));

        logic.Process(e2, 600);
        Assert.True(logic.IsStringTuned(0));

        logic.Process(e1, 66);
        Assert.True(logic.IsStringTuned(0));
    }

    /// <summary>The hold survives an out-of-range reading too: it is neither
    /// credited nor zeroed.</summary>
    [Fact]
    public void Process_OutOfRangeReading_KeepsTheHoldBuiltSoFar()
    {
        var logic = Loaded();
        var e2 = ArrangementTunerLogic.FrequencyOf(40);

        logic.Process(e2, 300);
        logic.Process(ArrangementTunerLogic.FrequencyOf(40 + 2.0), 66); // +200 ¢: out of range
        var frame = logic.Process(e2, 300);

        Assert.True(frame.StringJustConfirmed);
    }

    /// <summary>The boundary: inside <see cref="ArrangementTunerLogic.MaxStringDistanceCents" />
    /// an off reading still resets the string, just outside it does not.</summary>
    [Theory]
    [InlineData(140.0, true)]
    [InlineData(-140.0, true)]
    [InlineData(160.0, false)]
    [InlineData(-160.0, false)]
    public void Process_OffReading_ResetsOnlyWithinTheMaximumDistance(double cents, bool resets)
    {
        var logic = Loaded();
        logic.Process(ArrangementTunerLogic.FrequencyOf(55), 600); // G3
        Assert.True(logic.IsStringTuned(3));

        var frame = logic.Process(ArrangementTunerLogic.FrequencyOf(55 + cents / 100.0), 66);

        Assert.Equal(3, frame.NearestString);
        Assert.Equal(resets, frame.InRange);
        Assert.Equal(!resets, logic.IsStringTuned(3));
    }

    /// <summary>The distance is measured in the chart's own reference, so a
    /// 100 ¢-flat chart's string is in range where its own target sits.</summary>
    [Fact]
    public void IsInRange_IsMeasuredAgainstTheChartsReference()
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(ArrangementType.Lead, 6, [], -100.0);
        var e4 = ArrangementTunerLogic.FrequencyOf(64);

        Assert.True(logic.IsInRange(ArrangementTunerLogic.FrequencyOf(64 - 1.0), 5));
        Assert.True(logic.IsInRange(e4, 5));                                         // 100 ¢ sharp of it
        Assert.False(logic.IsInRange(ArrangementTunerLogic.FrequencyOf(64 + 0.6), 5)); // 160 ¢ sharp
        Assert.False(logic.IsInRange(e4, 6));
        Assert.False(logic.IsInRange(e4, -1));
    }

    [Fact]
    public void Process_AllStringsConfirmed_ReportsAllTuned()
    {
        var logic = Loaded(ArrangementType.Bass, 4);

        foreach (var target in logic.Targets)
            logic.Process(target.FrequencyHz, 600);

        Assert.True(logic.AllTuned);
    }

    [Fact]
    public void Process_SlightlyOffButWithinTolerance_CountsAsInTune()
    {
        var logic = Loaded();
        // +4 cents on A2 stays inside the ±5 cent window.
        var freq = ArrangementTunerLogic.FrequencyOf(45 + 0.04);

        var frame = logic.Process(freq, 66);

        Assert.Equal(1, frame.NearestString);
        Assert.True(frame.InTune);
        Assert.Equal(4.0, frame.CentsOff, 6);
    }
}
