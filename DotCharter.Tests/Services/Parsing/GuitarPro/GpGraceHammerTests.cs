using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A grace note that hammers or pulls into its target (GP7+ writes
///     <c>HopoOrigin</c> on the grace note and <c>HopoDestination</c> on the target) is
///     the sound the legato starts from, so it must import as a short note of its own
///     in front of the target and leave the target marked as the hammer-on or pull-off.
///     Importing only a notation hint (<see cref="GuitarNote.GraceFret"/>) left the
///     chart with a bare target note: nothing on the highway to hammer from. A plain
///     grace with no gesture stays a hint, and a grace slide keeps its slide.
/// </summary>
public sealed class GpGraceHammerTests
{
    private static readonly string[] GuitarTuning = ["40", "45", "50", "55", "59", "64"];
    private static readonly string[] BassTuning = ["28", "33", "38", "43"];

    private const string HopoOrigin = """<Property name="HopoOrigin"><Enable /></Property>""";
    private const string HopoDestination = """<Property name="HopoDestination"><Enable /></Property>""";
    private const string LegatoSlide = """<Property name="Slide"><Flags>2</Flags></Property>""";

    private const int GraceString = 2;
    private const int GraceFret = 4;
    private const int TargetFret = 6;

    private sealed record Fixture(
        string[] Tuning, string? GraceFlavour, string GraceProps, string TargetProps, int GraceFretNumber = GraceFret,
        int TargetFretNumber = TargetFret, bool TiedTail = false, int TargetRhythm = 0, bool NextOnString = false, int TailRhythm = 0);

    private static string GpifScore(Fixture f)
    {
        var graced = f.GraceFlavour != null;
        var voiceBeats = (graced ? "0 1 2" : "0 2") + (f.TiedTail ? " 3" : "") + (f.NextOnString ? " 4" : "");
        var graceBeat = graced
            ? $"""<Beat id="1"><GraceNotes>{f.GraceFlavour}</GraceNotes><Rhythm ref="1" /><Notes>1</Notes></Beat>"""
            : "";
        var tailBeat = f.TiedTail ? $"""<Beat id="3"><Rhythm ref="{f.TailRhythm}" /><Notes>3</Notes></Beat>""" : "";
        var tailNote = f.TiedTail
            ? $"""
                <Note id="3"><Tie origin="false" destination="true" /><Properties>
                  <Property name="String"><String>{GraceString}</String></Property>
                  <Property name="Fret"><Fret>{f.TargetFretNumber}</Fret></Property>
                </Properties></Note>
                """
            : "";
        var nextBeat = f.NextOnString ? """<Beat id="4"><Rhythm ref="0" /><Notes>4</Notes></Beat>""" : "";
        var nextNote = f.NextOnString
            ? $"""
                <Note id="4"><Properties>
                  <Property name="String"><String>{GraceString}</String></Property>
                  <Property name="Fret"><Fret>{f.TargetFretNumber + 2}</Fret></Property>
                </Properties></Note>
                """
            : "";
        var targetTie = f.TiedTail ? """<Tie origin="true" destination="false" />""" : "";

        return $"""
            <GPIF>
              <Score><Title>Grace Hammer Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Fixture</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>{string.Join(' ', f.Tuning)}</Pitches></Property>
                    <Property name="CapoFret"><Fret>0</Fret></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>{voiceBeats}</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                {graceBeat}
                <Beat id="2"><Rhythm ref="{f.TargetRhythm}" /><Notes>2</Notes></Beat>
                {tailBeat}
                {nextBeat}
              </Beats>
              <Notes>
                <Note id="0"><Properties>
                  <Property name="String"><String>0</String></Property>
                  <Property name="Fret"><Fret>9</Fret></Property>
                </Properties></Note>
                <Note id="1"><Properties>
                  <Property name="String"><String>{GraceString}</String></Property>
                  <Property name="Fret"><Fret>{f.GraceFretNumber}</Fret></Property>
                  {f.GraceProps}
                </Properties></Note>
                <Note id="2">{targetTie}<Properties>
                  <Property name="String"><String>{GraceString}</String></Property>
                  <Property name="Fret"><Fret>{f.TargetFretNumber}</Fret></Property>
                  {f.TargetProps}
                </Properties></Note>
                {tailNote}
                {nextNote}
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>
                <Rhythm id="2"><NoteValue>Eighth</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }

    /// <summary>A bar whose last beat is a two-string chord and whose grace beat carries one
    /// grace note per string, as GP spells a graced strum.</summary>
    private static string GpifChordScore(string graceProps, string targetProps)
    {
        return $"""
            <GPIF>
              <Score><Title>Grace Hammer Chord Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Fixture</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>{string.Join(' ', GuitarTuning)}</Pitches></Property>
                    <Property name="CapoFret"><Fret>0</Fret></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1 2</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="1" /><Notes>1 2</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>3 4</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties>
                  <Property name="String"><String>5</String></Property>
                  <Property name="Fret"><Fret>3</Fret></Property>
                </Properties></Note>
                <Note id="1"><Properties>
                  <Property name="String"><String>1</String></Property>
                  <Property name="Fret"><Fret>3</Fret></Property>
                  {graceProps}
                </Properties></Note>
                <Note id="2"><Properties>
                  <Property name="String"><String>2</String></Property>
                  <Property name="Fret"><Fret>3</Fret></Property>
                  {graceProps}
                </Properties></Note>
                <Note id="3"><Properties>
                  <Property name="String"><String>1</String></Property>
                  <Property name="Fret"><Fret>5</Fret></Property>
                  {targetProps}
                </Properties></Note>
                <Note id="4"><Properties>
                  <Property name="String"><String>2</String></Property>
                  <Property name="Fret"><Fret>5</Fret></Property>
                  {targetProps}
                </Properties></Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }

    private static ChartLevel ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpgracehammer-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
        {
            writer.Write(gpif);
        }

        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(arrangements).Levels[0];
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Where the target sits when no grace beat exists, taken from an import of the same
    /// bar without the ornament so no test hard-codes the importer's rhythm arithmetic.</summary>
    private static FractionalPosition AuthoredTargetPosition(Fixture f) =>
        Assert.Single(ImportGpif(GpifScore(f with { GraceFlavour = null })).Notes,
            n => n.StringIndex == GraceString && n.Fret == f.TargetFretNumber).Position;

    private static (GuitarNote Grace, GuitarNote Target) Import(Fixture f)
    {
        var level = ImportGpif(GpifScore(f));
        var onString = level.Notes.Where(n => n.StringIndex == GraceString).OrderBy(n => n.Position).ToList();
        Assert.Equal(2, onString.Count); // the grace and its target, nothing merged away
        return (onString[0], onString[1]);
    }

    public static TheoryData<string> Instruments => ["guitar", "bass"];

    private static string[] Tuning(string instrument) => instrument == "bass" ? BassTuning : GuitarTuning;

    // ---- before-beat -------------------------------------------------------

    [Theory]
    [MemberData(nameof(Instruments))]
    public void BeforeBeatGraceHammerOn_IsANoteAheadOfTheBeatAndTheTargetIsTheHammerOn(string instrument)
    {
        var f = new Fixture(Tuning(instrument), "BeforeBeat", HopoOrigin, HopoDestination);
        var authored = AuthoredTargetPosition(f);

        var (grace, target) = Import(f);

        Assert.Equal(GraceFret, grace.Fret);
        Assert.True(grace.Position.CompareTo(authored) < 0, "the grace starts before the beat");
        Assert.Equal(authored, target.Position); // a before-beat grace does not move the target
        Assert.Equal(TargetFret, target.Fret);
        Assert.Equal(HopoType.HammerOn, target.Hopo);
        Assert.Equal(HopoType.None, grace.Hopo);
        Assert.Null(grace.SlideTo);
        Assert.False(grace.LinkNext);
        Assert.Null(target.GraceFret); // the hint is consumed
    }

    [Fact]
    public void GraceHammerOnIntoATiedTarget_KeepsBothTheGraceAndTheTie()
    {
        // The shape of the owner's bass line: the target is the origin of a tie.
        var f = new Fixture(BassTuning, "BeforeBeat", HopoOrigin, HopoDestination, TiedTail: true);

        var level = ImportGpif(GpifScore(f));

        var onString = level.Notes.Where(n => n.StringIndex == GraceString).OrderBy(n => n.Position).ToList();
        Assert.Equal(2, onString.Count); // grace + the merged tied target
        Assert.Equal(GraceFret, onString[0].Fret);
        Assert.Equal(TargetFret, onString[1].Fret);
        Assert.Equal(HopoType.HammerOn, onString[1].Hopo);
    }

    // ---- on-beat -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Instruments))]
    public void OnBeatGraceHammerOn_SitsOnTheBeatAndPushesTheTarget(string instrument)
    {
        var f = new Fixture(Tuning(instrument), "OnBeat", HopoOrigin, HopoDestination);
        var authored = AuthoredTargetPosition(f);

        var (grace, target) = Import(f);

        Assert.Equal(GraceFret, grace.Fret);
        Assert.Equal(authored, grace.Position);
        Assert.True(target.Position.CompareTo(authored) > 0, "an on-beat grace pushes the target late");
        Assert.Equal(HopoType.HammerOn, target.Hopo);
        Assert.Null(target.GraceFret);
    }

    [Fact]
    public void OnBeatGraceHammerOn_DoesNotTurnAQuarterNoteTailIntoABareStrike()
    {
        var f = new Fixture(GuitarTuning, "OnBeat", HopoOrigin, HopoDestination);
        var quarter = new FractionalPosition(1);
        var control = Assert.Single(ImportGpif(GpifScore(f with { GraceFlavour = null })).Notes,
            n => n.StringIndex == GraceString);
        Assert.True(control.Sustain.CompareTo(quarter) >= 0, "the fixture's target sustains a quarter note");

        var (_, target) = Import(f);

        // The ornament eats an eighth of the beat; the sub-quarter sustain drop must not follow.
        Assert.Equal(quarter - (target.Position - AuthoredTargetPosition(f)), target.Sustain);
    }

    [Fact]
    public void OnBeatGraceHammerOn_IntoAQuarterThatAbutsTheNextNote_KeepsQuarterMinusTheGraceSlot()
    {
        var f = new Fixture(GuitarTuning, "OnBeat", HopoOrigin, HopoDestination, NextOnString: true);
        var authored = AuthoredTargetPosition(f);

        var level = ImportGpif(GpifScore(f));
        var onString = level.Notes.Where(n => n.StringIndex == GraceString).OrderBy(n => n.Position).ToList();
        Assert.Equal(3, onString.Count);
        var (grace, target) = (onString[0], onString[1]);

        // The written quarter is a sustained note; the next note on the string clips it
        // only as far as the grace slot it shifted by.
        var slot = target.Position - authored;
        Assert.Equal(authored, grace.Position);
        Assert.Equal(new FractionalPosition(1) - slot, target.Sustain);
        Assert.Equal(onString[2].Position, target.Position + target.Sustain);
    }

    [Fact]
    public void OnBeatGraceHammerOn_IntoAnEighthTiedToAnEighth_KeepsTheWrittenQuarterSustain()
    {
        var f = new Fixture(GuitarTuning, "OnBeat", HopoOrigin, HopoDestination, TiedTail: true, TargetRhythm: 2,
            TailRhythm: 2);
        var authored = AuthoredTargetPosition(f);

        var level = ImportGpif(GpifScore(f));
        var onString = level.Notes.Where(n => n.StringIndex == GraceString).OrderBy(n => n.Position).ToList();

        Assert.Equal(2, onString.Count); // the grace and the merged target
        var slot = onString[1].Position - authored;
        Assert.Equal(new FractionalPosition(1) - slot, onString[1].Sustain);
    }

    [Fact]
    public void OnBeatGraceHammerOn_IntoAnEighth_StaysWithoutATail()
    {
        var f = new Fixture(GuitarTuning, "OnBeat", HopoOrigin, HopoDestination, TargetRhythm: 2, NextOnString: true);

        var level = ImportGpif(GpifScore(f));
        var onString = level.Notes.Where(n => n.StringIndex == GraceString).OrderBy(n => n.Position).ToList();

        Assert.Equal(FractionalPosition.Zero, onString[1].Sustain);
    }

    // ---- direction and degenerate frets ------------------------------------

    [Fact]
    public void GraceThatFallsIntoALowerFret_LandsAsAPullOff()
    {
        var f = new Fixture(GuitarTuning, "BeforeBeat", HopoOrigin, HopoDestination, GraceFretNumber: 9,
            TargetFretNumber: 7);

        var (grace, target) = Import(f);

        Assert.Equal(9, grace.Fret);
        Assert.Equal(HopoType.PullOff, target.Hopo);
    }

    [Fact]
    public void GraceHammerOn_WhoseTargetCarriesNoDestinationFlag_StillMarksTheTarget()
    {
        var f = new Fixture(GuitarTuning, "BeforeBeat", HopoOrigin, "");

        var (_, target) = Import(f);

        Assert.Equal(HopoType.HammerOn, target.Hopo);
    }

    [Fact]
    public void GraceHammerOn_FromTheOpenString_IsANote()
    {
        var f = new Fixture(GuitarTuning, "BeforeBeat", HopoOrigin, HopoDestination, GraceFretNumber: 0,
            TargetFretNumber: 2);

        var (grace, target) = Import(f);

        Assert.Equal(0, grace.Fret);
        Assert.Equal(HopoType.HammerOn, target.Hopo);
    }

    [Fact]
    public void GraceHammerOn_OnTheTargetsOwnFret_StaysAHint()
    {
        var f = new Fixture(GuitarTuning, "BeforeBeat", HopoOrigin, "", GraceFretNumber: 6, TargetFretNumber: 6);

        var level = ImportGpif(GpifScore(f));

        var target = Assert.Single(level.Notes, n => n.StringIndex == GraceString);
        Assert.Equal(6, target.GraceFret);
        Assert.Equal(HopoType.None, target.Hopo);
    }

    // ---- the other grace kinds keep their behaviour ------------------------

    [Theory]
    [MemberData(nameof(Instruments))]
    public void GraceSlide_StillSlidesIntoTheTarget_AndLeavesNoHammerOn(string instrument)
    {
        var f = new Fixture(Tuning(instrument), "BeforeBeat", LegatoSlide, "");

        var (grace, target) = Import(f);

        Assert.Equal(GraceFret, grace.Fret);
        Assert.Equal(TargetFret, grace.SlideTo);
        Assert.True(grace.LinkNext);
        Assert.Equal(HopoType.None, target.Hopo);
    }

    [Theory]
    [MemberData(nameof(Instruments))]
    public void PlainGrace_StaysANotationHint(string instrument)
    {
        var f = new Fixture(Tuning(instrument), "BeforeBeat", "", "");

        var level = ImportGpif(GpifScore(f));

        var target = Assert.Single(level.Notes, n => n.StringIndex == GraceString);
        Assert.Equal(GraceFret, target.GraceFret);
        Assert.Equal(HopoType.None, target.Hopo);
    }

    // ---- chord target -------------------------------------------------------

    [Fact]
    public void GraceHammerOnIntoAChord_BecomesAnOriginChordAndMarksEveryGracedString()
    {
        var level = ImportGpif(GpifChordScore(HopoOrigin, HopoDestination));

        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count); // the ornament and the strum
        var origin = chords[0];
        var strum = chords[1];

        Assert.Equal(2, origin.ChordNotes.Count);
        Assert.All(origin.ChordNotes, cn => Assert.Equal(3, cn.Fret));
        Assert.All(origin.ChordNotes, cn => Assert.Null(cn.SlideTo));
        Assert.All(origin.ChordNotes, cn => Assert.False(cn.LinkNext));
        Assert.All(strum.ChordNotes, cn => Assert.Equal(5, cn.Fret));
        Assert.All(strum.ChordNotes, cn => Assert.Equal(HopoType.HammerOn, cn.Hopo));
        Assert.All(strum.ChordNotes, cn => Assert.Null(cn.GraceFret));
    }
}
