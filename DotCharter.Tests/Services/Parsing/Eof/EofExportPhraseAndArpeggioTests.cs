using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     Two things the <c>.eof</c> save companion — which runs on EVERY Ctrl+S —
///     dropped for charts that did not come from EOF itself.
///     <para>
///         <b>Phrases.</b> A chart states its RS phrases in TWO stores:
///         <c>EventPoint.Phrase</c> (what the timeline editor authors, what the
///         rscp round-trips) and <c>Arrangement.Phrases</c> +
///         <c>PhraseIterations</c> (what <c>RsXmlSongReader</c> fills, and it
///         fills ONLY those). <c>EofExportSession.BuildTextEvents</c> read the
///         first alone, so an RS-XML import exported an <c>.eof</c> with no
///         phrase structure at all. <c>RsXmlSongWriter.MergeEditorPhraseMarkers</c>
///         is the same merge in the other direction.
///     </para>
///     <para>
///         <b>Arpeggios.</b> Two flags spell ARPEGGIO — <c>ChordTemplate.Arpeggio</c>
///         and <c>ForceArpeggioInRs</c> — and an RS-XML/psarc import sets only the
///         second (from the <c>-arp</c> displayName). The hand-shape section
///         writer read the first alone, so an imported arpeggio shipped as an
///         ordinary hand shape.
///     </para>
/// </summary>
public sealed class EofExportPhraseAndArpeggioTests
{
    private static ChartProject Project()
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        project.Chart.SongName = "Imported";
        project.Chart.Artist = "Reader";
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0, Bpm = 120.0, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
            });
        }

        return project;
    }

    /// <summary>An arrangement shaped exactly as <c>RsXmlSongReader</c> leaves
    /// one: phrases in the explicit stores, and NOTHING in the event points.</summary>
    private static Arrangement AsReadFromRsXml()
    {
        var arrangement = new Arrangement
        { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.Phrases.AddRange(["COUNT", "riff", "solo", "END"]);
        arrangement.PhraseInfos["solo"] = new PhraseInfo(0, true);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 2));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(28), 3));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        { Position = new FractionalPosition(4), Sustain = new FractionalPosition(1), StringIndex = 2, Fret = 5 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static List<Arrangement> RoundTrip(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-phrase-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        try
        {
            var importer = new EofImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PhrasesStatedOnlyInTheExplicitStores_StillReachTheEofFile()
    {
        var project = Project();
        project.Arrangements.Add(AsReadFromRsXml());

        var imported = Assert.Single(RoundTrip(project));

        Assert.Equal(["COUNT", "END", "riff", "solo"], imported.Phrases.Order().ToList());
        // Placed, not merely named: each one is an event point at its iteration.
        var placed = imported.EventPoints
            .Where(ep => !string.IsNullOrEmpty(ep.Phrase))
            .ToDictionary(ep => ep.Phrase, ep => ep.Position);
        Assert.Equal(new FractionalPosition(0), placed["COUNT"]);
        Assert.Equal(new FractionalPosition(4), placed["riff"]);
        Assert.Equal(new FractionalPosition(12), placed["solo"]);
        Assert.Equal(new FractionalPosition(28), placed["END"]);
        // The solo bit rides the same event.
        Assert.True(imported.PhraseInfos.TryGetValue("solo", out var info) && info.Solo);
    }

    /// <summary>The merge is ADDITIVE: a chart whose two stores agree — an rscp
    /// round trip, a psarc import, both of which fill both — must not gain a
    /// duplicate marker.</summary>
    [Fact]
    public void AChartThatStatesItsPhrasesBothWays_GainsNoDuplicateMarkers()
    {
        var project = Project();
        var arrangement = AsReadFromRsXml();
        foreach (var iteration in arrangement.PhraseIterations)
        {
            arrangement.EventPoints.Add(new EventPoint
            { Position = iteration.Position, Phrase = arrangement.Phrases[iteration.PhraseId] });
        }

        project.Arrangements.Add(arrangement);

        var imported = Assert.Single(RoundTrip(project));

        var markers = imported.EventPoints.Where(ep => !string.IsNullOrEmpty(ep.Phrase)).ToList();
        Assert.Equal(4, markers.Count);
        Assert.Equal(4, markers.Select(m => m.Position).Distinct().Count());
    }

    [Fact]
    public void AForceArpeggioInRsShape_ShipsAsAnArpeggioNotAPlainHandShape()
    {
        var project = Project();
        var arrangement = AsReadFromRsXml();

        // Exactly what RsXmlSongReader produces for `displayName="Am-arp"`:
        // ForceArpeggioInRs set, Arpeggio untouched.
        var template = new ChordTemplate
        { Id = 0, Name = "Am", ForceArpeggioInRs = true, Frets = { [1] = 0, [2] = 2, [3] = 2 } };
        Assert.False(template.Arpeggio);
        arrangement.ChordTemplates.Add(template);

        var level = arrangement.Levels[0];
        var chord = new Chord { Position = new FractionalPosition(8), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 2 });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10), TemplateId = 0
        });
        project.Arrangements.Add(arrangement);

        var imported = Assert.Single(RoundTrip(project));
        var shape = Assert.Single(imported.Levels[0].HandShapes);
        var shapeTemplate = imported.ChordTemplates.Single(t => t.Id == shape.TemplateId);

        Assert.True(shapeTemplate.IsArpeggio,
            "the hand shape came back as a plain shape — the arpeggio flag never reached the .eof");
    }
}
