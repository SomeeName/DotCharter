using System.IO.Compression;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     GP6-8 chord DIAGRAMS reach the chart (#492). A gpif track's
///     <c>DiagramCollection</c> carries the name the arranger typed and a finger
///     per string; a beat points at one with <c>&lt;Chord&gt;id&lt;/Chord&gt;</c>.
///     The XML importer used to read neither, so every chord was named and
///     fingered by the suggesters ("Bb7" imported as "A#7").
/// </summary>
public sealed class GpChordDiagramTests
{
    // Dm7 at the 5th fret (x5x565): A5 G5 B6 E5, fingered unlike the suggester.
    private static readonly Dictionary<int, int> Dm7Frets = new() { [1] = 5, [3] = 5, [4] = 6, [5] = 5 };
    private static readonly Dictionary<int, int> Dm7Fingers = new() { [1] = 2, [3] = 3, [4] = 4, [5] = 3 };

    // Bb7 (x13131): a NAME-only diagram, no fingering.
    private static readonly Dictionary<int, int> Bb7Frets = new() { [1] = 1, [2] = 3, [3] = 1, [4] = 3, [5] = 1 };

    private static readonly string[] FingerWords = ["Thumb", "Index", "Middle", "Ring", "Pinky"];

    private sealed record Beat(int? DiagramRef, params (int String, int Fret, bool Muted)[] Notes);

    private static Beat Strum(int? diagramRef, Dictionary<int, int> frets) =>
        new(diagramRef, [.. frets.Select(kv => (kv.Key, kv.Value, false))]);

    // ---- the mapping --------------------------------------------------------

    [Theory]
    [InlineData("Thumb", 0)]
    [InlineData("Index", 1)]
    [InlineData("Middle", 2)]
    [InlineData("Ring", 3)] // what GP7/GP8 write
    [InlineData("Annular", 3)]
    [InlineData("Pinky", 4)]
    [InlineData("Little", 4)]
    [InlineData("I", 1)] // the note property's letters still map
    public void MapDiagramFinger_MapsTheWords(string value, int expected) =>
        Assert.Equal(expected, GpAuthoredFingering.MapDiagramFinger(value));

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Toe")]
    public void MapDiagramFinger_UnfingeredOrGarbage_IsNull(string? value) =>
        Assert.Null(GpAuthoredFingering.MapDiagramFinger(value));

    // ---- a referenced diagram -----------------------------------------------

    [Fact]
    public void AReferencedDiagram_NamesAndFingersItsStrum()
    {
        var arr = Import([Strum(0, Dm7Frets)]);
        var template = TemplateFor(arr, Dm7Frets);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);

        // Not vacuous: the suggesters say something else for this shape.
        var (suggestedName, suggestedFingers) = Suggested(arr, Dm7Frets);
        Assert.NotEqual("Dm7", suggestedName);
        Assert.NotEqual(Dm7Fingers, suggestedFingers);
    }

    [Fact]
    public void ANameOnlyDiagram_NamesItsStrum_AndTheSuggesterFingersIt()
    {
        var arr = Import([Strum(1, Bb7Frets)]);
        var template = TemplateFor(arr, Bb7Frets);

        Assert.Equal("Bb7", template.Name);
        var (suggestedName, suggestedFingers) = Suggested(arr, Bb7Frets);
        Assert.NotEqual("Bb7", suggestedName);
        Assert.Equal(suggestedFingers, template.Fingers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStrumFrettedUnlikeItsDiagram_TakesTheNameOnly_AndWithoutTheReference_Nothing(bool referenced)
    {
        // The top three strings of the Dm7: C F A, all in Dm7 — still that
        // chord, but a different grip, so the diagram's fingers do not apply.
        // Without the <Chord> reference (and no diagram fretted like it) the
        // suggesters name and finger it: the reference is what does the work.
        var partial = new Dictionary<int, int> { [3] = 5, [4] = 6, [5] = 5 };
        var arr = Import([Strum(referenced ? 0 : null, partial)]);
        var template = TemplateFor(arr, partial);
        var (suggestedName, suggestedFingers) = Suggested(arr, partial);

        Assert.NotEqual("Dm7", suggestedName);
        Assert.Equal(referenced ? "Dm7" : suggestedName, template.Name);
        Assert.Equal(suggestedFingers, template.Fingers);
    }

    [Fact]
    public void AStrumSoundingAPitchTheDiagramLacks_TakesNothingFromIt()
    {
        // The Dm7 grip one fret up: D#m7. Arrangers reuse a diagram loosely; a
        // label for other pitches is worse than the suggester's guess.
        var shifted = Dm7Frets.ToDictionary(kv => kv.Key, kv => kv.Value + 1);
        var arr = Import([Strum(0, shifted)]);
        var template = TemplateFor(arr, shifted);
        var (suggestedName, suggestedFingers) = Suggested(arr, shifted);

        Assert.Equal(suggestedName, template.Name);
        Assert.Equal(suggestedFingers, template.Fingers);
    }

    [Fact]
    public void AMutedStringIsNotPartOfTheComparison()
    {
        // x5x565 with the low E struck dead: the hand still holds the diagram.
        var arr = Import([new Beat(0, [(0, 0, true), .. Dm7Frets.Select(kv => (kv.Key, kv.Value, false))])]);
        var template = Assert.Single(arr.ChordTemplates);

        Assert.Equal("Dm7", template.Name);
        foreach (var (s, finger) in Dm7Fingers)
            Assert.Equal(finger, template.Fingers[s]);
    }

    [Fact]
    public void DiagramFretsAreCapoRelative_LikeTheNotes()
    {
        // Capo 2: the file writes every fret two lower, the chart two higher.
        var relative = Dm7Frets.ToDictionary(kv => kv.Key, kv => kv.Value - 2);
        var arr = Import([Strum(0, relative)], capo: 2, dm7BaseFret: 2);
        var template = TemplateFor(arr, Dm7Frets);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);
    }

    // ---- an unreferenced strum ----------------------------------------------

    [Fact]
    public void AnUnreferencedStrum_BorrowsTheDiagramFrettedExactlyLikeIt()
    {
        // The FIRST strum of a shape creates its template, so without borrowing
        // the name would depend on whether the first one carried the reference.
        var arr = Import([Strum(null, Dm7Frets), Strum(0, Dm7Frets)]);
        var template = Assert.Single(arr.ChordTemplates);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);
    }

    [Fact]
    public void AStrumMutedOnEveryString_ComparesItsWrittenFrets()
    {
        // A dead chug over the shape first, then the referenced strum: the chug
        // creates the template both share (real in the owner's tabs).
        var dead = new Beat(null, [.. Dm7Frets.Select(kv => (kv.Key, kv.Value, true))]);
        var arr = Import([dead, Strum(0, Dm7Frets)]);
        var template = Assert.Single(arr.ChordTemplates);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);
    }

    [Fact]
    public void AStaleReference_IsAsGoodAsNone()
    {
        // The Dm7 grip pointing at the Bb7 diagram (A and C are not in Bb7): the
        // diagram fretted exactly like it names it instead.
        var arr = Import([Strum(1, Dm7Frets)]);
        var template = Assert.Single(arr.ChordTemplates);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);
    }

    [Fact]
    public void TwoDiagramsOfOneShapeThatDisagree_LendNoName()
    {
        var arr = Import([Strum(null, Dm7Frets)], conflictingDiagram: true);
        var template = Assert.Single(arr.ChordTemplates);

        Assert.Equal(Suggested(arr, Dm7Frets).Name, template.Name);
        // The fingering is a property of the frets, and both diagrams agree on it.
        Assert.Equal(Dm7Fingers, template.Fingers);
    }

    [Fact]
    public void ANotesOwnLeftFingering_BeatsTheDiagram()
    {
        var arr = Import([Strum(0, Dm7Frets)], noteFingerOnString1: 1);
        var template = TemplateFor(arr, Dm7Frets);

        Assert.Equal(1, template.Fingers[1]);
        Assert.Equal(Dm7Fingers[3], template.Fingers[3]);
    }

    [Fact]
    public void ALetRingArpeggioOfADiagram_TakesItsNameAndFingers()
    {
        // The chord picked one string at a time under let-ring: no strum at all,
        // the hand shape is the run's. Arpeggiated diagrams are common in the
        // owner's tabs.
        var arr = Import([.. Dm7Frets.Select(kv => new Beat(null, (kv.Key, kv.Value, false)))], letRing: true);
        var template = TemplateFor(arr, Dm7Frets);

        Assert.Equal("Dm7", template.Name);
        Assert.Equal(Dm7Fingers, template.Fingers);
        Assert.Contains(Assert.Single(arr.Levels).HandShapes, h => h.TemplateId == template.Id);
    }

    // ---- the import FHP pass ------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADiagramFingeringLikeTheSuggestion_IsNeverRefingeredOnImport(bool withDiagram)
    {
        // Fret 8, then G9+B10, then 8 again (#494's scenario). The diagram
        // fingers the double stop 1-2 — exactly the suggestion — so it is the
        // AUTHORED mark, not the fingers, that keeps the follower off it.
        // Without the diagram the same chord follows the hand at 8.
        var doubleStop = new Dictionary<int, int> { [3] = 9, [4] = 10 };
        var arr = Import(
            [new Beat(null, (3, 8, false)), Strum(withDiagram ? 2 : null, doubleStop),
             new Beat(null, (3, 8, false)), new Beat(null, (4, 8, false))],
            doubleStopDiagram: withDiagram);
        var level = Assert.Single(arr.Levels);
        var chord = Assert.Single(level.Chords);
        var template = arr.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        var atChord = level.Anchors.Last(a => a.Position <= chord.Position);

        if (withDiagram)
        {
            Assert.Equal(new Dictionary<int, int> { [3] = 1, [4] = 2 }, template.Fingers);
            Assert.Equal(9, atChord.Fret);
        }
        else
        {
            Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 3 }, template.Fingers);
            Assert.Equal(8, atChord.Fret);
        }

        Assert.Single(arr.ChordTemplates);
    }

    // ---- helpers ------------------------------------------------------------

    private static ChordTemplate TemplateFor(Arrangement arr, Dictionary<int, int> frets) =>
        arr.ChordTemplates.Single(t => t.Frets.Count == frets.Count &&
                                       frets.All(kv => t.Frets.TryGetValue(kv.Key, out var f) && f == kv.Value));

    private static (string Name, Dictionary<int, int> Fingers) Suggested(Arrangement arr, Dictionary<int, int> frets)
    {
        var probe = new ChordTemplate { Frets = new Dictionary<int, int>(frets) };
        ChordTemplateFingerSetter.SetSuggestedFingers(probe, arr.StringCount);
        return (ChordNameSuggester.SuggestFirstChordName(arr, probe.Frets), probe.Fingers);
    }

    private static Arrangement Import(IReadOnlyList<Beat> beats, int capo = 0, int dm7BaseFret = 4,
        bool conflictingDiagram = false, bool doubleStopDiagram = false, int? noteFingerOnString1 = null,
        bool letRing = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpdiagram-{Guid.NewGuid():N}.gp");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Content/score.gpif");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(Gpif(beats, capo, dm7BaseFret, conflictingDiagram, doubleStopDiagram, noteFingerOnString1,
                    letRing));
            }

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // A diagram in the GP7 layout: frets relative to baseFret, fingers as words,
    // and an unfingered string spelled the way GP spells it.
    private static string Diagram(int id, string name, int baseFret, Dictionary<int, int> frets,
        Dictionary<int, int>? fingers)
    {
        var fretXml = string.Concat(frets.Select(kv =>
            $"""<Fret string="{kv.Key}" fret="{(kv.Value == 0 ? 0 : kv.Value - baseFret)}"/>"""));
        var fingeringXml = fingers == null
            ? ""
            : "<Fingering>" + string.Concat(fingers.Select(kv =>
                  $"""<Position finger="{FingerWords[kv.Value]}" fret="{frets[kv.Key] - baseFret}" string="{kv.Key}"/>""")) +
              """<Position finger="None" fret="4294967295" string="0"/></Fingering>""";
        return $"""
            <Item id="{id}" name="{name}">
              <Diagram stringCount="6" fretCount="5" baseFret="{baseFret}">{fretXml}{fingeringXml}</Diagram>
            </Item>
            """;
    }

    private static string Gpif(IReadOnlyList<Beat> beats, int capo, int dm7BaseFret, bool conflictingDiagram,
        bool doubleStopDiagram, int? noteFingerOnString1, bool letRing)
    {
        // The diagrams are written in the FILE's (capo-relative) frets.
        var dm7Relative = Dm7Frets.ToDictionary(kv => kv.Key, kv => kv.Value - capo);
        var diagrams = Diagram(0, "Dm7", dm7BaseFret, dm7Relative, Dm7Fingers) +
                       Diagram(1, "Bb7", 0, Bb7Frets, null) +
                       (doubleStopDiagram
                           ? Diagram(2, "G5/B", 8, new Dictionary<int, int> { [3] = 9, [4] = 10 },
                               new Dictionary<int, int> { [3] = 1, [4] = 2 })
                           : "") +
                       (conflictingDiagram ? Diagram(3, "F6", dm7BaseFret, dm7Relative, Dm7Fingers) : "");

        var notes = new List<string>();
        var beatXml = new List<string>();
        for (var b = 0; b < beats.Count; b++)
        {
            var ids = new List<int>();
            foreach (var (s, fret, muted) in beats[b].Notes)
            {
                var finger = s == 1 && noteFingerOnString1 is { } f
                    ? $"""<Property name="LeftFingering"><Fingering>{"PIMAC"[f]}</Fingering></Property>"""
                    : "";
                ids.Add(notes.Count);
                notes.Add($"""
                    <Note id="{notes.Count}"><Properties>
                      <Property name="String"><String>{s}</String></Property>
                      <Property name="Fret"><Fret>{fret}</Fret></Property>
                      {(muted ? "<Property name=\"Muted\"><Enable /></Property>" : "")}{finger}
                    </Properties>{(letRing ? "<LetRing />" : "")}</Note>
                    """);
            }

            var chordRef = beats[b].DiagramRef is { } r ? $"<Chord><![CDATA[{r}]]></Chord>" : "";
            beatXml.Add($"""<Beat id="{b}"><Rhythm ref="0" />{chordRef}<Notes>{string.Join(' ', ids)}</Notes></Beat>""");
        }

        var range = Enumerable.Range(0, beats.Count).ToList();
        return $"""
            <GPIF>
              <Score><Title>Diagram Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Guitar</Name>
                  <Staves><Staff><Properties>
                    <Property name="CapoFret"><Fret>{capo}</Fret></Property>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                    <Property name="DiagramCollection"><Items>{diagrams}</Items></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>{string.Concat(range.Select(i => $"<MasterBar><Time>1/4</Time><Bars>{i}</Bars></MasterBar>"))}</MasterBars>
              <Bars>{string.Concat(range.Select(i => $"<Bar id=\"{i}\"><Voices>{i} -1 -1 -1</Voices></Bar>"))}</Bars>
              <Voices>{string.Concat(range.Select(i => $"<Voice id=\"{i}\"><Beats>{i}</Beats></Voice>"))}</Voices>
              <Beats>{string.Concat(beatXml)}</Beats>
              <Notes>{string.Concat(notes)}</Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
    }
}
