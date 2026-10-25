using System.Globalization;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A chart's phrases can live on <see cref="EventPoint.Phrase" /> markers (every phrase placed in
///     the editor or imported from EOF), in <see cref="Arrangement.PhraseIterations" /> (RS-XML and
///     .rscp imports), or in both. The SNG of a package and the RS-XML of the same chart read them
///     through <see cref="PhraseMarkers.Iterations" />, so they ship the same phrase iterations; the
///     SNG kept a marker only when its name was already in <see cref="Arrangement.Phrases" />, and
///     nothing in the editor puts it there.
/// </summary>
public sealed class PhraseStoreExportParityTests
{
    private const int SongLengthMs = 32_000;

    private static List<BeatMarker> CreateBeats()
    {
        var beats = new List<BeatMarker>(64);
        for (var i = 0; i < 64; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static Arrangement Chart(IList<BeatMarker> beats)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var ms in new[] { 1000, 2500, 4000, 5500, 7000, 8500 })
            level.Notes.Add(new GuitarNote { Position = Pos(beats, ms), StringIndex = 0, Fret = 3 });
        level.Anchors.Add(new FretAnchor(Pos(beats, 1000), 1));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static void Marker(Arrangement arrangement, IList<BeatMarker> beats, double ms, string phrase) =>
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, ms), Phrase = phrase });

    /// <summary>A chart placed in the editor: markers only, and no name in <c>Phrases</c>.</summary>
    private static Arrangement MarkersOnly(IList<BeatMarker> beats)
    {
        var arrangement = Chart(beats);
        Marker(arrangement, beats, 1000, "verse");
        Marker(arrangement, beats, 4000, "chorus");
        Marker(arrangement, beats, 7000, "verse");
        return arrangement;
    }

    /// <summary>An imported chart the user has since added a phrase to in the editor.</summary>
    private static Arrangement Mixed(IList<BeatMarker> beats)
    {
        var arrangement = Chart(beats);
        arrangement.Phrases.AddRange(["COUNT", "verse", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 1000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 7000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 10_000), 2));
        Marker(arrangement, beats, 4000, "bridge");
        return arrangement;
    }

    private static List<(string Name, int Ms)> FromSng(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, SongLengthMs).Sng;
        return sng.PhraseIterations
            .Select(it => (sng.Phrases[it.PhraseId].Name, (int)Math.Round(it.StartTime * 1000)))
            .ToList();
    }

    private static List<(string Name, int Ms)> FromXml(Arrangement arrangement, List<BeatMarker> beats)
    {
        var project = new ChartProject { Beats = [.. beats], Arrangements = [arrangement] };
        var song = new RsXmlSongWriter().Write(project, SongLengthMs).Root!;
        var names = song.Element("phrases")!.Elements("phrase").Select(p => (string)p.Attribute("name")!).ToList();
        return song.Element("phraseIterations")!.Elements("phraseIteration")
            .Select(it => (names[(int)it.Attribute("phraseId")!],
                (int)Math.Round(double.Parse((string)it.Attribute("time")!, CultureInfo.InvariantCulture) * 1000)))
            .ToList();
    }

    /// <summary>THE data loss: every hand-placed phrase left the package, which shipped the
    ///     COUNT / p0 / END stub instead.</summary>
    [Fact]
    public void AMarkersOnlyChart_ShipsItsPhrasesInTheSng()
    {
        var beats = CreateBeats();

        var iterations = FromSng(MarkersOnly(beats), beats);

        Assert.Equal(["COUNT", "verse", "chorus", "verse", "END"], iterations.Select(i => i.Name).ToArray());
        Assert.Equal([1000, 4000, 7000], iterations.Skip(1).Take(3).Select(i => i.Ms).ToArray());
    }

    /// <summary>With explicit iterations, a marker added in the editor joins them instead of
    ///     being ignored.</summary>
    [Fact]
    public void AMixedChart_ShipsTheEditorsPhraseBesideTheImportedOnes()
    {
        var beats = CreateBeats();

        var iterations = FromSng(Mixed(beats), beats);

        Assert.Equal(["COUNT", "verse", "bridge", "verse", "END"], iterations.Select(i => i.Name).ToArray());
        Assert.Equal(4000, iterations.Single(i => i.Name == "bridge").Ms);
    }

    [Fact]
    public void TheXmlAndTheSng_ShipTheSamePhraseIterations()
    {
        var beats = CreateBeats();
        foreach (var build in new Func<IList<BeatMarker>, Arrangement>[] { MarkersOnly, Mixed })
            Assert.Equal(FromXml(build(beats), beats), FromSng(build(beats), beats));
    }
}
