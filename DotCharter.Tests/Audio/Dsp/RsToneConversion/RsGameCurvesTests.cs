using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     <see cref="RsGameCurves" /> is read from the player's own install (#755), so these
///     pin the curve MATHS against its formulas and the curve SETS against what the
///     builder makes of a known bank — the synthetic one (<see cref="SyntheticRsGame" />,
///     invented numbers). A shape applied in the wrong domain, a lost unit scale or a
///     param id read from the wrong slot moves one of these. Whether a real install
///     builds the curves the rig was tuned against is <c>LocalRsGameTablesTest</c>'s.
/// </summary>
public sealed class RsGameCurvesTests
{
    private static RsGameCurveSet Set => SyntheticRsGame.Tables.Curves;

    // ------------------------------------------------------- stored domain

    [Theory]
    // (dB, sign(d) * (1 - 10^(-|d|/20)))
    [InlineData(20f, 0.9)]
    [InlineData(-20f, -0.9)]
    [InlineData(40f, 0.99)]
    [InlineData(0f, 0.0)]
    public void TheDbScaling_IsItsFormula_BothWays(float db, double stored)
    {
        Assert.Equal(stored, WwiseCurve.ToDbStored(db), 6);
        Assert.Equal(db, WwiseCurve.FromDbStored((float)stored), 3);
    }

    [Fact]
    public void LogScaledCurves_StoreLog10_AndEvaluateInHz()
    {
        var c = WwiseCurve.LogHz(10f, 10f, 1000f, 1000f, WwiseCurveShape.Linear);
        Assert.Equal(1f, c.StoredY0, 6);
        Assert.Equal(3f, c.StoredY1, 6);
        // Linear in log10: the knob's arithmetic middle is the GEOMETRIC middle in Hz.
        Assert.Equal(MathF.Pow(10f, 1f + 2f * (495f / 990f)), c.Evaluate(505f), 0.01f);
    }

    // ------------------------------------------------------- shapes

    [Theory]
    [InlineData((int)WwiseCurveShape.Linear, 0.5f, 0.5)]
    [InlineData((int)WwiseCurveShape.Exp1, 0.5f, 0.37632)]     // 0.5^1.41
    [InlineData((int)WwiseCurveShape.Exp3, 0.5f, 0.125)]
    [InlineData((int)WwiseCurveShape.Log1, 0.5f, 0.62368)]     // 1 - 0.5^1.41
    [InlineData((int)WwiseCurveShape.Log3, 0.5f, 0.875)]
    [InlineData((int)WwiseCurveShape.Sine, 0.5f, 0.70711)]     // sin(pi/4)
    [InlineData((int)WwiseCurveShape.SineRecip, 0.5f, 0.29289)] // 1 - cos(pi/4)
    [InlineData((int)WwiseCurveShape.Constant, 0.5f, 0.0)]
    public void Shapes_FollowTheirFormulas(int shape, float t, double expected)
        => Assert.Equal(expected, WwiseCurve.Fraction((WwiseCurveShape)shape, t), 4);

    [Theory]
    [InlineData((int)WwiseCurveShape.Exp1)]
    [InlineData((int)WwiseCurveShape.Sine)]
    [InlineData((int)WwiseCurveShape.Exp3)]
    public void EveryShape_RunsEndToEnd_AndClampsOutsideTheKnob(int shape)
    {
        var c = WwiseCurve.Plain(1f, 10f, 100f, 1000f, (WwiseCurveShape)shape);
        Assert.Equal(10f, c.Evaluate(1f), 3);
        Assert.Equal(1000f, c.Evaluate(100f), 3);
        Assert.Equal(10f, c.Evaluate(-5f), 3);
        Assert.Equal(1000f, c.Evaluate(500f), 3);
    }

    [Fact]
    public void ADescendingCurve_UsesTheSameFraction()
    {
        var c = WwiseCurve.Plain(1f, 18f, 100f, 1f, WwiseCurveShape.Exp1);
        var t = (30f - 1f) / 99f;
        Assert.Equal(18f - 17f * MathF.Pow(t, 1.41f), c.Evaluate(30f), 4);
    }

    // ------------------------------------------- the sets, as built from a bank

    [Fact]
    public void StudioEq_FrequenciesAreLog10Curves_InHz()
    {
        var eq = Assert.IsType<RsStudioEqCurves>(Set.StudioEq);
        Assert.Equal(MathF.Sqrt(30f * 400f), eq.BassFreqHz.Evaluate(215f), 0.05f);   // 30..400 Hz, linear in log10
        Assert.Equal(MathF.Sqrt(1000f * 7000f), eq.HiMidFreqHz.Evaluate(4f), 1f);    // 1..7 kHz on a 1..7 knob
        Assert.Equal(150f, eq.LoMidFreqHz.Evaluate(150f), 0.05f);
        Assert.Equal(1800f, eq.LoMidFreqHz.Evaluate(1800f), 0.5f);
        Assert.Equal(WwiseCurveShape.Sine, eq.LoMidFreqHz.Shape);
        Assert.Equal(18000f, eq.TrebleFreqHz.Evaluate(18f), 2f);
        Assert.Equal(0.5f, eq.Q.Evaluate(0f), 5);
        Assert.Equal(3f, eq.Q.Evaluate(1f), 5);
    }

    [Fact]
    public void Compression_OneKnobDrivesThreeParams_AndTimesAreInMs()
    {
        var c = Assert.IsType<RsCompressionCurves>(Set.Compression);
        Assert.Equal(-25f, c.ThresholdDb.Evaluate(50f), 3);
        Assert.Equal(1f + 24f * 0.125f, c.Ratio.Evaluate(50f), 3);           // Exp3
        Assert.Equal(10f, c.OutputGainDb.Evaluate(100f), 3);                  // dB-scaled
        Assert.Equal(WwiseCurveScaling.Db, c.OutputGainDb.Scaling);
        // The bank stores seconds; the set gives ms.
        Assert.Equal(2f, c.AttackMs.Evaluate(0f), 3);
        Assert.Equal(90f, c.AttackMs.Evaluate(100f), 3);
        Assert.Equal(275f, c.ReleaseMs.Evaluate(50f), 3);
    }

    [Fact]
    public void LoFi_HiCutUp_LowersTheCorner()
    {
        var lofi = Assert.IsType<RsLoFiCurves>(Set.LoFi);
        Assert.Equal(1050f, lofi.LoCutHz.Evaluate(50f), 1);
        Assert.True(lofi.HiCutHz.Evaluate(100f) < lofi.HiCutHz.Evaluate(0f), "Hi Cut up must LOWER the high-cut corner");
    }

    [Fact]
    public void BassEmulator_BodyInDb_ToneInHz()
    {
        var be = Assert.IsType<RsBassEmulatorCurves>(Set.BassEmulator);
        Assert.Equal(-6f, be.BodyDb.Evaluate(0f), 3);
        Assert.Equal(6f, be.BodyDb.Evaluate(100f), 3);
        Assert.Equal(MathF.Sqrt(200f * 2000f), be.ToneHz.Evaluate(50f), 0.5f);
    }

    [Fact]
    public void BitCruncher_CurvesAndTheFutzBoxBlock()
    {
        var bc = Assert.IsType<RsBitCruncherCurves>(Set.BitCruncher);
        Assert.Equal(100f, bc.FilterPct.Evaluate(0f), 3);
        Assert.Equal(0f, bc.FilterPct.Evaluate(100f), 3);
        Assert.Equal(30f, bc.RectifyPct.Evaluate(1f), 3);
        Assert.Equal(WwiseCurveShape.Sine, bc.RectifyPct.Shape);
        Assert.Equal(63f, bc.MixPct.Evaluate(63f), 3);
        // Block offsets 127 / 131 / 30 / 34: input, output, Amount, Intensity.
        Assert.Equal((7f, -9f, 50f, 10f), (bc.InputDb, bc.OutputDb, bc.DrivePct, bc.IntensityPct));
    }

    [Fact]
    public void BitCruncher_DownsampleAndBits_AreTheConvertersOwnLaws()
    {
        Assert.Equal(1, RsGameCurves.BitCruncherDownsampleFactor(0f));
        Assert.Equal(33, RsGameCurves.BitCruncherDownsampleFactor(32f));
        Assert.Equal(1336, (int)(44100.0 / RsGameCurves.BitCruncherDownsampleFactor(32f)));
        Assert.Equal(4, RsGameCurves.BitCruncherBits(4f));
        Assert.Equal(2, RsGameCurves.BitCruncherBits(1f));   // clamps to the knob
    }

    [Fact]
    public void BassOverdrive_BlendDrivesWetAndOutput_ToneIsTheGuitarDistortionsBand()
    {
        var od = Assert.IsType<RsBassOverdriveCurves>(Set.BassOverdrive);
        Assert.Equal(10f, od.DrivePct.Evaluate(0f), 3);
        Assert.Equal(80f, od.DrivePct.Evaluate(100f), 3);
        Assert.Equal(20f, od.WetPct.Evaluate(0f), 3);
        Assert.Equal(-3f, od.OutputDb.Evaluate(0f), 3);
        Assert.Equal(-9f, od.OutputDb.Evaluate(100f), 3);
        // param 51 on the Guitar Distortion, not the decoy Tone curve on the EQ before it
        Assert.Equal(-18f, od.TrebleDb.Evaluate(0f), 3);
        Assert.Equal(0f, od.TrebleDb.Evaluate(100f), 3);
    }

    [Fact]
    public void Acoustic_TheParamIdPicksTheSecondGuitarDistortion()
    {
        var ac = Assert.IsType<RsAcousticCurves>(Set.Acoustic);
        Assert.Equal(-30f, ac.MidGainDb.Evaluate(1f), 3);   // not the first block's flat 7
        Assert.Equal(-6f, ac.MidGainDb.Evaluate(100f), 3);
        Assert.Equal(100f, ac.MidFreqHz.Evaluate(100f), 0.05f);
    }

    [Fact]
    public void Choruses_ReadTheBanksFirstFlanger_AndCountTheVoices()
    {
        var chorus = Set.Choruses["Pedal_Chorus"];
        Assert.Equal((10f, 1, 0f, -1f), (chorus.DelayMs, chorus.Voices, chorus.DryLevel, chorus.FeedForward));
        Assert.Equal(WwiseCurveShape.Linear, chorus.DepthPercent.Shape);   // an S-curve runs linear
        Assert.Equal(50f, chorus.DepthPercent.Evaluate(50f), 3);
        Assert.Null(chorus.DelayKnobMs);
        Assert.Null(chorus.StereoDegrees);

        var studio = Set.Choruses["Rack_StudioChorus"];
        Assert.Equal((20f, 2, 0.5f, 1f), (studio.DelayMs, studio.Voices, studio.DryLevel, studio.FeedForward));
        Assert.Equal(40f, studio.DelayKnobMs!.Value.Evaluate(100f), 3);
        Assert.Equal(90f, studio.StereoDegrees!.Value.Evaluate(100f), 3);
        Assert.Equal(3, Set.Choruses.Count);   // the synthetic install has three of the six
    }
}
