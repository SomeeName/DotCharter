namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     The other half of EOF's finger/handshape validator
///     (<c>EOF_SRC/song.c:12596-12612</c>, its <c>-1</c>): a sound inside a hand
///     shape must agree with the shape it is played under.
///     <para>
///         <b>Both denominators, because they disagree.</b> Over the 98
///         hand-finished RS2 exports in <c>TestData/EOF</c>: 9,878 hand shapes,
///         89,267 (sound, string) pairs inside one, <b>15 disagreements in 5
///         arrangements</b> — the "6 = 0.01%" this file claimed until 0.44 does
///         not reproduce. Over 691 official Rocksmith arrangements: 642,429
///         pairs, <b>4,443 disagreements = 0.69%</b>, i.e. 1,396 findings across
///         97 arrangements.
///     </para>
///     <para>
///         The corpus carries <b>zero arpeggio templates</b> (0 of 1,681), so it
///         could not measure the construct the rule reasons about at all — and
///         on official content an arpeggio's sounds routinely fall outside its
///         declared grip (2.54% of pairs vs 0.59% under a plain shape). Skipping
///         arpeggios and sub-three-string hand-POSITION bars takes official
///         content to 446 findings and keeps 9 of the corpus's 15.
///     </para>
/// </summary>
public sealed class HandShapeContentRuleTests
{
    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Validate(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());

    private static IReadOnlyList<ArrangementIssue> Violations(Arrangement arr) =>
        Validate(arr).Where(i => i.Type == ArrangementIssueType.SoundViolatesHandShape).ToList();

    /// <summary>
    ///     A shape over beats 0..4 declaring
    ///     {string 0: fret 5, string 1: fret 7, string 2: fret 7}.
    ///     <para>
    ///         THREE strings, not two, and that is load-bearing: the rule only
    ///         inspects templates that declare at least three, because one and two
    ///         string templates are Rocksmith's hand-POSITION bar rather than a
    ///         grip. This fixture declared two until 0.44 and every test built on
    ///         it went silent the moment that guard landed — which is the proof
    ///         the guard is not vacuous.
    ///     </para>
    /// </summary>
    private static Arrangement WithShape(params (int Beat, int String, int Fret)[] notes) =>
        WithTemplate(new ChordTemplate
        {
            Id = 0, Name = "Shape",
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7 }
        }, notes);

    /// <summary>The same one-shape arrangement over an arbitrary template.</summary>
    private static Arrangement WithTemplate(ChordTemplate template,
        params (int Beat, int String, int Fret)[] notes)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);

        arr.ChordTemplates.Add(template);

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4),
            TemplateId = 0
        });

        foreach (var (beat, stringIndex, fret) in notes)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret
            });
        }

        return arr;
    }

    /// <summary>
    ///     An arpeggio: the bar names the whole grip and the notes pick it out one
    ///     string at a time. The check is ONE-DIRECTIONAL for exactly this reason —
    ///     requiring the shape's strings all to be played would flag every
    ///     arpeggio in every chart.
    /// </summary>
    [Fact]
    public void NotesPickingOutTheShapeOneStringAtATimeAreFine()
    {
        var arr = WithShape((0, 0, 5), (1, 1, 7), (2, 0, 5), (3, 1, 7));

        Assert.Empty(Violations(arr));
    }

    /// <summary>A three-string, non-arpeggio template is a GRIP, and a sound on a
    ///     string it never names still contradicts it.</summary>
    [Fact]
    public void ANoteOnAStringTheShapeDoesNotDeclareIsReported()
    {
        var arr = WithShape((0, 0, 5), (1, 4, 9));

        var issue = Assert.Single(Violations(arr));
        Assert.Equal("E05", issue.Code);
        Assert.Equal(4, issue.StringIndex);
        Assert.NotNull(issue.HandShape);
    }

    /// <summary>
    ///     An ARPEGGIO is inspected by nothing. Its template names the grip; the
    ///     sounds under it walk through that position and let strings the grip
    ///     never mentions ring out — measured at 2.54% of (sound, string) pairs
    ///     over 691 official arrangements, against 0.59% for plain shapes.
    ///     <para>
    ///         This is the SNG spelling: the package importer's chord-template conversion
    ///         sets <see cref="ChordTemplate.Arpeggio" /> from chord mask 0x1 and
    ///         never <see cref="ChordTemplate.ForceArpeggioInRs" />.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnArpeggioTemplateFromAPsarcImportReportsNothing()
    {
        var arr = WithTemplate(new ChordTemplate
        {
            Id = 0, Name = "A5", Arpeggio = true,
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7 }
        }, (1, 4, 0), (2, 5, 0));

        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     …and the RS-XML spelling of the SAME construct.
    ///     <c>RsXmlSongReader.ReadChordTemplates</c> derives
    ///     <see cref="ChordTemplate.ForceArpeggioInRs" /> from a <c>-arp</c>
    ///     displayName and never sets <see cref="ChordTemplate.Arpeggio" />, so a
    ///     guard keyed on one flag alone works on psarc imports and is silently
    ///     dead on every DLC-Builder / EOF chart.
    /// </summary>
    [Fact]
    public void AnArpeggioTemplateFromAnRsXmlImportReportsNothing()
    {
        var arr = WithTemplate(new ChordTemplate
        {
            Id = 0, Name = "A5", ForceArpeggioInRs = true,
            Frets = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7 }
        }, (1, 4, 0), (2, 5, 0));

        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     A one- or two-string template is a hand POSITION, not a grip: it says
    ///     where the hand sits and the sounds under it use the rest of what that
    ///     hand reaches. 64% of the 1,396 findings this rule raised on official
    ///     content came from templates this narrow.
    /// </summary>
    [Fact]
    public void ATwoStringTemplateReportsNothingForASoundOnAThirdString()
    {
        var arr = WithTemplate(new ChordTemplate
        {
            Id = 0, Name = "",
            Frets = new Dictionary<int, int> { [0] = 3, [1] = 2 }
        }, (1, 2, 0), (2, 3, 0));

        Assert.Empty(Violations(arr));
    }

    [Fact]
    public void ANoteOnADeclaredStringAtAnotherFretIsReported()
    {
        var arr = WithShape((0, 0, 5), (1, 1, 9));

        var issue = Assert.Single(Violations(arr));
        Assert.Contains("fret 9", issue.Message);
        Assert.Contains("fret 7", issue.Message);
    }

    /// <summary>
    ///     A hand shape's end is EXCLUSIVE — shapes abut, and the next shape's
    ///     opening chord sits exactly on this one's end. A closed span would blame
    ///     every abutting shape for its neighbour's first note.
    /// </summary>
    [Fact]
    public void ANoteExactlyOnTheShapesEndBelongsToTheNextShape()
    {
        var arr = WithShape((4, 3, 12));

        Assert.Empty(Violations(arr));
    }

    [Fact]
    public void ANoteOutsideAnyShapeIsNotChecked()
    {
        var arr = WithShape((8, 3, 12));

        Assert.Empty(Violations(arr));
    }

    /// <summary>A shape whose template cannot be resolved declares nothing, so
    ///     there is nothing to contradict.</summary>
    [Fact]
    public void AShapeWithNoResolvableTemplateReportsNothing()
    {
        var arr = WithShape((1, 4, 9));
        arr.Levels[0].HandShapes[0].TemplateId = 77;

        Assert.Empty(Violations(arr));
    }

    /// <summary>One finding per SOUND — a strum disagreeing on three strings is
    ///     one mistake, not three.</summary>
    [Fact]
    public void AChordReportsOnceRatherThanOncePerString()
    {
        var arr = WithShape();
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 9 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 11 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 12 });
        arr.Levels[0].Chords.Add(chord);

        var issue = Assert.Single(Violations(arr));
        Assert.Same(chord, issue.Chord);
    }

    /// <summary>An open string is a fret like any other here: if the shape
    ///     declares fret 5 and the sound plays the string open, the hand drawn
    ///     in-game still cannot produce it.</summary>
    [Fact]
    public void AnOpenStringWhereTheShapeDeclaresAFretIsReported()
    {
        var arr = WithShape((1, 0, 0));

        Assert.Single(Violations(arr));
    }
}
