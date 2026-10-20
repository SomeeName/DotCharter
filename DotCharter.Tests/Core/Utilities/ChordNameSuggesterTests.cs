namespace DotCharter.Tests.Core.Utilities;

public class ChordNameSuggesterTests
{
    private static readonly Arrangement StandardGuitar = new()
    {
        StringCount = 6,
        Tuning = ["E", "A", "D", "G", "B", "E"]
    };

    [Fact]
    public void SuggestChordNames_OpenCMajor_ReturnsCMajorFirst()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(StandardGuitar,
            new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 });

        Assert.NotEmpty(suggestions);
        Assert.Equal("C", suggestions[0]);
    }

    [Fact]
    public void SuggestChordNames_OpenAMinor_PrefersAMinorButIncludesSlashAlternatives()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(StandardGuitar,
            new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 });

        Assert.Contains("Amin", suggestions);
        Assert.Equal("Amin", suggestions[0]);
        Assert.Contains("C6/A", suggestions);
    }

    [Fact]
    public void SuggestChordNames_FirstInversionDChord_IncludesSlashName()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(StandardGuitar,
            new Dictionary<int, int> { [0] = 2, [2] = 0, [3] = 2, [4] = 3, [5] = 2 });

        Assert.Contains("D/F#", suggestions);
    }

    [Fact]
    public void SuggestChordNames_BFlatMajor_IncludesEnharmonicSpellings()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(StandardGuitar,
            new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 3, [3] = 3, [4] = 3, [5] = 1 });

        Assert.Contains("A#", suggestions);
        Assert.Contains("Bb", suggestions);
    }

    // --- Inversion / slash-name ranking. The corpus ground truth (and the
    // RS2014/EOF convention) names an inversion by its PLAIN chord name far more
    // often than the slash form, so the plain name outranks the slash spelling
    // when the matched root is not the bass; the slash form stays available as a
    // lower-ranked alternative.
    //
    // These four shapes are all TWO-STRING power chords, so since 0.41 the
    // automatic namer leaves them blank by default (see the double-stop block at
    // the bottom of this file). They ask about RANKING, not about the gate, so
    // they opt naming back on explicitly — otherwise the ranking they exist to
    // pin would stop being exercised at all and the tests would pass vacuously. ---

    [Fact]
    public void SuggestFirstChordName_BEA_NamesSeventhSusFourOnTheBass()
    {
        // A(2) D(2) G(2) => B, E, A with B in the bass. Root B needs [5,10],
        // which only matches once 7sus4 exists; previously root E won with "Esus".
        var frets = new Dictionary<int, int> { [1] = 2, [2] = 2, [3] = 2 };

        Assert.Equal("B7sus4", ChordNameSuggester.SuggestFirstChordName(StandardGuitar, frets));
    }

    [Fact]
    public void SuggestFirstChordName_GPowerChordOverD_PrefersPlainName()
    {
        // D(0) G(0) => D, G with D in the bass: G5 in second inversion. The plain
        // "G5" wins; "G5/D" is only an alternative.
        var frets = new Dictionary<int, int> { [2] = 0, [3] = 0 };

        Assert.Equal("G5", ChordNameSuggester.SuggestFirstChordName(
            StandardGuitar, frets, DoubleStopNameOptions.NameEverything));
    }

    [Fact]
    public void SuggestChordNames_GPowerChordOverD_StillOffersTheSlashNameAsAnAlternative()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(StandardGuitar,
            new Dictionary<int, int> { [2] = 0, [3] = 0 });

        Assert.Equal("G5", suggestions[0]);
        Assert.Contains("G5/D", suggestions);
    }

    [Fact]
    public void SuggestFirstChordName_APowerChordOverE_PrefersPlainName()
    {
        // A(7) D(7) => E, A with E in the bass.
        var frets = new Dictionary<int, int> { [1] = 7, [2] = 7 };

        Assert.Equal("A5", ChordNameSuggester.SuggestFirstChordName(
            StandardGuitar, frets, DoubleStopNameOptions.NameEverything));
    }

    [Fact]
    public void SuggestFirstChordName_RootPositionPowerChords_KeepPlainNames()
    {
        // A(3) D(5) => C, G with C in the bass.
        Assert.Equal("C5", ChordNameSuggester.SuggestFirstChordName(StandardGuitar,
            new Dictionary<int, int> { [1] = 3, [2] = 5 }, DoubleStopNameOptions.NameEverything));

        // D(3) G(5) => F, C with F in the bass.
        Assert.Equal("F5", ChordNameSuggester.SuggestFirstChordName(StandardGuitar,
            new Dictionary<int, int> { [2] = 3, [3] = 5 }, DoubleStopNameOptions.NameEverything));
    }

    // --- New pattern-table entries ---

    [Fact]
    public void SuggestFirstChordName_SeventhSusFourWithFifth_IsNamed()
    {
        // A(3) D(3) G(3) B(8) => C, F, Bb, G: root C with [5,7,10].
        var frets = new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3, [4] = 8 };

        Assert.Equal("C7sus4", ChordNameSuggester.SuggestFirstChordName(StandardGuitar, frets));
    }

    [Fact]
    public void SuggestFirstChordName_MinorSeventhWithFifth_IsNamed()
    {
        // A(0) D(2) G(0) B(1) => A, E, G, C: root A with [3,7,10].
        // Before the table gained [3,7,10] this voicing named to "".
        var frets = new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 0, [4] = 1 };

        Assert.Equal("Amin7", ChordNameSuggester.SuggestFirstChordName(StandardGuitar, frets));
    }

    // --- Double stops -------------------------------------------------------
    //
    // Two pitches do not determine a chord, so a detected name for a two-string
    // shape is a guess wearing a confident label. EOF gates the same case
    // (eof_dont_auto_name_double_stops, EOF_SRC/tuning.c:425) and rscharter never
    // auto-names at all; both render a blank name as nothing.

    [Fact]
    public void ADoubleStopIsNotAutoNamed()
    {
        // G(4) B(3) => B, D: a third. The matcher would happily call this
        // something; two notes cannot say which.
        var frets = new Dictionary<int, int> { [3] = 4, [4] = 3 };

        Assert.Equal("", ChordNameSuggester.SuggestFirstChordName(StandardGuitar, frets));
    }

    /// <summary>
    ///     The gate is on the AUTOMATIC namer only. Naming a double stop by hand
    ///     has to stay possible, or the charter loses a label they may want — the
    ///     point is that the app should not invent one.
    /// </summary>
    [Fact]
    public void TheCandidateListIsNeverGated()
    {
        var suggestions = ChordNameSuggester.SuggestChordNames(
            StandardGuitar, new Dictionary<int, int> { [2] = 0, [3] = 0 });

        Assert.Equal("G5", suggestions[0]);
    }

    [Fact]
    public void ByDefaultAPowerChordIsSuppressedToo_MatchingEof()
    {
        Assert.Equal("", ChordNameSuggester.SuggestFirstChordName(
            StandardGuitar, new Dictionary<int, int> { [2] = 0, [3] = 0 }));
    }

    [Fact]
    public void PowerChordsCanBeExemptedWithoutNamingEveryDoubleStop()
    {
        var options = new DoubleStopNameOptions(Suppress: true, KeepPowerChordNames: true);

        // D(0) G(0) => D, G — a fourth, i.e. an inverted fifth. Still a power chord.
        Assert.Equal("G5", ChordNameSuggester.SuggestFirstChordName(
            StandardGuitar, new Dictionary<int, int> { [2] = 0, [3] = 0 }, options));

        // G(4) B(3) => B, D — a third. Not a power chord, so still suppressed.
        Assert.Equal("", ChordNameSuggester.SuggestFirstChordName(
            StandardGuitar, new Dictionary<int, int> { [3] = 4, [4] = 3 }, options));
    }

    [Fact]
    public void AThreeStringShapeIsUntouchedByTheGate()
    {
        var frets = new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3, [4] = 8 };

        Assert.Equal("C7sus4", ChordNameSuggester.SuggestFirstChordName(StandardGuitar, frets));
    }

    /// <summary>An unused string is negative, and must not be counted as sounding.</summary>
    [Fact]
    public void UnusedStringsDoNotCountTowardsTheTwo()
    {
        var frets = new Dictionary<int, int> { [0] = -1, [1] = -1, [2] = 0, [3] = 0, [4] = -1, [5] = -1 };

        Assert.True(ChordNameSuggester.IsDoubleStop(frets));
    }

    [Fact]
    public void IsDoubleStop_IsExactlyTwo()
    {
        Assert.False(ChordNameSuggester.IsDoubleStop(new Dictionary<int, int> { [2] = 0 }));
        Assert.True(ChordNameSuggester.IsDoubleStop(new Dictionary<int, int> { [2] = 0, [3] = 0 }));
        Assert.False(ChordNameSuggester.IsDoubleStop(
            new Dictionary<int, int> { [2] = 0, [3] = 0, [4] = 0 }));
    }

    /// <summary>
    ///     Naming does NOT require the two strings to be adjacent — that is the
    ///     separate arrangement-property predicate (EOF's
    ///     <c>eof_pro_guitar_note_is_double_stop</c> /
    ///     <c>RsTechniques.IsDoubleStopAdjacentStrings</c>). Two pitches are two
    ///     pitches wherever they sit.
    /// </summary>
    [Fact]
    public void NonAdjacentStringsAreStillADoubleStopForNaming() => Assert.True(ChordNameSuggester.IsDoubleStop(new Dictionary<int, int> { [0] = 0, [5] = 0 }));

    /// <summary>An octave is one note played twice, not a root and a fifth.</summary>
    [Fact]
    public void AnOctaveIsNotAPowerChord()
    {
        // E(0) low and D(2) => E2 and E3.
        Assert.False(ChordNameSuggester.IsPowerChord(
            StandardGuitar.Tuning, new Dictionary<int, int> { [0] = 0, [2] = 2 }));
    }

    /// <summary>
    ///     The interval is judged at SOUNDING pitch, so a capo cannot turn a power
    ///     chord into something else or vice versa.
    /// </summary>
    [Fact]
    public void PowerChordDetectionFoldsInTheCapo()
    {
        var frets = new Dictionary<int, int> { [2] = 0, [3] = 0 };

        Assert.True(ChordNameSuggester.IsPowerChord(StandardGuitar.Tuning, frets));
        Assert.True(ChordNameSuggester.IsPowerChord(StandardGuitar.Tuning, frets, capo: 3));
    }

    [Theory]
    [InlineData("Bb", 2, "C")]
    [InlineData("A#", -1, "A")]
    [InlineData("F#", 12, "F#")]
    [InlineData("?", 1, "?")]
    public void ChangeToneName_MatchesRsCharterBehaviour(string tone, int change, string expected)
    {
        var actual = ChordNameSuggester.ChangeToneName(tone, change);
        Assert.Equal(expected, actual);
    }

    // --- Capo ---------------------------------------------------------------
    //
    // Fret semantics in this domain model: a fret is ABSOLUTE on the un-capoed
    // neck and fret 0 means "open", which a capo stops at the capo fret. So the
    // sounding offset of a string is max(fret, capo) — the same derivation as
    // RefretPlanner.SoundingMidi and LivePitchAcceptance.SoundingMidi. Only OPEN
    // strings are transposed by a capo; fretted notes above it are untouched.

    private static Arrangement Capoed(int capo)
    {
        return new Arrangement
        {
            StringCount = 6,
            Tuning = ["E", "A", "D", "G", "B", "E"],
            Capo = capo
        };
    }

    [Fact]
    public void SuggestChordNames_Capo0_IsAStrictNoOp()
    {
        // Every historical expectation must survive verbatim with Capo = 0 —
        // the overwhelming majority of charts. Capo 0 must not shift anything.
        var noCapo = Capoed(0);

        Assert.Equal("C", ChordNameSuggester.SuggestFirstChordName(noCapo,
            new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }));
        Assert.Equal("Amin", ChordNameSuggester.SuggestFirstChordName(noCapo,
            new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 }));
        // Two strings, so the double-stop gate would blank it — the claim under
        // test is that CAPO 0 changes nothing, so naming is switched on to keep
        // the assertion about the capo rather than about the gate.
        Assert.Equal("G5", ChordNameSuggester.SuggestFirstChordName(noCapo,
            new Dictionary<int, int> { [2] = 0, [3] = 0 }, DoubleStopNameOptions.NameEverything));
        Assert.Equal("B7sus4", ChordNameSuggester.SuggestFirstChordName(noCapo,
            new Dictionary<int, int> { [1] = 2, [2] = 2, [3] = 2 }));
    }

    [Fact]
    public void SuggestChordNames_Capo0_MatchesTheExplicitNoCapoOverload()
    {
        // Belt and braces: the Arrangement overload at Capo 0 and the raw
        // tuning overload with no capo argument must agree element for element.
        string[] tuning = ["E", "A", "D", "G", "B", "E"];
        var shapes = new[]
        {
            new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 },
            new Dictionary<int, int> { [0] = 2, [2] = 0, [3] = 2, [4] = 3, [5] = 2 },
            new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 },
            new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 0, [4] = 1 },
            new Dictionary<int, int> { [2] = 0, [3] = 0 }
        };

        foreach (var shape in shapes)
        {
            Assert.Equal(
                ChordNameSuggester.SuggestChordNames(tuning, shape),
                ChordNameSuggester.SuggestChordNames(Capoed(0), shape));
        }
    }

    [Fact]
    public void SuggestFirstChordName_Capo2_OpenCShape_SoundsAsD()
    {
        // The classic capo case. With a capo on fret 2 the player fingers the
        // familiar open C shape, which this model stores at ABSOLUTE frets:
        // the "3rd fret" of the capoed neck is fret 5, and the strings played
        // "open" against the capo are fret 0 (sounding at fret 2).
        // What the player HEARS is D major, and that is what it must be named.
        var frets = new Dictionary<int, int> { [1] = 5, [2] = 4, [3] = 0, [4] = 3, [5] = 0 };

        Assert.Equal("D", ChordNameSuggester.SuggestFirstChordName(Capoed(2), frets));
    }

    [Fact]
    public void SuggestFirstChordName_Capo2_OpenCShape_IsNotWhatTheUncapoedReadingGives()
    {
        // Proves the capo is doing the work: the identical fret dictionary read
        // WITHOUT a capo leaves the two open strings sounding G and E, which is
        // not a D chord at all. This was the shipped bug — every capoed chart
        // named its chords at the wrong pitch.
        var frets = new Dictionary<int, int> { [1] = 5, [2] = 4, [3] = 0, [4] = 3, [5] = 0 };

        Assert.NotEqual(
            ChordNameSuggester.SuggestFirstChordName(Capoed(0), frets),
            ChordNameSuggester.SuggestFirstChordName(Capoed(2), frets));
    }

    [Fact]
    public void SuggestFirstChordName_Capo2_OpenShapeMatchesTheSameSoundingShapeWithoutACapo()
    {
        // The strongest statement of correctness: a capoed shape must name
        // exactly like the shape that produces the same sounding pitches on an
        // uncapoed neck. Capo 2 with open strings == those strings fretted at 2.
        var capoed = new Dictionary<int, int> { [1] = 5, [2] = 4, [3] = 0, [4] = 3, [5] = 0 };
        var equivalent = new Dictionary<int, int> { [1] = 5, [2] = 4, [3] = 2, [4] = 3, [5] = 2 };

        Assert.Equal(
            ChordNameSuggester.SuggestChordNames(Capoed(0), equivalent),
            ChordNameSuggester.SuggestChordNames(Capoed(2), capoed));
    }

    [Fact]
    public void SuggestFirstChordName_CapoedBarre_IsUnaffectedByTheCapo()
    {
        // A barre chord is entirely fretted ABOVE the capo, so the capo touches
        // none of its strings — an E-shape barre at fret 5 is A major with or
        // without a capo behind it. ("Capo added to fretted strings only" is
        // EOF's relative→absolute import rule, NOT a pitch shift.)
        var barre = new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 };

        Assert.Equal("A", ChordNameSuggester.SuggestFirstChordName(Capoed(0), barre));
        Assert.Equal("A", ChordNameSuggester.SuggestFirstChordName(Capoed(2), barre));
        Assert.Equal(
            ChordNameSuggester.SuggestChordNames(Capoed(0), barre),
            ChordNameSuggester.SuggestChordNames(Capoed(4), barre));
    }

    [Fact]
    public void SuggestChordNames_UnusedStringsStayUnusedUnderACapo()
    {
        // A negative fret means "string not played" and must never be folded up
        // to the capo — otherwise a capoed power chord grows phantom notes.
        var frets = new Dictionary<int, int> { [0] = -1, [1] = 5, [2] = 7, [3] = -1 };

        var capoed = ChordNameSuggester.SuggestChordNames(Capoed(2), frets);
        var uncapoed = ChordNameSuggester.SuggestChordNames(Capoed(0), frets);

        // Both strings are fretted above the capo, so the name is capo-independent.
        Assert.Equal(uncapoed, capoed);
        Assert.Equal("D5", capoed[0]);
    }

    // ─── A tuning entry that is not a note name ───────────────────────────────
    // Arrangement.Tuning is a bare string[] filled from rscp/RS-XML/GP input with
    // no validation in Core, so "", "x" or a localized note name can reach here.

    [Fact]
    public void SuggestChordNames_UnparseableTuningEntry_DoesNotMisnameAShapeThatAvoidsIt()
    {
        // Skipping the bad entry shifted every higher string's open pitch down one
        // slot AND left the list one short, so the top string was dropped too —
        // the suggester then returned a confidently wrong name rather than none.
        var frets = new Dictionary<int, int> { [3] = 0, [4] = 0, [5] = 0 };

        var clean = ChordNameSuggester.SuggestChordNames(["E", "A", "D", "G", "B", "E"], frets);
        var corrupt = ChordNameSuggester.SuggestChordNames(["E", "", "D", "G", "B", "E"], frets);

        Assert.NotEmpty(clean);
        Assert.Equal(clean, corrupt);
    }

    [Fact]
    public void SuggestChordNames_UnparseableStringInsideTheShape_DropsOnlyThatString()
    {
        var corrupt = ChordNameSuggester.SuggestChordNames(
            ["E", "N/A", "D", "G", "B", "E"],
            new Dictionary<int, int> { [1] = 0, [3] = 0, [4] = 0, [5] = 0 });

        var withoutIt = ChordNameSuggester.SuggestChordNames(
            ["E", "A", "D", "G", "B", "E"],
            new Dictionary<int, int> { [3] = 0, [4] = 0, [5] = 0 });

        Assert.NotEmpty(withoutIt);
        Assert.Equal(withoutIt, corrupt);
    }
}
