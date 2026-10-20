using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     The engraved open-string pitches must agree with the letters every other
///     surface prints for the same arrangement.
///     <para>
///         An arrangement states its tuning as per-string OFFSETS against a
///         STANDARD row, and there are two copies of that row: the exporter's own
///         MIDI table and <see cref="TuningUtilities.GetTuningNames" />, which is
///         what the importers measure against and what the tuning chips, the
///         browser column and the PDF's tuning legend all print. When the two
///         disagree the sheet engraves a different instrument from the one the
///         page's own legend names — and the exporter had no 8-string row at all,
///         so both of an 8-string's extra low strings were given the top E4.
///     </para>
/// </summary>
public class AlphaTexTuningTests
{
    private static string Header(int stringCount, int[]? offsets = null)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = stringCount };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        if (offsets != null)
        {
            foreach (var o in offsets)
                arr.TuningPitches.Add(new TuningOffset(o));
        }

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        var start = tex.IndexOf("\\tuning", StringComparison.Ordinal);
        Assert.True(start >= 0, "the export must carry a tuning line");
        var end = tex.IndexOf("\\instrument", start, StringComparison.Ordinal);
        Assert.True(end > start, "the tuning line is followed by the instrument program");
        return tex[start..end].Trim();
    }

    /// <summary>alphaTex lists strings HIGHEST first, scientific octaves.</summary>
    [Theory]
    [InlineData(4, "\\tuning g2 d2 a1 e1")]
    [InlineData(6, "\\tuning e4 b3 g3 d3 a2 e2")]
    [InlineData(7, "\\tuning e4 b3 g3 d3 a2 e2 b1")]
    [InlineData(8, "\\tuning e4 b3 g3 d3 a2 e2 b1 f#1")]
    public void StandardTuning_MatchesTheStringCountsInstrument(int strings, string expected) => Assert.Equal(expected, Header(strings));

    /// <summary>
    ///     THE regression: an 8-string is F#-B-E-A-D-G-B-E. Before the fix the
    ///     six-string row answered and the index clamp repeated its top string,
    ///     so the two lowest strings both engraved as e4.
    /// </summary>
    [Fact]
    public void AnEightString_DoesNotRepeatItsTopStringOnTheLowOnes()
    {
        var header = Header(8);
        Assert.Equal(1, header.Split(' ').Count(t => t == "e4"));
        Assert.Contains("f#1", header, StringComparison.Ordinal);
        Assert.Contains("b1", header, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The cross-assert that keeps the two standard rows from drifting again:
    ///     the pitch CLASSES the exporter engraves must be the ones
    ///     <see cref="TuningUtilities.GetTuningNames" /> names, for every string
    ///     count whose base is unambiguous. 5 is excluded on purpose — the
    ///     exporter reads a 5-string as a BASS (B0 E1 A1 D2 G2, which is what its
    ///     emitted instrument program says too) while the naming table reads it
    ///     off the six-string row; that ambiguity is documented in
    ///     <see cref="TuningOffsetResolution" /> and has to be resolved at the
    ///     importers, not here.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EngravedPitchClasses_AgreeWithTheNamesEverySurfacePrints(int strings)
    {
        // Low → high, one offset per string, deliberately not all zero so a table
        // that ignored the offsets could not pass by accident.
        var offsets = new int[strings];
        for (var i = 0; i < strings; i++) offsets[i] = i % 3 - 1;

        // alphaTex is highest-string-first; the names are lowest-first.
        var engraved = Header(strings, offsets)["\\tuning".Length..].Trim().Split(' ').Reverse().ToArray();
        var named = TuningUtilities.GetTuningNames(strings, offsets);

        Assert.Equal(strings, engraved.Length);
        for (var i = 0; i < strings; i++)
            Assert.Equal(PitchClass(named[i]), PitchClass(engraved[i]));
    }

    /// <summary>Pitch class 0-11 from either spelling (the names table uses flats,
    /// alphaTex sharps, and the alphaTex token carries an octave digit).</summary>
    private static int PitchClass(string note)
    {
        var letter = note.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return letter.ToUpperInvariant() switch
        {
            "C" => 0,
            "C#" or "DB" => 1,
            "D" => 2,
            "D#" or "EB" => 3,
            "E" => 4,
            "F" => 5,
            "F#" or "GB" => 6,
            "G" => 7,
            "G#" or "AB" => 8,
            "A" => 9,
            "A#" or "BB" => 10,
            "B" => 11,
            _ => throw new Xunit.Sdk.XunitException($"unreadable note '{note}'")
        };
    }
}
