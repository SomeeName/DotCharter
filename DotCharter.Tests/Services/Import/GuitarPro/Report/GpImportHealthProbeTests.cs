using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Import.GuitarPro.Report;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report;

/// <summary>
///     The engine of <c>tools/tab-corpus</c> on SYNTHETIC Guitar Pro files only — the
///     owner's corpus is never needed, read or quoted by a test.
/// </summary>
public sealed class GpImportHealthProbeTests
{
    /// <summary>
    ///     <c>|: X Y :| Z</c> in a Lead track and <c>|: X Y :| W</c> in a Rhythm track: five
    ///     measures from three written bars. Guitar Pro's repeat says measures 0/2 and 1/3 are
    ///     one bar each — the detector must agree — and the two tracks share four measures.
    /// </summary>
    [Fact]
    public void RepeatsAndSharedRuns_AreMeasuredAgainstGuitarProsOwnRepeats()
    {
        var path = LegacyGpFixtureBuilder.WriteMultiTrackFile("5.10",
        [
            ("Lead", Bars(1, 3, 5)),
            ("Rhythm", Bars(1, 3, 7))
        ]);
        try
        {
            var report = GpImportHealthProbe.Run(path);

            Assert.True(report.Ok, report.ErrorClass + " " + report.ErrorMessage);
            Assert.Equal(5, report.Measures);
            Assert.Equal(3, report.SourceBars);
            Assert.Equal(2, report.RepeatPlayedMeasures);
            Assert.Equal(1, report.CrossGroups);
            Assert.Equal(2, report.Arrangements.Count);

            var lead = report.Arrangements[0];
            Assert.Equal(ArrangementType.Lead, lead.Type);
            Assert.Equal(5, lead.ContentMeasures);
            Assert.Equal(2, lead.GpRepeatPairs);
            Assert.Equal(2, lead.GpRepeatPairsEqual);
            Assert.Equal(2, lead.GpRepeatPairsGrouped);
            Assert.Equal(4, lead.RepeatedMeasures);
            Assert.Equal(0, lead.RepeatedBeyondGp);
            Assert.Equal(4, lead.SharedMeasures);
            Assert.Equal(4, lead.SharedSamePosition);
            Assert.Equal(lead.LinkClass, report.Arrangements[1].LinkClass);
            Assert.Equal(0, lead.BadString + lead.FretOver24 + lead.BelowCapo + lead.OffGrid);

            Assert.Equal("ok", lead.RoundTrip);
            Assert.Equal(5, lead.RoundTripMeasures);
            Assert.Equal(lead.Sounds, lead.RoundTripSoundsAfter);
            Assert.Empty(lead.SourceTechniques); // the census reads gpif only
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A written-out repeat (no repeat sign) is found too, and counted as beyond GP.</summary>
    [Fact]
    public void AWrittenOutRepeat_IsCountedBeyondGuitarPro()
    {
        var measures = new[] { Bar(1), Bar(3), Bar(1), Bar(3), Bar(5) };
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures, trackName: "Lead");
        try
        {
            var lead = Assert.Single(GpImportHealthProbe.Run(path, new GpProbeOptions(RoundTrip: false)).Arrangements);

            Assert.Equal(0, lead.GpRepeatPairs);
            Assert.Equal(4, lead.RepeatedMeasures);
            Assert.Equal(4, lead.RepeatedBeyondGp);
            Assert.Equal("skipped", lead.RoundTrip);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatDoesNotImport_IsAFailure_NotACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-probe-broken-{Guid.NewGuid():N}.gp");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        try
        {
            var report = GpImportHealthProbe.Run(path);

            Assert.False(report.Ok);
            Assert.StartsWith("import:", report.ErrorClass, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The technique census reads the gpif itself and agrees with the importer
    /// where nothing is dropped.</summary>
    [Fact]
    public void TheCensus_CountsAuthoredTechniques_InAGpif()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var bent = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 2, Fret = 7, Sustain = new FractionalPosition(1) };
        bent.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1));
        level.Notes.Add(bent);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5, Mute = MuteType.Palm });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 1, Fret = 5 });
        var beats = Enumerable.Range(0, 8).Select(i => new BeatMarker
        {
            Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
        }).ToList();
        var path = Path.Combine(Path.GetTempPath(), $"dc-probe-census-{Guid.NewGuid():N}.gp");
        File.WriteAllBytes(path, GpExporter.ExportToBytes(arrangement, level, beats));
        try
        {
            var lead = Assert.Single(GpImportHealthProbe.Run(path).Arrangements);

            Assert.Equal(3, lead.SourceTechniques["notes"]);
            Assert.Equal(1, lead.SourceTechniques["bend"]);
            Assert.Equal(1, lead.SourceTechniques["palm-mute"]);
            Assert.Equal(lead.SourceTechniques["bend"], lead.ImportedTechniques["bend"]);
            Assert.Equal(lead.SourceTechniques["palm-mute"], lead.ImportedTechniques["palm-mute"]);
            Assert.Equal(3, lead.ImportedTechniques["notes"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A slide-in becomes an origin sound that slides into the authored note — in the
    ///     chart an ordinary slide — so the census takes the importer's own tally for it and
    ///     does not count the origin as an authored note or slide (without the tally the
    ///     corpus census counts every slide-in as dropped).
    /// </summary>
    [Fact]
    public void TheCensus_CountsMaterializedSlideIns_AsSlideIns()
    {
        static string Note(int id, int str, int fret, int slide = 0) => $"""
            <Note id="{id}"><Properties>
              <Property name="String"><String>{str}</String></Property>
              <Property name="Fret"><Fret>{fret}</Fret></Property>
              {(slide == 0 ? "" : $"<Property name=\"Slide\"><Flags>{slide}</Flags></Property>")}
            </Properties></Note>
            """;
        var gpif = $"""
            <GPIF>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1 2 3</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1 2</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>3</Notes></Beat>
                <Beat id="3"><Rhythm ref="0" /><Notes>4</Notes></Beat>
              </Beats>
              <Notes>{Note(0, 1, 7, 16)}{Note(1, 1, 5, 32)}{Note(2, 2, 7, 32)}{Note(3, 0, 3)}{Note(4, 0, 5)}</Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"dc-probe-slidein-{Guid.NewGuid():N}.gp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open());
            writer.Write(gpif);
        }

        try
        {
            var lead = Assert.Single(GpImportHealthProbe.Run(path, new GpProbeOptions(RoundTrip: false)).Arrangements);

            Assert.Equal(3, lead.SourceTechniques["slide-in"]);
            Assert.Equal(3, lead.ImportedTechniques["slide-in"]);
            Assert.Equal(lead.SourceTechniques["notes"], lead.ImportedTechniques["notes"]);
            Assert.Equal(lead.SourceTechniques["slide"], lead.ImportedTechniques["slide"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The gpif path records which written bar each unfolded bar plays.</summary>
    [Fact]
    public void TheGpifMetadata_RecordsTheWrittenBarOfEveryUnfoldedBar()
    {
        var doc = XDocument.Parse("""
            <GPIF><MasterBars>
              <MasterBar><Time>4/4</Time><Repeat start="true" end="false"/></MasterBar>
              <MasterBar><Time>4/4</Time><Repeat start="false" end="true" count="2"/></MasterBar>
              <MasterBar><Time>3/4</Time></MasterBar>
            </MasterBars></GPIF>
            """);
        var meta = new GpImportMetadata();

        GpMetadataParser.Populate(doc, meta);

        Assert.Equal([0, 1, 0, 1, 2], meta.Bars.Select(b => b.SourceBarIndex));
        Assert.Equal([1, 1, 2, 2, 1], meta.Bars.Select(b => b.RepeatPass));
    }

    /// <summary>|: a b :| c — the first two repeat twice.</summary>
    private static FxMeasure[] Bars(int a, int b, int c)
    {
        var first = Bar(a);
        first.RepeatStart = true;
        var second = Bar(b);
        second.RepeatTotalPasses = 2;
        return [first, second, Bar(c)];
    }

    /// <summary>Four quarter notes climbing from <paramref name="fret" />.</summary>
    private static FxMeasure Bar(int fret) => new FxMeasure().AddVoice(
        FxBeat.Note(0, 1, fret), FxBeat.Note(0, 2, fret + 1), FxBeat.Note(0, 1, fret + 2), FxBeat.Note(0, 3, fret));
}
