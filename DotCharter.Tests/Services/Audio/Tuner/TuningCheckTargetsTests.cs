using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class TuningCheckTargetsTests
{
    private static Arrangement Guitar(params int[] steps)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        foreach (var step in steps.Length > 0 ? steps : new int[6])
            arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    [Fact]
    public void StandardGuitar_E2ToE4()
    {
        var targets = TuningCheckTargets.Build(Guitar());

        Assert.Equal(6, targets.Length);
        Assert.Equal(new[] { 40, 45, 50, 55, 59, 64 }, targets.Select(t => t.Midi));
        Assert.Equal(new[] { "E2", "A2", "D3", "G3", "B3", "E4" }, targets.Select(t => t.NoteName));
        Assert.Equal(82.41, targets[0].FrequencyHz, 2);
        Assert.Equal(329.63, targets[5].FrequencyHz, 2);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, targets.Select(t => t.StringIndex));
    }

    [Fact]
    public void DropD_LowersOnlyTheLowString()
    {
        var targets = TuningCheckTargets.Build(Guitar(-2, 0, 0, 0, 0, 0));

        Assert.Equal(38, targets[0].Midi);
        Assert.Equal("D2", targets[0].NoteName);
        Assert.Equal(73.42, targets[0].FrequencyHz, 2);
        Assert.Equal(45, targets[1].Midi);
    }

    [Fact]
    public void MissingTuningPitches_FallsBackToTuningType()
    {
        var standard = new Arrangement { StringCount = 6, TuningType = "E_STANDARD" };
        Assert.Equal(new[] { 40, 45, 50, 55, 59, 64 },
            TuningCheckTargets.Build(standard).Select(t => t.Midi));

        var dropD = new Arrangement { StringCount = 6, TuningType = "E_DROP_D" };
        var targets = TuningCheckTargets.Build(dropD);
        Assert.Equal(38, targets[0].Midi);
        Assert.Equal(45, targets[1].Midi);
    }

    [Fact]
    public void Bass_AnchorsOneOctaveDown()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        for (var i = 0; i < 4; i++) bass.TuningPitches.Add(new TuningOffset(0));

        var targets = TuningCheckTargets.Build(bass);
        Assert.Equal(new[] { 28, 33, 38, 43 }, targets.Select(t => t.Midi));
        Assert.Equal(41.20, targets[0].FrequencyHz, 2);
    }

    [Fact]
    public void SevenString_ExtendsDown_FromALowB1()
    {
        // A 7-string's extra string is a LOW B1 below the low E — the convention
        // every tab format, the tuning names and the RS7 layout use, and the one
        // StandardOpenStrings states for every consumer (#742). This test used to
        // pin the opposite (E2 … E4 then a high A4).
        var seven = new Arrangement { StringCount = 7 };
        for (var i = 0; i < 7; i++) seven.TuningPitches.Add(new TuningOffset(0));

        var targets = TuningCheckTargets.Build(seven);
        Assert.Equal(7, targets.Length);
        Assert.Equal(new[] { 35, 40, 45, 50, 55, 59, 64 }, targets.Select(t => t.Midi));
        Assert.Equal("B1", targets[0].NoteName);
        Assert.Equal(61.74, targets[0].FrequencyHz, 2);

        // Drop A moves only the low string, two half steps down.
        seven.TuningPitches[0].Step = -2;
        var dropA = TuningCheckTargets.Build(seven);
        Assert.Equal(33, dropA[0].Midi);
        Assert.Equal("A1", dropA[0].NoteName);
        Assert.Equal(40, dropA[1].Midi);
    }

    [Fact]
    public void CentOffset_ShiftsFrequenciesButNotNames()
    {
        var offset = Guitar();
        offset.CentOffset = -50;

        var targets = TuningCheckTargets.Build(offset);
        Assert.Equal("E2", targets[0].NoteName);
        Assert.Equal(40, targets[0].Midi);
        Assert.Equal(82.4069 * Math.Pow(2, -50 / 1200.0), targets[0].FrequencyHz, 3);
    }

    [Fact]
    public void Capo_DefaultFlow_TunesOpenStrings()
    {
        var capoed = Guitar();
        capoed.Capo = 2;

        // Rocksmith flow: tune open, THEN place the capo — targets unshifted.
        var targets = TuningCheckTargets.Build(capoed);
        Assert.Equal(40, targets[0].Midi);
        Assert.Equal("E2", targets[0].NoteName);
    }

    [Fact]
    public void Capo_VerifyWithCapoOn_RaisesEveryTarget()
    {
        var capoed = Guitar();
        capoed.Capo = 2;

        var targets = TuningCheckTargets.Build(capoed, TuningCheckCapoMode.VerifyWithCapoOn);
        Assert.Equal(new[] { 42, 47, 52, 57, 61, 66 }, targets.Select(t => t.Midi));
        Assert.Equal("F#2", targets[0].NoteName);
        Assert.Equal(92.50, targets[0].FrequencyHz, 2);
    }

    [Fact]
    public void CapoAndCentOffset_Compose()
    {
        var arrangement = Guitar();
        arrangement.Capo = 3;
        arrangement.CentOffset = 20;

        var targets = TuningCheckTargets.Build(arrangement, TuningCheckCapoMode.VerifyWithCapoOn);
        Assert.Equal(43, targets[0].Midi); // G2
        var expected = 440.0 * Math.Pow(2, (43 - 69) / 12.0) * Math.Pow(2, 20 / 1200.0);
        Assert.Equal(expected, targets[0].FrequencyHz, 6);
    }
}
