using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <see cref="ChordRule" />'s I45 — a barre with an OPEN string strictly
///     between its ends, which no hand can play: the barring finger lies across
///     that string too, so it cannot be sounding open.
///     <para>
///         The check looked for <c>fret == 0</c>. Under a capo an open string is
///         written as the CAPO FRET, not 0 — that is what
///         <c>TemplateAndOverlapRule</c> means by <c>isOpen = fret &lt;= capo</c>,
///         and it is exactly the value <c>ArrangementAutoFixService.FixCapoFrets</c>
///         normalizes a sub-capo template fret to. So on a capo'd chart the whole
///         check went silent: the unplayable shape validated clean.
///     </para>
/// </summary>
public sealed class BarreOverOpenStringsCapoTests
{
    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>
    ///     A first-finger barre across strings 0 and 3 at <paramref name="barreFret" />,
    ///     with string 1 between them sounding at <paramref name="innerFret" /> and
    ///     carrying no finger of its own. String 2 is fretted normally so the
    ///     shape is a real grip.
    /// </summary>
    private static Arrangement Chart(int capo, int barreFret, int innerFret)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = capo };

        var template = new ChordTemplate { Id = 0, Name = "Barre" };
        template.Frets[0] = barreFret;
        template.Frets[1] = innerFret;
        template.Frets[2] = barreFret + 2;
        template.Frets[3] = barreFret;
        template.Fingers[0] = 1;
        template.Fingers[2] = 3;
        template.Fingers[3] = 1;
        arr.ChordTemplates.Add(template);

        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 0 };
        foreach (var (stringIndex, fret) in template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        level.Chords.Add(chord);
        arr.Levels.Add(level);

        return arr;
    }

    private static IReadOnlyList<ArrangementIssue> Barre(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.BarreOverOpenStrings)
            .ToList();

    /// <summary>The uncapoed case, which has always worked — the control that
    /// stops the capo cases below passing for the wrong reason.</summary>
    [Fact]
    public void AnOpenStringUnderABarreIsReportedWithoutACapo()
    {
        var issue = Assert.Single(Barre(Chart(capo: 0, barreFret: 5, innerFret: 0)));

        Assert.Equal("I45", issue.Code);
    }

    /// <summary>
    ///     Capo 3, barre at fret 7, inner string sounding at fret 3 — which is
    ///     the capo, i.e. open. Exactly as unplayable as the case above.
    /// </summary>
    [Fact]
    public void AnOpenStringUnderABarreIsReportedWithACapoToo()
    {
        var issue = Assert.Single(Barre(Chart(capo: 3, barreFret: 7, innerFret: 3)));

        Assert.Equal("I45", issue.Code);
    }

    /// <summary>A fret 0 written under a capo means the same open string, and
    /// the rule reads both spellings.</summary>
    [Fact]
    public void AFretZeroUnderACapoIsStillOpen() => Assert.Single(Barre(Chart(capo: 3, barreFret: 7, innerFret: 0)));

    /// <summary>
    ///     The narrowing half: a string genuinely FRETTED between the barre's
    ///     ends is ordinary — that is what a barre chord is — and stays
    ///     unreported at any capo.
    /// </summary>
    [Fact]
    public void AFrettedStringUnderABarreIsFine()
    {
        Assert.Empty(Barre(Chart(capo: 0, barreFret: 5, innerFret: 8)));
        Assert.Empty(Barre(Chart(capo: 3, barreFret: 7, innerFret: 9)));
    }

    /// <summary>An inner string ON the barre's own fret is the barre itself, not
    /// an open string — the boundary the <c>&lt;= capo</c> test must not swallow.</summary>
    [Fact]
    public void AnInnerStringOnTheBarreFretIsFine() => Assert.Empty(Barre(Chart(capo: 3, barreFret: 7, innerFret: 7)));
}
