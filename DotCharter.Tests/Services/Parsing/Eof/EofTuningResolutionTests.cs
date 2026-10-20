using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     <b>A chart states its tuning TWICE</b> — note LETTERS
///     (<see cref="Arrangement.Tuning" />) and per-string OFFSETS
///     (<see cref="Arrangement.TuningPitches" />) — and
///     <see cref="TuningOffsetResolution" /> is the one answer for anything that
///     WRITES one. Four exporters were put behind it; the `.eof` writer was not,
///     and it is the one that runs on EVERY save, so an arrangement carrying only
///     letters opened in Editor on Fire as E standard with every note at a
///     different sounding pitch.
/// </summary>
public sealed class EofTuningResolutionTests
{
    private static ChartProject LettersOnly(string[] letters, int strings = 6)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        project.Chart.SongName = "Letters Only";
        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120.0, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        }

        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = strings, Tuning = letters
        };
        arrangement.TuningPitches.Clear(); // the whole point: offsets were never authored
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static Arrangement ExportAndReimport(ChartProject project)
    {
        var result = EofExporter.Export(project);
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-tuning-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, result.Bytes);
        try
        {
            var importer = new EofImporter();
            var tracks = importer.GetAvailableTracks(path);
            var reimported = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(reimported.Arrangements, a => a.Name == "Lead");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(new[] { "D", "A", "D", "G", "B", "e" }, new[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, new[] { -1, -1, -1, -1, -1, -1 })]
    [InlineData(new[] { "C", "G", "C", "F", "A", "D" }, new[] { -4, -2, -2, -2, -2, -2 })]
    public void ATuningStatedOnlyAsLetters_ReachesTheEofFile(string[] letters, int[] expected) => Assert.Equal(expected, ExportAndReimport(LettersOnly(letters)).TuningPitches.Select(t => t.Step));

    /// <summary>
    ///     The conservative half — authored offsets always win, so every chart
    ///     that has them exports byte-for-byte as before even when the letters
    ///     disagree.
    /// </summary>
    [Fact]
    public void AuthoredOffsetsStillWin_EvenWhenTheLettersDisagree()
    {
        var project = LettersOnly(["E", "A", "D", "G", "B", "e"]);
        foreach (var step in new[] { -4, -2, -2, -2, -2, -2 })
            project.Arrangements[0].TuningPitches.Add(new TuningOffset(step));

        Assert.Equal([-4, -2, -2, -2, -2, -2],
            ExportAndReimport(project).TuningPitches.Select(t => t.Step));
    }

    /// <summary>
    ///     A 5-string bass offset has no agreed base, so the resolution returns
    ///     nothing rather than guessing — and the writer must fall back to the
    ///     zeros its callers already used instead of inventing a vector.
    /// </summary>
    [Fact]
    public void AnAmbiguousStringCount_FallsBackToZerosRatherThanGuessing()
    {
        var project = LettersOnly(["B", "E", "A", "D", "G"], strings: 5);

        Assert.Equal([0, 0, 0, 0, 0], ExportAndReimport(project).TuningPitches.Select(t => t.Step));
    }

    // ---- the READ half: both halves have to come back ----------------------

    /// <summary>
    ///     The mirror of the whole class, on the way IN. The EOF reader filled
    ///     only <see cref="Arrangement.TuningPitches" />, so a re-imported
    ///     arrangement kept <see cref="Arrangement" />'s DEFAULT six letters
    ///     <c>E A D G B e</c> whatever the file said — and the letters are what
    ///     the lane gutter, the tuner, the tuning chip, chord naming and the
    ///     sheet export read. Every other importer already synced both.
    ///     <para>
    ///     The letters come back at the app's CANONICAL spelling
    ///     (<see cref="TuningUtilities.GetTuningNames" />'s table, which prefers
    ///     the sharp for 1/6/8 and the flat for 3/10) — which is the point: an
    ///     EOF import now names its strings exactly the way a GP, Songsterr,
    ///     rscp, RS-XML or psarc import of the same tuning does.
    ///     </para>
    /// </summary>
    [Theory]
    // authored letters                                    → what comes back
    [InlineData(new[] { "D", "A", "D", "G", "B", "e" }, new[] { "D", "A", "D", "G", "B", "E" })]
    [InlineData(new[] { "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, new[] { "Eb", "G#", "C#", "F#", "Bb", "Eb" })]
    [InlineData(new[] { "C", "G", "C", "F", "A", "D" }, new[] { "C", "G", "C", "F", "A", "D" })]
    public void TheNoteLETTERS_SurviveTheRoundTripToo(string[] letters, string[] expected)
    {
        var reimported = ExportAndReimport(LettersOnly(letters));

        Assert.Equal(expected, reimported.Tuning);
    }

    /// <summary>On a 4-string bass the stale six-entry default also NAMED two
    /// strings that do not exist.</summary>
    [Fact]
    public void AFourStringBass_ComesBackWithFourLettersNotSix()
    {
        var project = LettersOnly(["E", "A", "D", "G"], strings: 4);
        project.Arrangements[0].ArrangementType = ArrangementType.Bass;

        var reimported = ExportAndReimport(project);

        Assert.Equal(4, reimported.StringCount);
        Assert.Equal(["E", "A", "D", "G"], reimported.Tuning);
    }
}
