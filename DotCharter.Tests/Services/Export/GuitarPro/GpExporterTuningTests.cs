using DotCharter.Services.Export.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

public sealed class GpExporterTuningTests
{
    [Theory]
    [InlineData(ArrangementType.Lead, 6, -1200, new[] { -5, -5, -5, -5, -4, -4 }, new double[] { 48, 43, 38, 33, 28, 23 })]
    [InlineData(ArrangementType.Lead, 6, -1250, new[] { -5, -5, -5, -5, -4, -4 }, new double[] { 48, 43, 38, 33, 28, 23 })]
    [InlineData(ArrangementType.Bass, 6, 0, new[] { -5, -5, -5, -5, -4, -4 }, new double[] { 48, 43, 38, 33, 28, 23 })]
    [InlineData(ArrangementType.Bass, 4, 0, new[] { 0, 0, 0, 0 }, new double[] { 43, 38, 33, 28 })]
    [InlineData(ArrangementType.Lead, 4, 0, new[] { 0, 0, 0, 0 }, new double[] { 55, 50, 45, 40 })]
    [InlineData(ArrangementType.Lead, 9, 0, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }, new double[] { 64, 59, 55, 50, 45, 40, 35, 30, 25 })]
    public void Written_open_pitches_include_the_path_and_reference_octave(
        ArrangementType type, int strings, double cents, int[] offsets, double[] expected)
    {
        var arrangement = new Arrangement
        {
            ArrangementType = type, StringCount = strings, CentOffset = cents, Capo = 3
        };
        foreach (var step in offsets) arrangement.TuningPitches.Add(new TuningOffset(step));

        Assert.Equal(expected, GpExporter.BuildTuningHighToLow(arrangement));
    }

    [Fact]
    public void Letter_only_Drop_D_writes_D2_as_the_low_string()
    {
        var arrangement = new Arrangement { Tuning = ["D", "A", "D", "G", "B", "E"] };

        Assert.Equal(new double[] { 64, 59, 55, 50, 45, 38 }, GpExporter.BuildTuningHighToLow(arrangement));
    }
}
