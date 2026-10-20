using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     The facets added in 0.39.1, each proven the same way as the original set:
///     a chart where two things are alike under ONE reading and unalike under
///     another, with the facet as the only thing that decides. A facet that cannot
///     be shown to change the answer is a control with no meaning.
/// </summary>
public sealed class PatternMatcherFacetTests
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

    private static Arrangement ArrangementWith(params ChordTemplate[] templates)
    {
        var arrangement = new Arrangement();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        return arrangement;
    }

    // ------------------------------------------------------------- the shape

    /// <summary>
    ///     <see cref="StringRule.Relative" /> is the string rule's twin of
    ///     transposition: the same grip moved ACROSS the neck — a power chord on
    ///     6/5 and the same one on 5/4 — is one hand doing one thing.
    /// </summary>
    [Fact]
    public void StringRelative_FindsTheSameGripOnOtherStrings()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (2, 5), (3, 7))); // the same grip, two strings over
        level.Chords.Add(Strum(8, (2, 5), (4, 7))); // the same frets, a DIFFERENT gap

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        Assert.Equal(0, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Count);

        pattern.Strings = StringRule.Relative;
        var matches = PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level));

        // The string INTERVAL is the key — a shape with a different gap between
        // its strings is a different grip, not a moved one.
        Assert.Same(level.Chords[1], Assert.Single(matches.Chords));
    }

    // ------------------------------------------------------------ the timing

    [Fact]
    public void SustainSameKind_SeparatesStruckFromHeldWithoutDemandingOneLength()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        seed.Sustain = new FractionalPosition(2);
        level.Notes.Add(seed);

        var alsoHeld = Note(4, 0, 5);
        alsoHeld.Sustain = new FractionalPosition(4); // held, but longer
        level.Notes.Add(alsoHeld);

        level.Notes.Add(Note(8, 0, 5)); // struck, no sustain at all

        var pattern = NotePattern();

        pattern.Sustain = SustainRule.SameKind;
        Assert.Same(alsoHeld, Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Notes));

        pattern.Sustain = SustainRule.SameLength;
        Assert.Equal(0, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);

        pattern.Sustain = SustainRule.Ignore;
        Assert.Equal(2, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);
    }

    /// <summary>
    ///     A lick restated at half or double time is the same lick — an exact
    ///     spacing comparison can never find it, and ignoring rhythm entirely
    ///     finds far too much.
    /// </summary>
    [Fact]
    public void RhythmProportional_FindsTheSameLickAtHalfTime()
    {
        var level = new ChartLevel();
        var a = Note(0, 0, 5);
        var b = Note(1, 0, 7);
        var c = Note(2, 0, 9);
        level.Notes.Add(a);
        level.Notes.Add(b);
        level.Notes.Add(c);

        // The same three notes at half speed: gaps of 2 beats instead of 1.
        level.Notes.Add(Note(8, 0, 5));
        level.Notes.Add(Note(10, 0, 7));
        level.Notes.Add(Note(12, 0, 9));

        // …and the same three notes at an arbitrary, NON-proportional spacing.
        level.Notes.Add(Note(20, 0, 5));
        level.Notes.Add(Note(21, 0, 7));
        level.Notes.Add(Note(25, 0, 9));

        var pattern = NotePattern();
        pattern.Grouping = PatternGrouping.AsRun;

        pattern.Rhythm = RhythmRule.Exact;
        Assert.Empty(PatternMatcher.Find(pattern, SeedNotes(a, b, c), SourceOf(level)).Groups);

        pattern.Rhythm = RhythmRule.Proportional;
        var groups = PatternMatcher.Find(pattern, SeedNotes(a, b, c), SourceOf(level)).Groups;

        // The half-time restatement ONLY: one constant ratio, not any spacing.
        Assert.Single(groups);
        Assert.Equal(At(8), groups[0].Position);
    }

    [Fact]
    public void MatchBeatOffset_SeparatesTheDownbeatFromTheOffbeat()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(new GuitarNote { Position = At(4), StringIndex = 0, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = At(8, 1, 2), StringIndex = 0, Fret = 5 });

        var pattern = NotePattern();
        Assert.Equal(2, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Count);

        pattern.MatchBeatOffset = true;
        Assert.Same(level.Notes[1],
            Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level)).Notes));
    }

    /// <summary>"The same grip, but only around here" — the companion to
    ///     transposition, which otherwise reaches the whole neck.</summary>
    [Fact]
    public void MaxFretDistance_KeepsATransposablePatternNearItsSeed()
    {
        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        level.Chords.Add(seed);
        level.Chords.Add(Strum(4, (0, 7), (1, 9)));   // two frets up
        level.Chords.Add(Strum(8, (0, 17), (1, 19))); // twelve frets up

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        pattern.Fret = FretRule.Transposable;
        Assert.Equal(2, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Count);

        pattern.MaxFretDistance = 3;
        Assert.Same(level.Chords[1],
            Assert.Single(PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level)).Chords));
    }

    [Fact]
    public void MatchArpeggio_SeparatesAnArpeggioFromAPlainStrumOfTheSameFrets()
    {
        var plain = new ChordTemplate { Id = 0, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 } };
        var arpeggio = new ChordTemplate
        {
            Id = 1, Arpeggio = true, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 }
        };
        var arrangement = ArrangementWith(plain, arpeggio);

        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        seed.TemplateId = 0;
        level.Chords.Add(seed);

        var arped = Strum(4, (0, 5), (1, 7));
        arped.TemplateId = 1;
        level.Chords.Add(arped);

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        Assert.Equal(1, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level, arrangement)).Count);

        // The flag sets ChordMask.Arpeggio on export and draws purple brackets
        // instead of a hand-shape bar, so it is a real authored difference.
        pattern.MatchArpeggio = true;
        Assert.Equal(0, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level, arrangement)).Count);
    }

    // ------------------------------------------------------------- the range

    private static Arrangement WithSections(params (int Beat, string Section)[] markers)
    {
        var arrangement = new Arrangement();
        foreach (var (beat, section) in markers)
            arrangement.EventPoints.Add(new EventPoint { Position = At(beat), Section = section });
        return arrangement;
    }

    /// <summary>
    ///     "Every chorus and nothing else" is a charting request, not a search
    ///     refinement — it is the difference between fixing one passage and
    ///     rewriting the whole chart.
    /// </summary>
    [Fact]
    public void RangeSameSection_LooksOnlyInsideLikeNamedSections()
    {
        var arrangement = WithSections((0, "chorus"), (8, "verse"), (16, "chorus"));

        var level = new ChartLevel();
        var seed = Note(1, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(9, 0, 5));  // in the verse
        level.Notes.Add(Note(17, 0, 5)); // in the second chorus

        var pattern = NotePattern();
        Assert.Equal(2, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level, arrangement)).Count);

        pattern.Range = PatternRange.SameSection;
        Assert.Same(level.Notes[2],
            Assert.Single(PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level, arrangement)).Notes));
    }

    /// <summary>A chart with no structure authored yet must not silently match
    ///     NOTHING — an absent vocabulary means no restriction, not a refusal.</summary>
    [Fact]
    public void RangeSameSection_WithNoSectionsAuthored_IsANoOp()
    {
        var level = new ChartLevel();
        var seed = Note(1, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(9, 0, 5));

        var pattern = NotePattern();
        pattern.Range = PatternRange.SameSection;

        Assert.Equal(1, PatternMatcher.Find(pattern, SeedNotes(seed), SourceOf(level, new Arrangement())).Count);
    }

    // -------------------------------------------------------- the companions

    /// <summary>
    ///     A chord, the hand shape drawn over it and the FHP holding it are ONE
    ///     authored decision. Selecting the chord alone and re-fingering it leaves
    ///     the other two describing a shape that is no longer there.
    /// </summary>
    [Fact]
    public void Companions_BringTheHandShapeAndTheAnchorAlong()
    {
        var template = new ChordTemplate { Id = 0, Frets = new Dictionary<int, int> { [0] = 5, [1] = 7 } };
        var arrangement = ArrangementWith(template);

        var level = new ChartLevel();
        var seed = Strum(0, (0, 5), (1, 7));
        seed.TemplateId = 0;
        level.Chords.Add(seed);
        level.HandShapes.Add(new HandShape { Position = At(0), EndPosition = At(2), TemplateId = 0 });
        level.Anchors.Add(new FretAnchor(At(0), 5, 4));

        var twin = Strum(8, (0, 5), (1, 7));
        twin.TemplateId = 0;
        level.Chords.Add(twin);
        var twinShape = new HandShape { Position = At(8), EndPosition = At(10), TemplateId = 0 };
        level.HandShapes.Add(twinShape);
        var twinAnchor = new FretAnchor(At(8), 5, 4);
        level.Anchors.Add(twinAnchor);

        var pattern = NotePattern();
        pattern.Scope = PatternScope.Chords;
        Assert.Equal(1, PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level, arrangement)).Count);

        pattern.Companions = PatternCompanions.CoveringHandShape | PatternCompanions.GoverningAnchor;
        var matches = PatternMatcher.Find(pattern, SeedChords(seed), SourceOf(level, arrangement));

        Assert.Same(twin, Assert.Single(matches.Chords));
        Assert.Same(twinShape, Assert.Single(matches.HandShapes));
        Assert.Same(twinAnchor, Assert.Single(matches.Anchors));
        // Still ONE occurrence — companions travel WITH a match, they are not
        // matches of their own, or the navigate verb would stop three times.
        Assert.Single(matches.Groups);
    }

    /// <summary>A companion that is already SELECTED is not re-reported —
    ///     "matches never include the seeds" has to survive the expansion.</summary>
    [Fact]
    public void Companions_NeverReturnASeedElement()
    {
        var level = new ChartLevel();
        var seed = Note(0, 0, 5);
        level.Notes.Add(seed);
        level.Notes.Add(Note(8, 0, 5));

        var seedAnchor = new FretAnchor(At(0), 5, 4);
        level.Anchors.Add(seedAnchor);
        level.Anchors.Add(new FretAnchor(At(8), 5, 4));

        var pattern = NotePattern();
        pattern.Companions = PatternCompanions.GoverningAnchor;

        var matches = PatternMatcher.Find(pattern,
            new PatternSeed([seed], [], [], [seedAnchor]), SourceOf(level));

        Assert.DoesNotContain(seedAnchor, matches.Anchors);
    }

    /// <summary>The inverse direction: a pattern seeded from an ANCHOR pulls in
    ///     everything played under the matching hand positions, which is what makes
    ///     "fix every anchor like this one" an edit rather than a selection.</summary>
    [Fact]
    public void CompanionSoundsInside_PullsInWhatTheAnchorGoverns()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Notes.Add(Note(0, 0, 7));
        level.Notes.Add(Note(1, 1, 9));

        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 0, 7));
        level.Notes.Add(Note(9, 1, 9));

        var pattern = new MatchPattern
        {
            Name = "anchors", Scope = PatternScope.Anchors, Fret = FretRule.Exact,
            MatchAnchorWidth = true, MatchAnchorSpan = true,
            Companions = PatternCompanions.SoundsInside
        };

        var matches = PatternMatcher.Find(pattern, SeedAnchors(level.Anchors[0]), SourceOf(level));

        Assert.Same(level.Anchors[1], Assert.Single(matches.Anchors));
        Assert.Equal(2, matches.Notes.Count);
        Assert.All(matches.Notes, n => Assert.True(n.Position >= At(8)));
    }

    // ------------------------------------------------- 0.39.0 compatibility

    /// <summary>
    ///     Three facets shipped as booleans and became tri-states one build later.
    ///     A pattern authored against 0.39.0 has to keep MEANING what it said —
    ///     most of all "Same bend anywhere", whose entire point is that it ignores
    ///     the strings.
    /// </summary>
    [Fact]
    public void TheLegacyBooleans_FoldIntoTheirTriStates()
    {
        var legacy = new MatchPattern
        {
            Id = "legacy", Name = "legacy",
            MatchStrings = false, MatchRhythm = false, MatchSustain = true
        };

        var normalized = legacy.Normalized();

        Assert.Equal(StringRule.Ignore, normalized.Strings);
        Assert.Equal(RhythmRule.Ignore, normalized.Rhythm);
        Assert.Equal(SustainRule.SameLength, normalized.Sustain);

        // …and they are dropped, so this build never writes them again.
        Assert.Null(normalized.MatchStrings);
        Assert.Null(normalized.MatchRhythm);
        Assert.Null(normalized.MatchSustain);
    }

    /// <summary>An ABSENT legacy boolean (every config this build writes) must not
    ///     touch the tri-state — otherwise loading would reset it on every save.</summary>
    [Fact]
    public void AnAbsentLegacyBoolean_LeavesTheTriStateAlone()
    {
        var pattern = new MatchPattern
        {
            Id = "modern", Name = "modern",
            Strings = StringRule.Relative, Rhythm = RhythmRule.Proportional, Sustain = SustainRule.SameKind
        };

        var normalized = pattern.Normalized();

        Assert.Equal(StringRule.Relative, normalized.Strings);
        Assert.Equal(RhythmRule.Proportional, normalized.Rhythm);
        Assert.Equal(SustainRule.SameKind, normalized.Sustain);
    }

    // ------------------------------------------------------------ the summary

    /// <summary>
    ///     The rail's one-line description. It is not decoration: comparing
    ///     definitions is the job, and comparing means reading several at once.
    /// </summary>
    [Fact]
    public void TheSummary_NamesTheFacetsThatAreActuallySet()
    {
        var summary = PatternSummary.Describe(new MatchPattern
        {
            Scope = PatternScope.Chords,
            Fret = FretRule.Transposable,
            Strings = StringRule.Relative,
            MatchFingering = true,
            Range = PatternRange.SameSection,
            MaxFretDistance = 4,
            Companions = PatternCompanions.CoveringHandShape | PatternCompanions.GoverningAnchor
        }, FakeLocalization.English());

        Assert.Contains("Chords", summary);
        Assert.Contains("any position", summary);
        Assert.Contains("any strings", summary);
        Assert.Contains("same fingering", summary);
        Assert.Contains("this section only", summary);
        Assert.Contains("within 4 frets", summary);
        Assert.Contains("hand shape", summary);
        Assert.Contains("FHP", summary);
    }

    /// <summary>Two patterns that differ in ONE facet must not read identically —
    ///     a summary that cannot tell them apart is worse than none.</summary>
    [Fact]
    public void TheSummary_DistinguishesTheTwoAnchorBuiltIns()
    {
        var builtIns = MatchPatternCatalog.BuiltIns();
        var job = PatternSummary.Describe(builtIns.Single(p => p.Id == MatchPatternCatalog.SameHandJobId), FakeLocalization.English());
        var loose = PatternSummary.Describe(builtIns.Single(p => p.Id == MatchPatternCatalog.SameHandPositionId), FakeLocalization.English());

        Assert.NotEqual(job, loose);
        Assert.Contains("same span", job);
        Assert.Contains("ANY span", loose);
    }

    [Fact]
    public void EveryBuiltIn_HasANonEmptySummary()
    {
        foreach (var builtIn in MatchPatternCatalog.BuiltIns())
        {
            Assert.False(string.IsNullOrWhiteSpace(PatternSummary.Describe(builtIn, FakeLocalization.English())),
                $"{builtIn.Id} describes as nothing");
        }
    }
}
