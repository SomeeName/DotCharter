using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Tests for the pure (Skia-free) parts of the PDF exporter: page geometry and
/// the title-block / tuning-legend text. The notation raster itself goes through
/// AlphaSkia (native) and cannot be produced deterministically headless, so it
/// is verified manually rather than gated — see the change notes.
/// </summary>
[Collection("AlphaTab")]
public class SheetPdfExporterTests
{
    private static readonly FakeLocalization English = FakeLocalization.English();

    private static Arrangement Arr() => new()
    {
        Name = "Rhythm",
        StringCount = 6,
        Tuning = ["E", "A", "D", "G", "B", "e"]
    };

    [Fact]
    public void PageSizePt_A4AndLetter()
    {
        var a4 = SheetPdfExporter.PageSizePt(SheetPageFormat.A4);
        var letter = SheetPdfExporter.PageSizePt(SheetPageFormat.Letter);

        Assert.Equal(595.276f, a4.Width, 3);
        Assert.Equal(841.89f, a4.Height, 3);
        Assert.Equal(612f, letter.Width);
        Assert.Equal(792f, letter.Height);
    }

    [Fact]
    public void BuildTuningLegend_LowToHigh() => Assert.Equal("Tuning: E A D G B e", SheetPdfExporter.BuildTuningLegend(Arr(), English));

    [Fact]
    public void BuildHeaderLines_UsesSongArtistArrangement()
    {
        var project = new ChartProject();
        project.Chart.SongName = "Song X";
        project.Chart.Artist = "Artist Y";
        var arr = Arr();
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var (title, subtitle, info) = SheetPdfExporter.BuildHeaderLines(project, arr, level, English);

        Assert.Equal("Song X", title);
        Assert.Equal("Artist Y", subtitle);
        Assert.Contains("Rhythm", info);
        Assert.Contains("Tuning: E A D G B e", info);
    }

    [Fact]
    public void BuildHeaderLines_FallsBackToUntitled_AndOmitsCapoDifficulty()
    {
        var project = new ChartProject();
        project.Chart.SongName = "   "; // blank -> fallback
        var arr = Arr();                // capo 0, single level
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var (title, _, info) = SheetPdfExporter.BuildHeaderLines(project, arr, level, English);

        Assert.Equal("Untitled", title);
        Assert.DoesNotContain("Capo", info);
        Assert.DoesNotContain("Difficulty", info);
    }

    [Fact]
    public void BuildHeaderLines_ShowsCapoAndDifficulty_WhenPresent()
    {
        var project = new ChartProject();
        project.Chart.SongName = "S";
        var arr = Arr();
        arr.Capo = 3;
        arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        var level = new ChartLevel { DifficultyIndex = 2 };
        arr.Levels.Add(level);

        var (_, _, info) = SheetPdfExporter.BuildHeaderLines(project, arr, level, English);

        Assert.Contains("Capo 3", info);
        Assert.Contains("Difficulty 2", info);
    }

    /// <summary>#286: the header used to be English in every language. Each part comes
    /// from the resx now; the keys-only fake shows which key and argument made it.</summary>
    [Fact]
    public void BuildHeaderLines_TakesEveryWordFromTheResx()
    {
        var keys = FakeLocalization.KeysOnly();
        var project = new ChartProject();
        project.Chart.SongName = " ";
        var arr = Arr();
        arr.Capo = 3;
        arr.Levels.Add(new ChartLevel());
        var level = new ChartLevel { DifficultyIndex = 2 };
        arr.Levels.Add(level);

        var (title, _, info) = SheetPdfExporter.BuildHeaderLines(project, arr, level, keys);

        Assert.Equal("Project_Untitled", title);
        Assert.Contains("SheetPdf_Header_Tuning(E A D G B e)", info);
        Assert.Contains("SheetPdf_Header_Capo(3)", info);
        Assert.Contains("SheetPdf_Header_Difficulty(2)", info);
        Assert.DoesNotContain("Tuning:", info);
    }
}
