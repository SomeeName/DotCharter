using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <see cref="SlideTechniqueRule" />'s three OPEN-STRING gates — L09 (slide
///     from an open string), L11 (hammer-on onto one) and L12 (tap on one) —
///     against a CAPO.
///     <para>
///         All three tested <c>Fret == 0</c>, which is only the open string on
///         an uncapoed neck. A capo stops every string at its own fret, so on a
///         capo'd chart the open string is fret <c>capo</c> and the three checks
///         went silent for exactly the notes they exist to catch: nothing is
///         fretting the string, so there is nothing to slide off, hammer onto or
///         tap. The rest of the app already spells "open" this way —
///         <see cref="OpenNoteBendRule" /> ("at or below the capo"),
///         <see cref="CapoAnchorRangeRule" />'s anchor-coverage skip and
///         <see cref="TemplateAndOverlapRule" />'s <c>fret &lt;= capo</c> — and
///         <c>DotCharter.Core/AGENTS.md</c> states the sounding offset as
///         <c>max(fret, capo)</c>.
///     </para>
/// </summary>
public sealed class SlideTechniqueCapoTests
{
    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Of(Arrangement arr, ArrangementIssueType type) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(arr, Beats())
            .Where(i => i.Type == type)
            .ToList();

    private static Arrangement WithNote(GuitarNote note, int capo)
    {
        var arr = new Arrangement { Capo = capo };
        var level = new ChartLevel();
        level.Notes.Add(note);
        arr.Levels.Add(level);
        return arr;
    }

    private static GuitarNote Slide(int fret, int slideTo) => new()
    {
        Position = new FractionalPosition(2),
        Sustain = new FractionalPosition(1),
        StringIndex = 2,
        Fret = fret,
        SlideTo = slideTo,
        LinkNext = true // keeps L10 (pitched slide not linked) out of the way
    };

    private static GuitarNote Hopo(int fret, HopoType hopo) => new()
    {
        Position = new FractionalPosition(2),
        StringIndex = 2,
        Fret = fret,
        Hopo = hopo
    };

    // ---- L09: a slide has to start from a stopped fret ----------------------

    [Fact]
    public void ASlideOffAnUncapoedOpenStringIsReported()
    {
        var issue = Assert.Single(Of(WithNote(Slide(fret: 0, slideTo: 7), capo: 0),
            ArrangementIssueType.SlideFromOpenString));

        Assert.Equal("L09", issue.Code);
    }

    /// <summary>
    ///     Fret 3 under a capo on fret 3 IS the open string — the capo holds it,
    ///     no finger does, and there is nothing to slide away from.
    /// </summary>
    [Fact]
    public void ASlideOffTheCapoFretIsReportedToo()
    {
        var issue = Assert.Single(Of(WithNote(Slide(fret: 3, slideTo: 7), capo: 3),
            ArrangementIssueType.SlideFromOpenString));

        Assert.Equal("L09", issue.Code);
        Assert.Contains("capo", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASlideOffAFrettedNoteAboveTheCapoIsFine()
    {
        Assert.Empty(Of(WithNote(Slide(fret: 5, slideTo: 9), capo: 3),
            ArrangementIssueType.SlideFromOpenString));
    }

    /// <summary>
    ///     The capo-fret start is reported ONCE, as L09 — not also as E01
    ///     ("ends on its own start fret"), which is the same else-if precedence
    ///     fret 0 has always had.
    /// </summary>
    [Fact]
    public void ACapoFretStartIsNotAlsoReportedAsADegenerateSlide()
    {
        Assert.Empty(Of(WithNote(Slide(fret: 3, slideTo: 3), capo: 3),
            ArrangementIssueType.SlideEndsOnStartFret));
    }

    // ---- L11 / L12: neither technique has a string to act on ----------------

    [Fact]
    public void AHammerOnOntoTheCapoFretIsReported()
    {
        var issue = Assert.Single(Of(WithNote(Hopo(fret: 3, HopoType.HammerOn), capo: 3),
            ArrangementIssueType.HammerOnOnOpenString));

        Assert.Equal("L11", issue.Code);
    }

    [Fact]
    public void ATapOnTheCapoFretIsReported()
    {
        var issue = Assert.Single(Of(WithNote(Hopo(fret: 3, HopoType.Tap), capo: 3),
            ArrangementIssueType.TapOnOpenString));

        Assert.Equal("L12", issue.Code);
    }

    [Fact]
    public void AHammerOnAboveTheCapoIsFine()
    {
        Assert.Empty(Of(WithNote(Hopo(fret: 5, HopoType.HammerOn), capo: 3),
            ArrangementIssueType.HammerOnOnOpenString));
    }

    /// <summary>The uncapoed behaviour is unchanged — capo 0 is the overwhelming
    /// majority of charts and must stay byte-for-byte the same.</summary>
    [Fact]
    public void AnUncapoedFrettedNoteIsStillFine()
    {
        Assert.Empty(Of(WithNote(Hopo(fret: 1, HopoType.Tap), capo: 0),
            ArrangementIssueType.TapOnOpenString));
        Assert.Empty(Of(WithNote(Slide(fret: 1, slideTo: 5), capo: 0),
            ArrangementIssueType.SlideFromOpenString));
    }

    // ---- the chord half uses the same definition ----------------------------

    /// <summary>
    ///     E01's chord branch guarded on <c>cn.Fret &gt; 0</c> for the same
    ///     reason the note branch guarded on fret 0 — a degenerate slide on an
    ///     open string is the open-string case, not a slide. Under a capo the
    ///     guard has to move with the definition, or a chord note on the capo
    ///     fret sliding to the capo fret reads as a real slide.
    /// </summary>
    [Fact]
    public void AChordNoteSlidingToItsOwnCapoFretIsNotADegenerateSlide()
    {
        var arr = new Arrangement { Capo = 3 };
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 3, SlideTo = 3, Sustain = new FractionalPosition(1)
        });
        level.Chords.Add(chord);
        arr.Levels.Add(level);

        Assert.Empty(Of(arr, ArrangementIssueType.SlideEndsOnStartFret));
    }
}
