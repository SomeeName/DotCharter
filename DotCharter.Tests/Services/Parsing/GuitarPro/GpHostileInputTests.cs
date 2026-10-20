using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Hostile-input coverage for the MODERN gpif branch. Every fixture is a real
///     <c>.gp</c> — a zip with a <c>Content/score.gpif</c> entry — driven through
///     the public <see cref="GpImporter" /> entry point, because that is the file a
///     user is handed and the branch that owns these fields. The three GP hostile
///     tests that existed before all write <c>.gp3/.gp4/.gp5</c> and exercise only
///     the legacy binary reader.
/// </summary>
public sealed class GpHostileInputTests
{
    /// <summary>§4.1 — <c>&lt;Time&gt;4/0&lt;/Time&gt;</c> reached
    /// <c>new Fraction(16, 0)</c>, which throws DivideByZeroException out of the
    /// whole import. The GP3-5 binary reader has always guarded it.</summary>
    [Theory]
    [InlineData("4/0")]
    [InlineData("0/4")]
    [InlineData("0/0")]
    [InlineData("-4/-4")]
    [InlineData("2000000000/4")]
    [InlineData("4/2000000000")]
    public void AnAbsurdTimeSignature_ImportsInsteadOfCrashing(string time)
    {
        var arr = ImportGpif(Score(time: time));
        Assert.NotEmpty(arr.Levels);
    }

    /// <summary>§4.1 — a zero tuplet numerator reached
    /// <c>new Fraction(den, 0)</c>.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    [InlineData(-3, 2)]
    [InlineData(0, 0)]
    public void AnAbsurdPrimaryTuplet_ImportsInsteadOfCrashing(int num, int den)
    {
        var arr = ImportGpif(Score(tuplet: $"""<PrimaryTuplet num="{num}" den="{den}" />"""));
        Assert.NotEmpty(arr.Levels);
    }

    /// <summary>§4.2 — every augmentation dot DOUBLES the denominator, so an
    /// unbounded count overflowed int into a zero (or negative) denominator.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(31)]
    [InlineData(2000000000)]
    [InlineData(-1)]
    public void AnAbsurdAugmentationDotCount_ImportsInsteadOfCrashing(int dots)
    {
        var arr = ImportGpif(Score(augmentationDots: dots));
        Assert.NotEmpty(arr.Levels);
    }

    /// <summary>
    ///     §4.2 — the unbounded-emit vector, corrected. The audit blamed the time
    ///     signature alone, but <c>ExpandTrill</c> takes <c>min(noteEnd, barEnd)</c>,
    ///     so BOTH have to be absurd: the tuplet ratio (the only unbounded factor
    ///     in a rhythm's duration) makes the note long enough, and the bar has to
    ///     be long enough not to clamp it back. Together they emitted one
    ///     GuitarNote per 32nd of a 10 000-quarter span.
    /// </summary>
    [Fact]
    public void ATrillOverAnAbsurdSpan_DoesNotEmitUnboundedNotes()
    {
        var arr = ImportGpif(Score(
            time: "2500/1",
            tuplet: """<PrimaryTuplet num="1" den="10000" />""",
            trill: true));

        var level = Assert.Single(arr.Levels);
        Assert.InRange(level.Notes.Count, 0, 2048);
    }

    /// <summary>An ordinary trill still expands into its alternating run.</summary>
    [Fact]
    public void AnOrdinaryTrill_StillExpands()
    {
        var arr = ImportGpif(Score(trill: true));
        var level = Assert.Single(arr.Levels);
        Assert.InRange(level.Notes.Count, 2, 32);
        Assert.True(level.Notes.Select(n => n.Fret).Distinct().Count() > 1,
            "a trill must alternate between two frets");
    }

    /// <summary>The same ratio without a trill still must not produce an absurd
    /// sustain — every downstream pass walks it.</summary>
    [Fact]
    public void AnAbsurdTupletRatio_DoesNotProduceAnAbsurdSustain()
    {
        var arr = ImportGpif(Score(tuplet: """<PrimaryTuplet num="1" den="1000000" />"""));
        var level = Assert.Single(arr.Levels);
        var note = Assert.Single(level.Notes);
        Assert.InRange(note.Sustain.DoubleValue, 0.0, 256.0);
    }

    /// <summary>A real triplet must be untouched.</summary>
    [Fact]
    public void ARealTriplet_IsStillApplied()
    {
        var arr = ImportGpif(Score(tuplet: """<PrimaryTuplet num="3" den="2" />"""));
        var level = Assert.Single(arr.Levels);
        var note = Assert.Single(level.Notes);
        // A quarter in a triplet is 2/3 of a beat.
        Assert.Equal(2.0 / 3.0, note.NotationDuration!.Value.DoubleValue, 6);
    }

    /// <summary>§4.3 — the repeat count is unfolded into real bars, so
    /// <c>count="2000000000"</c> over a repeat asked for billions of
    /// <c>UnfoldedBar</c>s: the import hangs and then OOMs.</summary>
    [Fact]
    public void AnAbsurdRepeatCount_IsClamped()
    {
        var masterBars = XElement.Parse("""
            <MasterBars>
              <MasterBar><Time>4/4</Time><Bars>0</Bars><Repeat start="true" end="false" /></MasterBar>
              <MasterBar><Time>4/4</Time><Bars>0</Bars><Repeat start="false" end="true" count="2000000000" /></MasterBar>
            </MasterBars>
            """);

        var unfolded = GpMasterBarSequence.Unfold(masterBars);
        Assert.InRange(unfolded.Count, 2, 256);
    }

    /// <summary>The clamp must not disturb an ordinary repeat.</summary>
    [Fact]
    public void AnOrdinaryRepeatCount_IsUnchanged()
    {
        var masterBars = XElement.Parse("""
            <MasterBars>
              <MasterBar><Time>4/4</Time><Bars>0</Bars><Repeat start="true" end="false" /></MasterBar>
              <MasterBar><Time>4/4</Time><Bars>0</Bars><Repeat start="false" end="true" count="3" /></MasterBar>
            </MasterBars>
            """);

        Assert.Equal(6, GpMasterBarSequence.Unfold(masterBars).Count);
    }

    /// <summary>§4.5 — the gpif string count is unbounded and sizes every
    /// per-string allocation loop downstream. The EOF parser rejects &gt; 8 and
    /// the GP binary reader clamps to 1..7.</summary>
    [Fact]
    public void AnAbsurdStringCount_IsClamped()
    {
        var pitches = string.Join(' ', Enumerable.Repeat("40", 5000));
        var arr = ImportGpif(Score(pitches: pitches));

        Assert.InRange(arr.StringCount, 1, 8);
        Assert.Equal(arr.StringCount, arr.TuningPitches.Count);
    }

    /// <summary>A real 7-string tuning must survive untouched.</summary>
    [Fact]
    public void ASevenStringTuning_IsUnchanged()
    {
        var arr = ImportGpif(Score(pitches: "35 40 45 50 55 59 64"));
        Assert.Equal(7, arr.StringCount);
        Assert.Equal(7, arr.TuningPitches.Count);
    }

    /// <summary>§4.6 — a zero tempo automation made every BeatMarker.Time
    /// Infinity (`60000.0 / 0`), so the imported project was unusable with no
    /// error; a negative one produced a backwards, non-monotonic grid, silently
    /// breaking TimelineMath.BinarySearchBeats' ascending-order assumption.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-120")]
    [InlineData("NaN")]
    public void ANonPositiveTempoAutomation_DoesNotPoisonTheBeatGrid(string value)
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(new GpBarInfo { BeatsInMeasure = 4, NoteDenominator = 4 });
        meta.Bars.Add(new GpBarInfo { BeatsInMeasure = 4, NoteDenominator = 4 });

        var parsed = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.5, Bpm = parsed });

        var beats = GpBeatMapBuilder.Build(meta, 120.0, 0.0);

        Assert.All(beats, b => Assert.True(double.IsFinite(b.Time), $"beat time was {b.Time}"));
        for (var i = 1; i < beats.Count; i++)
        {
            Assert.True(beats[i].Time > beats[i - 1].Time,
                $"beat {i} at {beats[i].Time} did not advance past {beats[i - 1].Time}");
        }
    }

    /// <summary>§4.6 — the same value read from a real gpif Automation element.</summary>
    [Fact]
    public void AZeroTempoAutomationInAGpifFile_DoesNotPoisonTheBeatGrid()
    {
        var meta = ImportGpifMetadata(Score(tempoAutomationValue: "0 2"));
        var beats = GpBeatMapBuilder.Build(meta, 120.0, 0.0);

        Assert.NotEmpty(beats);
        Assert.All(beats, b => Assert.True(double.IsFinite(b.Time), $"beat time was {b.Time}"));
    }

    /// <summary>A real tempo automation must still be applied.</summary>
    [Fact]
    public void ARealTempoAutomationIsStillApplied()
    {
        var meta = ImportGpifMetadata(Score(tempoAutomationValue: "180 2"));
        Assert.Contains(meta.TempoPoints, p => Math.Abs(p.Bpm - 180.0) < 0.001);
    }

    /// <summary>§4.4 — the bend point count is a raw file int32 that pre-sized
    /// the list, so 500 million allocated ~8 GB before the first read hit EOF and
    /// a negative value threw ArgumentOutOfRangeException.</summary>
    [Theory]
    [InlineData(500_000_000)]
    [InlineData(int.MaxValue)]
    public void AHugeBendPointCount_FailsOnTheDataNotTheAllocation(int pointCount)
    {
        // A real chunk header declaring a point count the payload does not
        // carry. The reader must reach EOF on the DATA rather than die
        // pre-sizing a list from the count (500 million entries is ~8 GB).
        var reader = new GpLegacyBinaryReader(new MemoryStream(BendChunkHeader(pointCount)));
        Assert.Throws<EndOfStreamException>(() => GpBendChunkReader.ReadPoints(reader));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANegativeBendPointCount_ReadsNoPointsInsteadOfThrowingOnTheCapacity(int pointCount)
    {
        // `new List<T>(-1)` threw ArgumentOutOfRangeException — an exception
        // shape no caller in the import chain expects.
        var reader = new GpLegacyBinaryReader(new MemoryStream(BendChunkHeader(pointCount)));
        Assert.Empty(GpBendChunkReader.ReadPoints(reader));
    }

    [Theory]
    [InlineData(500_000_000)]
    [InlineData(int.MaxValue)]
    public void AHugeBendPointCount_AlsoFailsSafelyWhenAppending(int pointCount)
    {
        var reader = new GpLegacyBinaryReader(new MemoryStream(BendChunkHeader(pointCount)));
        List<(int Offset, double Step)>? destination = null;
        Assert.Throws<EndOfStreamException>(() => GpBendChunkReader.AppendPoints(reader, ref destination));
    }

    private static byte[] BendChunkHeader(int pointCount)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)1); // bendType
        bw.Write(100); // bendHeight
        bw.Write(pointCount);
        bw.Flush();
        return ms.ToArray();
    }

    // ---- helpers ------------------------------------------------------------

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gphostile-{Guid.NewGuid():N}.gp");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(gpif);
        return path;
    }

    private static Arrangement ImportGpif(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static GpImportMetadata ImportGpifMetadata(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList()).Metadata;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Score(
        string time = "4/4",
        string pitches = "40 45 50 55 59 64",
        string tuplet = "",
        int augmentationDots = 0,
        bool trill = false,
        string? tempoAutomationValue = null)
    {
        var dotElement = augmentationDots == 0
            ? ""
            : $"""<AugmentationDot count="{augmentationDots}" />""";

        var trillProperty = trill ? "<Trill>67</Trill>" : "";

        var automations = tempoAutomationValue == null
            ? ""
            : $"""
               <Automations>
                 <Automation>
                   <Type>Tempo</Type><Linear>false</Linear><Bar>0</Bar>
                   <Position>0</Position><Visible>true</Visible>
                   <Value>{tempoAutomationValue}</Value>
                 </Automation>
               </Automations>
               """;

        return $"""
            <GPIF>
              <Score><Title>Hostile Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks>{automations}</MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Guitar</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>{pitches}</Pitches></Property>
                    <Property name="CapoFret"><Fret>0</Fret></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>{time}</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
              <Beats><Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat></Beats>
              <Notes>
                <Note id="0"><Properties>
                  <Property name="String"><String>0</String></Property>
                  <Property name="Fret"><Fret>5</Fret></Property>
                </Properties>{trillProperty}</Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue>{dotElement}{tuplet}</Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }
}
