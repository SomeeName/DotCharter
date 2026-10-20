using System.Globalization;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
/// <c>SongAverageTempo</c> is what the game's song list shows, and it must be
/// DERIVED from the beat grid like every other chart-wide value — not read off
/// <see cref="ChartProject.Bpm"/>, which is the nominal/initial tempo. Reading
/// the nominal tempo made the manifest disagree with the <c>&lt;averageTempo&gt;</c>
/// in the RS-XML built from the very same chart on every tempo-mapped chart.
/// </summary>
public sealed class RsPackageAverageTempoTests
{
    /// <summary>A grid whose real average is <paramref name="bpm"/>.</summary>
    private static List<BeatMarker> Grid(double bpm, int count)
    {
        var msPerBeat = 60_000.0 / bpm;
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * msPerBeat,
                Bpm = bpm,
                IsMeasure = i % 4 == 0,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    private static ChartProject BuildProject(double nominalBpm, double actualBpm, int beatCount = 65)
    {
        var project = new ChartProject
        {
            Bpm = nominalBpm,
            Chart = { SongName = "Tempo Mapped", Artist = "DotCharter" }
        };
        foreach (var beat in Grid(actualBpm, beatCount)) project.Beats.Add(beat);

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arr);
        return project;
    }

    private static RocksmithBuildOptions Options() => new()
    {
        OutputPath = "unused_p.psarc",
        AudioPath = "unused.wem"
    };

    [Fact]
    public void TheManifestTempo_IsDerivedFromTheBeatGrid_NotFromChartProjectBpm()
    {
        // Nominal 120, but the grid actually runs at 180 — a tempo map that
        // starts elsewhere, or an import that never updated the nominal value.
        var project = BuildProject(nominalBpm: 120, actualBpm: 180);

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal(180f, metadata.AverageTempo, 0.01f);
        Assert.NotEqual(120f, metadata.AverageTempo);
    }

    [Fact]
    public void TheManifestTempo_AgreesWithTheRsXmlBuiltFromTheSameChart()
    {
        var project = BuildProject(nominalBpm: 120, actualBpm: 168);

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        var song = new RsXmlSongWriter().Write(project).Root!;
        var xmlTempo = double.Parse(song.Element("averageTempo")!.Value, CultureInfo.InvariantCulture);

        // The two artifacts are built from ONE chart; the song list and the XML
        // may not describe different songs.
        Assert.Equal(xmlTempo, metadata.AverageTempo, 0.01);
    }

    [Fact]
    public void AChartWithNoUsableBeatGrid_StillShipsALegalTempo()
    {
        // < 2 beats: RsXmlSongWriter.AverageTempo falls back to 120, and the
        // manifest must not ship a 0 or a negative that the game divides by.
        var project = BuildProject(nominalBpm: 0, actualBpm: 120, beatCount: 1);

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal(120f, metadata.AverageTempo, 0.01f);
    }
}
