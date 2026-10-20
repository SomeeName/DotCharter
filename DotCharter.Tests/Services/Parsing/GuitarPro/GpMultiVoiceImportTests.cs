using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// Multi-voice + grace-note import from the GP7/GP8 XML container. GP bars
/// carry up to four voice slots; slot 0 becomes the playable chart level and
/// slots 1..3 import as notation-only <see cref="NotationVoice"/> sidecars on
/// the arrangement (voices 2-4 used to be dropped wholesale). Grace beats
/// (<c>GraceNotes</c>) carry no bar time in gpif — GP adds them to already-full
/// bars — so they must not advance the import cursor (the old importer shifted
/// every onset after a grace late) and instead attach as the target note's
/// <c>GraceFret</c> hint.
/// <para>Fixture provenance: the gpif shape (Bars/Voices slot list,
/// Voices/Beats/Notes/Rhythms id maps, Properties) is validated against real
/// GP8 exports on this machine ("Hotel California.gp" et al.); no real
/// multi-voice/grace artifact was available, so those two elements follow the
/// format as read by Guitar Pro's own bar-fill rules and the alphaTab/TuxGuitar
/// gpif readers (<c>GraceNotes</c> = BeforeBeat|OnBeat, voice slots beyond the
/// first).</para>
/// </summary>
public sealed class GpMultiVoiceImportTests
{
    /// <summary>Builds a real .gp zip (Content/score.gpif) so the test runs the
    /// whole <see cref="GpImporter"/> path, not just the XML walker.</summary>
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpvoice-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static string TwoVoiceGpif() => """
        <GPIF>
          <Score><Title>Two Voice Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
          </MasterBars>
          <Bars>
            <Bar id="0"><Voices>0 1 -1 -1</Voices></Bar>
            <Bar id="1"><Voices>2 3 -1 -1</Voices></Bar>
          </Bars>
          <Voices>
            <Voice id="0"><Beats>0 1 2 3</Beats></Voice>
            <Voice id="1"><Beats>4 5</Beats></Voice>
            <Voice id="2"><Beats>6 7 8 9 10</Beats></Voice>
            <Voice id="3"><Beats>11 12</Beats></Voice>
          </Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
            <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
            <Beat id="4"><Rhythm ref="1" /><Notes>4</Notes></Beat>
            <Beat id="5"><Rhythm ref="1" /><Notes>5</Notes></Beat>
            <Beat id="6"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="2" /><Notes>6</Notes></Beat>
            <Beat id="7"><Rhythm ref="0" /><Notes>7</Notes></Beat>
            <Beat id="8"><Rhythm ref="0" /><Notes>8</Notes></Beat>
            <Beat id="9"><Rhythm ref="0" /><Notes>9</Notes></Beat>
            <Beat id="10"><Rhythm ref="0" /><Notes>10</Notes></Beat>
            <Beat id="11"><Rhythm ref="1" /><Notes>11 12</Notes></Beat>
            <Beat id="12"><Rhythm ref="1" /></Beat>
          </Beats>
          <Notes>
            <Note id="0"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="1"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>7</Fret></Property>
            </Properties></Note>
            <Note id="2"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="3"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>7</Fret></Property>
            </Properties></Note>
            <Note id="4"><Tie origin="true" /><Properties>
              <Property name="String"><String>5</String></Property>
              <Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="5"><Tie origin="false" /><Properties>
              <Property name="String"><String>5</String></Property>
              <Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="6"><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="7"><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="8"><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>7</Fret></Property>
            </Properties></Note>
            <Note id="9"><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>8</Fret></Property>
            </Properties></Note>
            <Note id="10"><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>10</Fret></Property>
            </Properties></Note>
            <Note id="11"><Properties>
              <Property name="String"><String>4</String></Property>
              <Property name="Fret"><Fret>0</Fret></Property>
            </Properties></Note>
            <Note id="12"><Properties>
              <Property name="String"><String>5</String></Property>
              <Property name="Fret"><Fret>0</Fret></Property>
            </Properties></Note>
          </Notes>
          <Rhythms>
            <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Half</NoteValue></Rhythm>
            <Rhythm id="2"><NoteValue>16th</NoteValue></Rhythm>
          </Rhythms>
        </GPIF>
        """;

    private static Arrangement ImportSingleTrack(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(arrangements);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Voice1_BecomesThePlayableChart_Voices2Plus_BecomeNotationSidecars()
    {
        var arr = ImportSingleTrack(TwoVoiceGpif());
        var level = Assert.Single(arr.Levels);

        // The chart level holds ONLY voice 1: 4 quarters bar0 + 4 quarters bar1.
        Assert.Equal(8, level.Notes.Count);
        Assert.All(level.Notes, n => Assert.True(n.StringIndex is 0 or 2));
        Assert.Empty(level.Chords);

        // Voice 2 imported as ONE notation sidecar.
        var voice = Assert.Single(arr.NotationVoices);
        Assert.Equal(1, voice.VoiceIndex);

        // Bar 0: the two tied halves merged into one whole note with the
        // SUMMED authored duration and the full sustain (AdjustSustains must
        // not zero it - the sidecar keeps authored data verbatim).
        var whole = Assert.Single(voice.Notes);
        Assert.Equal(new FractionalPosition(0), whole.Position);
        Assert.Equal(5, whole.StringIndex);
        Assert.Equal(3, whole.Fret);
        Assert.Equal(new Fraction(4, 1), whole.NotationDuration);
        Assert.Equal(new FractionalPosition(4), whole.Sustain);

        // Bar 1: the sidecar chord (two open strings, authored half).
        var chord = Assert.Single(voice.Chords);
        Assert.Equal(new FractionalPosition(4), chord.Position);
        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.Equal(new Fraction(2, 1), chord.NotationDuration);
    }

    [Fact]
    public void GraceBeat_AttachesAsHint_AndDoesNotShiftPositions()
    {
        var arr = ImportSingleTrack(TwoVoiceGpif());
        var level = Assert.Single(arr.Levels);

        var bar1 = level.Notes.Where(n => n.Position.BeatId >= 4).OrderBy(n => n.Position).ToList();
        Assert.Equal(4, bar1.Count);

        // The grace 16th consumed NO bar time: the four quarters sit exactly
        // on beats 4..7 (the old importer shifted them all a 16th late).
        Assert.Equal(new[] { 4, 5, 6, 7 }, bar1.Select(n => n.Position.BeatId));
        Assert.All(bar1, n => Assert.Equal(Fraction.Zero, n.Position.Fraction));

        // ...and became the target note's ornament hint (same string).
        Assert.Equal(3, bar1[0].GraceFret);
        Assert.Equal(5, bar1[0].Fret);
        Assert.All(bar1.Skip(1), n => Assert.Null(n.GraceFret));
    }

    [Fact]
    public void SingleVoiceFile_ImportsWithNoSidecars()
    {
        // The exact shape every single-voice GP export has: one voice slot
        // used, the rest -1 (verified against real GP8 files).
        var arr = ImportSingleTrack("""
            <GPIF>
              <Score><Title>Single Voice</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>
                <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
              </MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties>
                  <Property name="String"><String>0</String></Property>
                  <Property name="Fret"><Fret>5</Fret></Property>
                </Properties></Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """);

        Assert.Single(Assert.Single(arr.Levels).Notes);
        Assert.Empty(arr.NotationVoices);
    }
}
