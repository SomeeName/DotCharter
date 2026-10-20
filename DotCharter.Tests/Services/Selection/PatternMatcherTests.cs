using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     The pattern engine, facet by facet. Every test here is the same shape:
///     build a chart where two things are alike under ONE reading and unalike
///     under another, then prove the facet is what decides. A facet that cannot
///     be shown to change the answer is a checkbox with no meaning, and the whole
///     point of the builder is that the owner can tell the difference.
/// </summary>
public sealed class PatternMatcherTests
{
    private static FractionalPosition At(int beat) => new(beat);

    private static FractionalPosition At(int beat, int numerator, int denominator) =>
        new(beat, new Fraction(numerator, denominator));

    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = At(beat), StringIndex = stringIndex, Fret = fret
    };

    private static Chord Strum(int beat, params (int StringIndex, int Fret)[] notes)
    {
        var chord = new Chord { Position = At(beat) };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static PatternSource SourceOf(ChartLevel level, Arrangement? arrangement = null) =>
        new(arrangement, level.Notes, level.Chords, level.HandShapes, level.Anchors,
            arrangement?.EventPoints ?? (IReadOnlyList<EventPoint>)[]);

    private static PatternSeed SeedNotes(params GuitarNote[] notes) => new(notes, [], [], []);
    private static PatternSeed SeedChords(params Chord[] chords) => new([], chords, [], []);
    private static PatternSeed SeedAnchors(params FretAnchor[] anchors) => new([], [], [], anchors);

    private static MatchPattern NotePattern() => new()
    {
        Name = "test", Scope = PatternScope.Notes, Fret = FretRule.Exact,
        Strings = StringRule.Exact, MatchSize = true, MatchSoundKind = true
    };

    // ------------------------------------------------------------ the basics

    [Fact]
    public void AnEmptySeed_MatchesNothing()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 0, 5));
        level.Notes.Add(Note(1, 0, 5));

        var matches = PatternMatcher.Find(NotePattern(), PatternSeed.Empty, SourceOf(level));

        // A pattern is a question ABOUT a selection, never a standing filter —
        // an empty seed must not quietly select the whole chart.
        Assert.Equal(0, matches.Count);
    }

    [Fact]
    public void TheSeedItself_IsNeverAMatch()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(4, 0, 5));

        var matches = PatternMatcher.Find(NotePattern(), SeedNotes(seed), SourceOf(level));

        Assert.Same(level.Notes[1], Assert.Single(matches.Notes));
    }

    [Fact]
    public void AScopeThatExcludesTheSeedKind_MatchesNothing()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(4, 0, 5));

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);
    }

    // -------------------------------------------------------------- the fret

    [Fact]
    public void FretExact_RefusesTheSameShapeElsewhereOnTheNeck()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7), (2, 7));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (0, 8), (1, 10), (2, 10)));

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Count);
    }

    [Fact]
    public void FretTransposable_FindsTheSameShapeElsewhereOnTheNeck()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7), (2, 7));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (0, 8), (1, 10), (2, 10)));

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        pattern.Fret = FretRule.Transposable;

        Assert.Same(level.Chords[1],
            Assert.Single(PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Chords));
    }

    /// <summary>
    ///     An open string cannot move with the hand, so a shape containing one is
    ///     only ever alike another shape with the SAME string open. Without this
    ///     rule an open-E power chord would "transpose" onto a fretted one and
    ///     select a chord that sounds nothing like it.
    /// </summary>
    [Fact]
    public void FretTransposable_NeverTransposesAnOpenString()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 0), (1, 2), (2, 2));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (0, 3), (1, 5), (2, 5))); // the same INTERVALS, all fretted
        level.Chords.Add(Strum(8, (0, 0), (1, 5), (2, 5))); // open + the same intervals above it

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        pattern.Fret = FretRule.Transposable;

        var matches = PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level));

        Assert.Same(level.Chords[2], Assert.Single(matches.Chords));
    }

    [Fact]
    public void FretIgnore_MatchesAcrossFretsButStillHonoursTheStrings()
    {
        var level = new ChartLevel();
        var seed = Note(0, 2, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(4, 2, 12)); // same string, different fret
        level.Notes.Add(Note(8, 3, 12)); // different string

        var pattern = NotePattern();
        pattern.Fret = FretRule.Ignore;

        var matches = PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level));

        Assert.Same(level.Notes[1], Assert.Single(matches.Notes));
    }

    // ------------------------------------------------------------- the shape

    [Fact]
    public void MatchStringsOff_FindsTheSameFretsOnOtherStrings()
    {
        var level = new ChartLevel();
        var seed = Note(0, 2, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(4, 5, 5));

        var pattern = NotePattern();
        Assert.Equal(0, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);

        pattern.Strings = StringRule.Ignore;
        Assert.Same(level.Notes[1],
            Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Notes));
    }

    [Fact]
    public void MatchSize_IsWhatKeepsATwoNoteShapeApartFromAThreeNoteOne()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (0, 5), (1, 7), (2, 7)));

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        pattern.Strings = StringRule.Ignore;
        pattern.Fret = FretRule.Ignore;
        pattern.MatchSize = true;

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Count);

        pattern.MatchSize = false;
        Assert.Equal(1, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Count);
    }

    /// <summary>
    ///     A chord and the same pitches spelled as loose notes are the same thing
    ///     to a HAND, which is the reading <c>MatchSoundKind = false</c> asks for.
    /// </summary>
    [Fact]
    public void MatchSoundKindOff_LetsAChordMatchLooseNotes()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);
        level.Chords.Add(Strum(4, (0, 5)));

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Sounds;

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);

        pattern.MatchSoundKind = false;
        Assert.Same(level.Chords[0],
            Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Chords));
    }

    // -------------------------------------------------------- the techniques

    [Fact]
    public void ATechniqueNotTicked_IsIgnoredEntirely()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);

        var bent = Note(4, 0, 5);
        bent.BendValues.Add(new BendValue(At(4, 1, 2), 1.0f));
        level.Notes.Add(bent);

        // No technique facets: a clean note and a bent one are alike.
        Assert.Equal(1, PatternMatcher.Find(NotePattern(), SeedNotes(seed), SourceOf(level)).Count);

        var strict = NotePattern();
        strict.Techniques = TechniqueFacet.Bend;
        Assert.Equal(0, PatternMatcher.Find(strict, SeedNotes(seed), SourceOf(level)).Count);
    }

    /// <summary>The bend facet compares the whole CURVE, not merely "has a bend"
    ///     — two different bends are two different sounds.</summary>
    [Fact]
    public void TheBendFacet_ComparesTheCurveAndNotJustItsPresence()
    {
        var level = new ChartLevel();

        var seed = Note(0, 0, 5);
        seed.BendValues.Add(new BendValue(At(0, 1, 2), 1.0f));
        level.Notes.Add(seed);

        var same = Note(4, 0, 5);
        same.BendValues.Add(new BendValue(At(4, 1, 2), 1.0f));
        level.Notes.Add(same);

        var deeper = Note(8, 0, 5);
        deeper.BendValues.Add(new BendValue(At(8, 1, 2), 2.0f));
        level.Notes.Add(deeper);

        var pattern = NotePattern();
        pattern.Techniques = TechniqueFacet.Bend;

        Assert.Same(same, Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Notes));
    }

    /// <summary>
    ///     A slide is a MOVEMENT, so it is compared as a distance and survives
    ///     transposition. Storing the destination fret instead would make every
    ///     transposable pattern silently refuse every sliding note.
    /// </summary>
    [Fact]
    public void TheSlideFacet_SurvivesTransposition()
    {
        var level = new ChartLevel();

        var seed = Note(0, 0, 5);
        seed.SlideTo = 7;
        level.Notes.Add(seed);

        var transposed = Note(4, 0, 10);
        transposed.SlideTo = 12; // the same +2 slide, five frets up
        level.Notes.Add(transposed);

        var wrongDistance = Note(8, 0, 10);
        wrongDistance.SlideTo = 15;
        level.Notes.Add(wrongDistance);

        var pattern = NotePattern();
        pattern.Fret = FretRule.Transposable;
        pattern.Techniques = TechniqueFacet.Slide;

        Assert.Same(transposed, Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Notes));
    }

    // --------------------------------------------------------------- the run

    [Fact]
    public void AsRun_FindsTheWholeLickAgainAndReportsItAsOneOccurrence()
    {
        var level = new ChartLevel();
        var a = Note(0, 0, 5);
        var b = Note(1, 0, 7);
        var c = Note(2, 1, 5);
        level.Notes.Add(a);
        level.Notes.Add(b);
        level.Notes.Add(c);

        level.Notes.Add(Note(8, 0, 5));
        level.Notes.Add(Note(9, 0, 7));
        level.Notes.Add(Note(10, 1, 5));

        var pattern = NotePattern();
        pattern.Grouping = PatternGrouping.AsRun;

        var matches = PatternMatcher.Find(pattern, SeedNotes(a, b, c), SourceOf(level));

        Assert.Equal(3, matches.Count);
        // Three ELEMENTS but ONE occurrence — that distinction is what stops
        // "go to next match" stepping through a lick note by note.
        Assert.Single(matches.Groups);
        Assert.Equal(At(8), matches.Groups[0].Position);
    }

    [Fact]
    public void AsRun_WithExactRhythm_RefusesTheSameNotesAtADifferentSpacing()
    {
        var level = new ChartLevel();
        var a = Note(0, 0, 5);
        var b = Note(1, 0, 7);
        level.Notes.Add(a);
        level.Notes.Add(b);

        level.Notes.Add(Note(8, 0, 5));
        level.Notes.Add(Note(12, 0, 7)); // same two notes, four beats apart instead of one

        var pattern = NotePattern();
        pattern.Grouping = PatternGrouping.AsRun;
        pattern.Rhythm = RhythmRule.Exact;
        Assert.Equal(0, PatternMatcher.Find(pattern, SeedNotes(a, b), SourceOf(level)).Count);

        pattern.Rhythm = RhythmRule.Ignore;
        Assert.Equal(2, PatternMatcher.Find(pattern, SeedNotes(a, b), SourceOf(level)).Count);
    }

    /// <summary>A run of identical notes must not report overlapping windows as
    ///     separate occurrences — otherwise four notes in a row yield three
    ///     "matches" of a two-note lick and the navigation crawls.</summary>
    [Fact]
    public void AsRun_DoesNotReportOverlappingWindows()
    {
        var level = new ChartLevel();
        var a = Note(0, 0, 5);
        var b = Note(1, 0, 5);
        level.Notes.Add(a);
        level.Notes.Add(b);

        for (var beat = 8; beat < 12; beat++) level.Notes.Add(Note(beat, 0, 5));

        var pattern = NotePattern();
        pattern.Grouping = PatternGrouping.AsRun;

        var matches = PatternMatcher.Find(pattern, SeedNotes(a, b), SourceOf(level));

        Assert.Equal(2, matches.Groups.Count);
        Assert.Equal(4, matches.Count);
    }

    // ------------------------------------------------------------- anchors

    private static ChartLevel TwoAnchorsOverDifferentPassages()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Notes.Add(Note(0, 0, 7));
        level.Notes.Add(Note(1, 1, 9));

        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 3, 10));
        level.Notes.Add(Note(9, 4, 8));
        return level;
    }

    /// <summary>
    ///     The owner's standing requirement, expressed as a facet rather than a
    ///     law: with the span ON, two fret-7 anchors over different passages are
    ///     not interchangeable. Turning it OFF is allowed but is exactly the
    ///     over-selection the built-in rule refuses.
    /// </summary>
    [Fact]
    public void MatchAnchorSpan_IsWhatSeparatesTwoAnchorsOnTheSameFret()
    {
        var level = TwoAnchorsOverDifferentPassages();

        var pattern = new MatchPattern
        {
            Name = "anchors", Scope = PatternScope.Anchors, Fret = FretRule.Exact,
            MatchAnchorWidth = true, MatchAnchorSpan = true
        };

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedAnchors(level.Anchors[0]), SourceOf(level)).Count);

        pattern.MatchAnchorSpan = false;
        Assert.Same(level.Anchors[1],
            Assert.Single(PatternMatcher.Find(pattern, SeedAnchors(level.Anchors[0]), SourceOf(level)).Anchors));
    }

    [Fact]
    public void MatchAnchorWidth_SeparatesTwoOtherwiseIdenticalHandPositions()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Anchors.Add(new FretAnchor(At(8), 7, 5));

        var pattern = new MatchPattern
        {
            Name = "anchors", Scope = PatternScope.Anchors, Fret = FretRule.Exact,
            MatchAnchorWidth = true, MatchAnchorSpan = false
        };

        Assert.Equal(0, PatternMatcher.Find(pattern, SeedAnchors(level.Anchors[0]), SourceOf(level)).Count);

        pattern.MatchAnchorWidth = false;
        Assert.Equal(1, PatternMatcher.Find(pattern, SeedAnchors(level.Anchors[0]), SourceOf(level)).Count);
    }

    // ---------------------------------------------------- fingering + naming

    private static Arrangement ArrangementWith(params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        return arrangement;
    }

    /// <summary>
    ///     An unauthored finger is a HOLE, not a value: fingers only have to agree
    ///     where BOTH sides specify one. Comparing "unspecified" against "index"
    ///     as a difference would mean the fingering workflow could never select
    ///     the unfingered twins it exists to fill in.
    /// </summary>
    [Fact]
    public void MatchFingering_TreatsAnUnauthoredFingerAsAHole()
    {
        var fingered = new ChordTemplate
        {
            Id = 0, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 3 }
        };
        var unfingered = new ChordTemplate { Id = 1, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 } };
        var otherFingering = new ChordTemplate
        {
            Id = 2, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 4 }
        };
        var arrangement = ArrangementWith(fingered, unfingered, otherFingering);

        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        seed.TemplateId = 0;
        level.Chords.Add(seed);

        var blank = Strum(4, (0, 5), (1, 7));
        blank.TemplateId = 1;
        level.Chords.Add(blank);

        var different = Strum(8, (0, 5), (1, 7));
        different.TemplateId = 2;
        level.Chords.Add(different);

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        pattern.MatchFingering = true;

        var matches = PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level, arrangement));

        Assert.Same(blank, Assert.Single(matches.Chords));
    }

    [Fact]
    public void HandShapes_MatchOnTheirTemplateShape()
    {
        var template = new ChordTemplate { Id = 0, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 } };
        var other = new ChordTemplate { Id = 1, Frets = new Dictionary<int, int> { [0] = 3, [1] = 5 } };
        var arrangement = ArrangementWith(template, other);

        var level = new ChartLevel();
        var seed = new HandShape { Position = At(0), EndPosition = At(2), TemplateId = 0 };
        level.HandShapes.Add(seed);
        level.HandShapes.Add(new HandShape { Position = At(8), EndPosition = At(10), TemplateId = 0 });
        level.HandShapes.Add(new HandShape { Position = At(16), EndPosition = At(18), TemplateId = 1 });

        var pattern = NotePattern();
        pattern.Scope = PatternScope.HandShapes;

        var matches = PatternMatcher.Find(pattern, new PatternSeed([], [], [seed], []),
            SourceOf(level, arrangement));

        Assert.Same(level.HandShapes[1], Assert.Single(matches.HandShapes));

        // …and transposition reaches the third one, whose shape is the same grip
        // two frets down.
        pattern.Fret = FretRule.Transposable;
        var transposed = PatternMatcher.Find(pattern, new PatternSeed([], [], [seed], []),
            SourceOf(level, arrangement));
        Assert.Equal(2, transposed.HandShapes.Count);
    }

    /// <summary>A hand shape whose template cannot be resolved has NO signature —
    ///     it must not become an empty one that matches every other broken
    ///     shape.</summary>
    [Fact]
    public void AHandShapeWithNoResolvableTemplate_NeverMatches()
    {
        var arrangement = ArrangementWith();
        var level = new ChartLevel();
        var seed = new HandShape { Position = At(0), EndPosition = At(2), TemplateId = 7 };
        level.HandShapes.Add(seed);
        level.HandShapes.Add(new HandShape { Position = At(8), EndPosition = At(10), TemplateId = 9 });

        var pattern = NotePattern();
        pattern.Scope = PatternScope.HandShapes;

        Assert.Equal(0, PatternMatcher.Find(pattern, new PatternSeed([], [], [seed], []),
            SourceOf(level, arrangement)).Count);
    }

    // --------------------------------------------------------- the built-ins

    /// <summary>Every shipped starter pattern normalizes to something that can
    ///     actually match — an empty scope would look configured and select
    ///     nothing forever.</summary>
    [Fact]
    public void EveryBuiltIn_HasAnIdANameAndANonEmptyScope()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var builtIn in MatchPatternCatalog.BuiltIns())
        {
            Assert.False(string.IsNullOrWhiteSpace(builtIn.Id), "a built-in pattern ships with no id");
            Assert.False(string.IsNullOrWhiteSpace(builtIn.Name), $"{builtIn.Id} ships with no name");
            Assert.NotEqual(PatternScope.None, builtIn.Scope);
            Assert.True(ids.Add(builtIn.Id), $"duplicate built-in id {builtIn.Id}");
        }
    }

    /// <summary>
    ///     The starter set ships UNBOUND on purpose: seven patterns times three
    ///     verbs is twenty-one chords, and the keybind registry resolves a
    ///     collision last-registration-wins with nothing but a log line. Binding
    ///     them by default would silently kill shipped editing keys.
    /// </summary>
    [Fact]
    public void NoBuiltIn_ClaimsAKeyOutOfTheBox()
    {
        foreach (var builtIn in MatchPatternCatalog.BuiltIns())
            Assert.False(builtIn.HasAnyKey, $"{builtIn.Id} ships with a keybind");
    }

    [Fact]
    public void TheBuiltInAnchorPatterns_DifferOnlyInTheSpanFacet()
    {
        var builtIns = MatchPatternCatalog.BuiltIns();
        var job = builtIns.Single(p => p.Id == MatchPatternCatalog.SameHandJobId);
        var fretWidth = builtIns.Single(p => p.Id == MatchPatternCatalog.SameHandPositionId);

        // The pair exists so the owner can measure the difference between the
        // shipped anchor rule and the looser one it deliberately refuses.
        Assert.True(job.MatchAnchorSpan);
        Assert.False(fretWidth.MatchAnchorSpan);
        Assert.Equal(job.Fret, fretWidth.Fret);
        Assert.Equal(job.MatchAnchorWidth, fretWidth.MatchAnchorWidth);
    }

    /// <summary>
    ///     A cross-check against the shipped select-like rule for anchors: the
    ///     "same hand job" built-in is meant to BE that rule expressed as data, so
    ///     on a chart where the two disagree one of them is wrong.
    /// </summary>
    [Fact]
    public void TheSameHandJobBuiltIn_AgreesWithTheShippedAnchorRule()
    {
        var level = TwoAnchorsOverDifferentPassages();
        var identical = new FretAnchor(At(16), 7, 4);
        level.Anchors.Add(identical);
        level.Notes.Add(Note(16, 0, 7));
        level.Notes.Add(Note(17, 1, 9));

        var seed = level.Anchors[0];
        var shipped = new SelectLikeService(new FakeAppLogger()).FindLikeAnchors(level, [seed]);

        var builtIn = MatchPatternCatalog.BuiltIns().Single(p => p.Id == MatchPatternCatalog.SameHandJobId);
        var pattern = PatternMatcher.Find(builtIn, SeedAnchors(seed), SourceOf(level)).Anchors;

        Assert.Equal(shipped, pattern);
        Assert.Same(identical, Assert.Single(pattern));
    }
}
