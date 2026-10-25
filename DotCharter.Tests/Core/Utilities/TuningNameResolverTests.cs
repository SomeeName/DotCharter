namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     Coverage for the pure <see cref="TuningNameResolver" />: every common tuning
///     the owner asked to see by name resolves from BOTH input forms (per-string
///     semitone offsets and per-string note letters), the two forms agree, and any
///     unrecognised tuning / bad input returns <c>null</c> so callers keep showing
///     the raw note letters. The note-letter cases use the SAME spelling
///     <see cref="TuningUtilities.GetTuningNames" /> emits — the exact string Song
///     Select stores per song — so this also locks display↔storage agreement.
/// </summary>
public sealed class TuningNameResolverTests
{
    // (stringCount, per-string offsets low→high relative to standard, expected name).
    public static TheoryData<int, int[], string> KnownTunings => new()
    {
        // ---- 6-string standards ----
        { 6, new[] { 1, 1, 1, 1, 1, 1 }, "F Standard" },
        { 6, new[] { 0, 0, 0, 0, 0, 0 }, "E Standard" },
        { 6, new[] { -1, -1, -1, -1, -1, -1 }, "Eb Standard" },
        { 6, new[] { -2, -2, -2, -2, -2, -2 }, "D Standard" },
        { 6, new[] { -3, -3, -3, -3, -3, -3 }, "Db Standard" },
        { 6, new[] { -4, -4, -4, -4, -4, -4 }, "C Standard" },
        { 6, new[] { -5, -5, -5, -5, -5, -5 }, "B Standard" },
        { 6, new[] { -6, -6, -6, -6, -6, -6 }, "Bb Standard" },
        { 6, new[] { -7, -7, -7, -7, -7, -7 }, "A Standard" },
        // ---- 6-string drops ----
        { 6, new[] { -2, 0, 0, 0, 0, 0 }, "Drop D" },
        { 6, new[] { -3, -1, -1, -1, -1, -1 }, "Drop C#" },
        { 6, new[] { -4, -2, -2, -2, -2, -2 }, "Drop C" },
        { 6, new[] { -5, -3, -3, -3, -3, -3 }, "Drop B" },
        { 6, new[] { -6, -4, -4, -4, -4, -4 }, "Drop A#" },
        { 6, new[] { -7, -5, -5, -5, -5, -5 }, "Drop A" },
        { 6, new[] { -8, -6, -6, -6, -6, -6 }, "Drop G#" },
        { 6, new[] { -9, -7, -7, -7, -7, -7 }, "Drop G" },
        { 6, new[] { -10, -8, -8, -8, -8, -8 }, "Drop F#" },
        { 6, new[] { -11, -9, -9, -9, -9, -9 }, "Drop F" },
        // ---- 6-string alternate / open ----
        { 6, new[] { -1, 1, 1, 0, -1, -1 }, "Open Eb" },
        { 6, new[] { -2, 0, 0, -2, -2, -2 }, "Open D Minor" },
        { 6, new[] { -2, 0, 0, 0, -2, -2 }, "DADGAD" },
        { 6, new[] { -2, 0, 0, 0, 0, -2 }, "Double Drop D" },
        { 6, new[] { -2, 0, 0, -1, -2, -2 }, "Open D" },
        { 6, new[] { 0, 2, 2, 1, 0, 0 }, "Open E" },
        { 6, new[] { -2, -2, 0, 0, 0, -2 }, "Open G" },
        { 6, new[] { 0, 0, 2, 2, 2, 0 }, "Open A" },
        { 6, new[] { -4, -2, -2, 0, 1, 0 }, "Open C" },
        // ---- 4-string bass ----
        { 4, new[] { 0, 0, 0, 0 }, "E Standard" },
        { 4, new[] { -1, -1, -1, -1 }, "Eb Standard" },
        { 4, new[] { -2, -2, -2, -2 }, "D Standard" },
        { 4, new[] { -4, -4, -4, -4 }, "C Standard" },
        { 4, new[] { -5, -5, -5, -5 }, "B Standard" },
        { 4, new[] { -2, 0, 0, 0 }, "Drop D" },
        { 4, new[] { -3, -1, -1, -1 }, "Drop C#" },
        { 4, new[] { -4, -2, -2, -2 }, "Drop C" },
        { 4, new[] { -5, -3, -3, -3 }, "Drop B" },
        { 4, new[] { -7, -5, -5, -5 }, "Drop A" }
    };

    [Theory]
    [MemberData(nameof(KnownTunings))]
    public void ResolvesKnownTuning_FromBothOffsetsAndNotes_WhichAgree(int stringCount, int[] offsets, string expected)
    {
        // Form 1 — the raw offsets (the canonical form on the pre-play screen).
        Assert.Equal(expected, TuningNameResolver.ResolveFromOffsets(stringCount, offsets));

        // Form 2 — the note letters the app renders (GetTuningNames spelling, the
        // exact per-song string Song Select stores). List and space-joined forms.
        var notes = TuningUtilities.GetTuningNames(stringCount, offsets);
        Assert.Equal(expected, TuningNameResolver.Resolve(notes));
        Assert.Equal(expected, TuningNameResolver.Resolve(string.Join(" ", notes)));
    }

    [Fact]
    public void NoteNames_AreCaseAndEnharmonicInsensitive()
    {
        Assert.Equal("E Standard", TuningNameResolver.Resolve("e a d g b e")); // lowercase (Arrangement default has "e")
        // Eb standard spelled with sharps on input still resolves (D# ≡ Eb, etc.).
        Assert.Equal("Eb Standard", TuningNameResolver.Resolve(new[] { "D#", "G#", "C#", "F#", "A#", "D#" }));
    }

    [Theory]
    [InlineData("C C C C C C")]     // not a named tuning
    [InlineData("E A D G B D")]     // high string dropped only — no common name
    [InlineData("E Standard")]      // already-a-name literal (PSARC default) — not note letters
    [InlineData("garbage x y")]     // unparseable
    [InlineData("E A D")]           // wrong string count
    [InlineData("")]
    [InlineData(null)]
    public void UnknownOrBadInput_ReturnsNull(string? summary) => Assert.Null(TuningNameResolver.Resolve(summary));

    [Fact]
    public void ResolveFromOffsets_RejectsUnsupportedCountsAndLengthMismatch()
    {
        Assert.Null(TuningNameResolver.ResolveFromOffsets(10, new int[10]));                  // past 9 strings
        Assert.Null(TuningNameResolver.ResolveFromOffsets(3, new int[3]));
        Assert.Null(TuningNameResolver.ResolveFromOffsets(6, new[] { 0, 0, 0 }));             // length mismatch
        Assert.Null(TuningNameResolver.ResolveFromOffsets(6, null));
    }

    /// <summary>The 5/6-string bass and the 7/8-string guitar: standards named for the six-string
    ///     equivalent, drops for the lowest note, from offsets and from note letters alike.</summary>
    [Theory]
    [InlineData(new[] { -5, -5, -5, -5, -4 }, "B E A D G", "5-String Bass Standard")]
    [InlineData(new[] { -9, -7, -7, -7, -6 }, "G D G C F", "5-String Bass Drop G")]
    [InlineData(new[] { -5, -5, -5, -5, -4, -4 }, "B E A D G C", "6-String Bass Standard")]
    [InlineData(new[] { -6, -6, -6, -6, -5, -5 }, "Bb Eb Ab Db Gb B", "6-String Bass Eb Standard")]
    [InlineData(new[] { -7, -5, -5, -5, -4, -4 }, "A E A D G C", "6-String Bass Drop A")]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0 }, "B E A D G B E", "7-String Standard")]
    [InlineData(new[] { -2, 0, 0, 0, 0, 0, 0 }, "A E A D G B E", "7-String Drop A")]
    [InlineData(new[] { -1, -1, -1, -1, -1, -1, -1 }, "Bb Eb G# C# F# Bb Eb", "7-String Eb Standard")]
    [InlineData(new[] { -4, -2, -2, -2, -2, -2, -2 }, "G D G C F A D", "7-String Drop G")]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0 }, "F# B E A D G B E", "8-String Standard")]
    [InlineData(new[] { -2, 0, 0, 0, 0, 0, 0, 0 }, "E B E A D G B E", "8-String Drop E")]
    public void ExtendedRangeAndBassTunings_HaveNames(int[] offsets, string notes, string expected)
    {
        Assert.Equal(expected, TuningNameResolver.ResolveFromOffsets(offsets.Length, offsets));
        Assert.Equal(expected, TuningNameResolver.Resolve(notes));
    }

    /// <summary>Every generated name expands back to its offsets at its own string count only, and
    ///     is unique; none of them is a name rscharter's table already knows.</summary>
    [Fact]
    public void ExtendedRangeTypeNames_RoundTrip_AndStayOutOfTheRscharterTable()
    {
        Assert.Equal(ExtendedRangeTunings.All.Count, ExtendedRangeTunings.All.Select(t => t.TypeName).Distinct().Count());
        foreach (var tuning in ExtendedRangeTunings.All)
        {
            Assert.True(TuningUtilities.TryGetOffsetsForTuningType(tuning.TypeName, tuning.StringCount, out var offsets));
            Assert.Equal(tuning.Offsets, offsets);
            Assert.False(TuningUtilities.TryGetOffsetsForTuningType(tuning.TypeName, tuning.StringCount + 1, out _));
            Assert.Equal(tuning.TypeName, ExtendedRangeTunings.TypeNameFor(tuning.Offsets));
        }

        Assert.Equal("7_STRING_DROP_A", ExtendedRangeTunings.TypeNameFor([-2, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("5_STRING_BASS_E_FLAT_STANDARD", ExtendedRangeTunings.TypeNameFor([-6, -6, -6, -6, -5]));
    }

    [Fact]
    public void UnknownOffsets_ReturnNull_ForBothCounts()
    {
        Assert.Null(TuningNameResolver.ResolveFromOffsets(6, new[] { 1, 2, 3, 4, 5, 6 }));
        Assert.Null(TuningNameResolver.ResolveFromOffsets(4, new[] { 1, 3, 5, 7 }));
    }

    /// <summary>
    ///     The REAL distinct tuning strings from the owner's 4,321-song library
    ///     (`library-index.db`, 2026-08-01), most-played first, with the display
    ///     the filters must produce. Written from the live data rather than from
    ///     a list of tunings someone remembered — the last four rows are the
    ///     ones that keep a resolver honest: each differs from a named tuning by
    ///     a single string and must NOT borrow its name.
    /// </summary>
    public static TheoryData<string, string> LibraryTunings => new()
    {
        { "E A D G B E", "E Standard (E A D G B E)" },              // 3,019 songs
        { "Eb G# C# F# Bb Eb", "Eb Standard (Eb G# C# F# Bb Eb)" }, //   424
        { "D A D G B E", "Drop D (D A D G B E)" },                  //   302
        { "C G C F A D", "Drop C (C G C F A D)" },                  //   128
        { "C# G# C# F# Bb Eb", "Drop C# (C# G# C# F# Bb Eb)" },     //   112
        { "D G C F A D", "D Standard (D G C F A D)" },              //    96
        { "B E A D G C", "6-String Bass Standard (B E A D G C)" },  //    58
        { "B E A D F# B", "B Standard (B E A D F# B)" },            //    26
        { "C F Bb Eb G C", "C Standard (C F Bb Eb G C)" },          //    24
        { "C# F# B E G# C#", "Db Standard (C# F# B E G# C#)" },     //    20
        { "A E A D F# B", "Drop A (A E A D F# B)" },                //    12
        { "E B E G# B E", "Open E (E B E G# B E)" },                //    10
        { "D G D G B D", "Open G (D G D G B D)" },                  //     8
        { "D A D G A D", "DADGAD (D A D G A D)" },                  //     6
        { "B F# B E G# C#", "Drop B (B F# B E G# C#)" },            //     6
        { "E A E A C# E", "Open A (E A E A C# E)" },                //     4
        { "G# Eb G# C# F Bb", "Drop G# (G# Eb G# C# F Bb)" },       //     2
        { "F C F Bb D G", "Drop F (F C F Bb D G)" },                //     2
        { "F Bb Eb G# C F", "F Standard (F Bb Eb G# C F)" },        //     2
        { "Eb Bb Eb G Bb Eb", "Open Eb (Eb Bb Eb G Bb Eb)" },       //     2
        { "D A D F A D", "Open D Minor (D A D F A D)" },            //     2
        { "Bb F Bb Eb G C", "Drop A# (Bb F Bb Eb G C)" },           //     2
        // --- deliberately UNnamed: one string away from a named tuning ---
        { "D A D F A E", "D A D F A E" },                 // Open D Minor, top string up 2
        { "Eb G# C# F# B Eb", "Eb G# C# F# B Eb" },       // Eb Standard, B string up 1
        { "C G C F B E", "C G C F B E" },                 // Drop C, top two up 2
        { "E G D G B D", "E G D G B D" }                  // Open G, low string up 2
    };

    [Theory]
    [MemberData(nameof(LibraryTunings))]
    public void Describe_NamesTheLibrarysRealTunings_AndKeepsTheNotesBeside(string summary, string expected) => Assert.Equal(expected, TuningNameResolver.Describe(summary));

    [Fact]
    public void Describe_FallsBackToTheRawNotes_AndNeverEmitsAStrayBracket()
    {
        // Unknown ⇒ the notes alone, with no empty "()" tacked on.
        Assert.Equal("C C C C C C", TuningNameResolver.Describe("C C C C C C"));
        Assert.DoesNotContain("(", TuningNameResolver.Describe("C C C C C C"));

        // Absent ⇒ empty, never "()" and never a bare name.
        Assert.Equal("", TuningNameResolver.Describe(""));
        Assert.Equal("", TuningNameResolver.Describe("   "));
        Assert.Equal("", TuningNameResolver.Describe(null));
    }

    /// <summary>
    ///     The 6-string BASS standard shares the 6-entry bucket with every
    ///     guitar tuning, so this pins that adding it stole nothing: B-E-A-D-G-C
    ///     resolves to the bass name while the guitar tuning one semitone away
    ///     on the top string keeps its own answer.
    /// </summary>
    [Fact]
    public void SixStringBass_DoesNotCollideWithTheGuitarTable()
    {
        Assert.Equal("6-String Bass Standard", TuningNameResolver.Resolve("B E A D G C"));
        Assert.Equal("B Standard", TuningNameResolver.Resolve("B E A D F# B"));
        // A 5-string bass pads slot 5 with standard's own E — not a real tuning.
        Assert.Null(TuningNameResolver.Resolve("B E A D G E"));
    }
}
