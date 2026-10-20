using DotCharter.Services.Export.Sheet;
using DotCharter.Tests.Core;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     The sheet view engraves a 5/6-string bass charted on a guitar path at A220
///     (#161) as the bass it is: bass clef, the bass program, and the open strings
///     an octave below the guitar's (B0..C3, not B1..C4).
/// </summary>
public sealed class AlphaTexGuitarPathBassTests
{
    private static string Export(Arrangement arrangement)
    {
        var project = new ChartProject();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        return AlphaTexExporter.Export(project, arrangement, level, beats).AlphaTex;
    }

    [Fact]
    public void A_six_string_bass_on_a_guitar_path_is_engraved_as_a_bass()
    {
        var tex = Export(GuitarPathBassShapes.SixStringBassOnAltRhythm());

        Assert.Contains("\\tuning c3 g2 d2 a1 e1 b0 ", tex, StringComparison.Ordinal);
        Assert.Contains("\\instrument 33 ", tex, StringComparison.Ordinal);
        Assert.Contains("\\clef f4", tex, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guitar_Rhythm_path_is_still_engraved_as_a_guitar()
    {
        var tex = Export(GuitarPathBassShapes.GuitarRhythm(-50));

        Assert.Contains("\\tuning e4 b3 g3 d3 a2 e2 ", tex, StringComparison.Ordinal);
        Assert.Contains("\\instrument 27 ", tex, StringComparison.Ordinal);
        Assert.DoesNotContain("\\clef f4", tex, StringComparison.Ordinal);
    }
}
