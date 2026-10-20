using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <see cref="ChordRule" /> exercised directly against a built
///     <see cref="ArrangementValidationContext" /> — the rule alone, so the
///     allocation claim below measures this rule and not the other thirteen.
/// </summary>
public sealed class ChordRuleTests
{
    private static List<BeatMarker> Beats(int count, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static IReadOnlyList<ArrangementIssue> Run(Arrangement arr, IList<BeatMarker> beats)
    {
        var issues = new List<ArrangementIssue>();
        new ChordRule().Validate(ArrangementValidationContext.Build(arr, beats, null), issues);
        return issues;
    }

    // ---- I33: a check that could never fire --------------------------------

    /// <summary>
    ///     <b>The rule was a tautological contradiction.</b> It asked
    ///     <c>chord.HasLinkNext() &amp;&amp; chord.ChordNotes.All(cn =&gt;
    ///     !cn.LinkNext)</c> — and <c>HasLinkNext()</c> is defined as "any chord
    ///     note links", so the conjunction is <c>A &amp;&amp; !A</c>. I33 could
    ///     not be raised by any chart, while the inspector's field map, the
    ///     severity table and this message all read as if it were covered.
    ///     <para>
    ///         The representable fault is the one the SNG mapper cares about:
    ///         <c>ForceNoNotes</c> suppresses the chord-notes block while the mask
    ///         still carries <c>Parent</c> from the links.
    ///     </para>
    /// </summary>
    [Fact]
    public void Detects_ChordThatLinks_ButForcesNoChordNotes()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = new FractionalPosition(1), ForceNoNotes = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, LinkNext = true });
        arr.Levels[0].Chords.Add(chord);

        var issues = Run(arr, Beats(8));

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.MissingLinkNextChordNotes);
    }

    /// <summary>An ordinary linked chord is LEGAL TO AUTHOR and must stay clean —
    /// the landing is repaired at the export boundary, never flagged here.</summary>
    [Fact]
    public void DoesNot_Flag_AnOrdinaryLinkedChord()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, LinkNext = true, Sustain = new FractionalPosition(1)
        });
        arr.Levels[0].Chords.Add(chord);

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.MissingLinkNextChordNotes);
    }

    [Fact]
    public void DoesNot_Flag_AChordThatForcesNoNotesButDoesNotLink()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = new FractionalPosition(1), ForceNoNotes = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.MissingLinkNextChordNotes);
    }

    // ---- the two template-fingering verdicts still say the same thing -------

    [Fact]
    public void Detects_BarreOverAnOpenString()
    {
        var arr = TemplateArrangement(out var template, chords: 1);
        // First finger barres strings 0 and 3; string 1 in between is OPEN.
        template.Frets[0] = 5; template.Fingers[0] = 1;
        template.Frets[1] = 0;
        template.Frets[3] = 5; template.Fingers[3] = 1;

        var issues = Run(arr, Beats(8));

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.BarreOverOpenStrings);
    }

    [Fact]
    public void DoesNot_Flag_ABarreWithNoOpenStringUnderIt()
    {
        var arr = TemplateArrangement(out var template, chords: 1);
        template.Frets[0] = 5; template.Fingers[0] = 1;
        template.Frets[1] = 7; template.Fingers[1] = 3;
        template.Frets[3] = 5; template.Fingers[3] = 1;

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.BarreOverOpenStrings);
    }

    [Fact]
    public void Detects_AFrettedStringBelowTheLowestFinger()
    {
        var arr = TemplateArrangement(out var template, chords: 1);
        // Finger 1 sits on fret 7; finger 2 sits LOWER on the neck, at fret 5.
        template.Frets[0] = 7; template.Fingers[0] = 1;
        template.Frets[1] = 5; template.Fingers[1] = 2;

        var issues = Run(arr, Beats(8));

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
    }

    [Fact]
    public void DoesNot_Flag_AnOrdinaryFingering()
    {
        var arr = TemplateArrangement(out var template, chords: 1);
        template.Frets[0] = 5; template.Fingers[0] = 1;
        template.Frets[1] = 7; template.Fingers[1] = 3;

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
    }

    // ---- the -1 sentinel is "cannot judge", never "no issue" ---------------

    /// <summary>An arrangement carrying one template with a fingering I44
    /// flags, and one chord that PLAYS that shape without naming it.</summary>
    private static Arrangement UntemplatedChordOverAStrangeShape(out Chord chord, bool arpeggioTemplate = false)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        var template = new ChordTemplate { Id = 0, Name = "X", ForceArpeggioInRs = arpeggioTemplate };
        // Finger 1 sits on fret 7; finger 2 sits LOWER on the neck, at fret 5.
        template.Frets[0] = 7; template.Fingers[0] = 1;
        template.Frets[1] = 5; template.Fingers[1] = 2;
        arr.ChordTemplates.Add(template);

        arr.Levels.Add(new ChartLevel());
        chord = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        return arr;
    }

    /// <summary>
    ///     <b>A <c>TemplateId</c> of <c>-1</c> was a clean bill of health.</b>
    ///     I44/I45 were gated on <c>chord.TemplateId &gt;= 0</c>, so a chord left
    ///     on the sentinel — the tab importers' idempotency skips leave some —
    ///     was reported as having no fingering problem at all. The shape is
    ///     knowable: it is the template the next save will repoint the chord at.
    /// </summary>
    [Fact]
    public void AChordOnTheSentinel_IsJudgedByTheShapeItPlays()
    {
        var arr = UntemplatedChordOverAStrangeShape(out var chord);

        var issues = Run(arr, Beats(8));

        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
        Assert.Same(chord, issue.Chord);
        // …and it is JUDGED, so it is not also reported as unjudgeable.
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ChordWithoutTemplate);
    }

    /// <summary>Vacuity keeper: the fallback resolves a SHAPE, it does not
    /// flag every untemplated chord. A clean shape stays clean.</summary>
    [Fact]
    public void AChordOnTheSentinel_OverACleanShape_StaysClean()
    {
        var arr = UntemplatedChordOverAStrangeShape(out _);
        var template = arr.ChordTemplates[0];
        template.Frets[0] = 5; template.Fingers[0] = 1; // ordinary fingering
        template.Frets[1] = 7; template.Fingers[1] = 3;
        var chord = arr.Levels[0].Chords[0];
        chord.ChordNotes[0].Fret = 5;
        chord.ChordNotes[1].Fret = 7;

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ChordWithoutTemplate);
    }

    /// <summary>
    ///     …and when even the shape resolves to nothing, the honest answer is
    ///     "cannot judge" (D06) rather than silence — stated ONCE, because a
    ///     per-chord finding repeats itself once per difficulty level.
    /// </summary>
    [Fact]
    public void AChordWhoseShapeResolvesToNothing_IsReportedOnce_AsUnjudgeable()
    {
        var arr = UntemplatedChordOverAStrangeShape(out _);
        arr.ChordTemplates.Clear(); // nothing declares this shape

        // The same chord in a second difficulty level: the summary must not
        // become one finding per level.
        var second = new ChartLevel();
        var clone = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        clone.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 7 });
        clone.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        second.Chords.Add(clone);
        arr.Levels.Add(second);

        var issues = Run(arr, Beats(8));

        var summary = Assert.Single(issues, i => i.Type == ArrangementIssueType.ChordWithoutTemplate);
        Assert.Contains("2 chords", summary.Message);
        Assert.False(summary.HasTime); // non-positional, like D05
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
    }

    /// <summary>
    ///     The fallback uses the find-or-create KEY, so it must not adopt an
    ///     ARPEGGIO template for a plain chord — that is the flavour half of the
    ///     key, and a false positive there repoints a chord onto a template that
    ///     exports under a different name.
    /// </summary>
    [Fact]
    public void TheShapeFallback_DoesNotAdoptAnArpeggioTemplate()
    {
        var arr = UntemplatedChordOverAStrangeShape(out _, arpeggioTemplate: true);

        var issues = Run(arr, Beats(8));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
        Assert.Single(issues, i => i.Type == ArrangementIssueType.ChordWithoutTemplate);
    }

    /// <summary>An empty chord plays nothing, so there is no shape to resolve
    /// and nothing to report — D06 must not fire on it.</summary>
    [Fact]
    public void AnEmptyChordOnTheSentinel_IsNotCountedAsUnjudgeable()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Chords.Add(new Chord { Position = new FractionalPosition(1), TemplateId = -1 });

        Assert.DoesNotContain(Run(arr, Beats(8)), i => i.Type == ArrangementIssueType.ChordWithoutTemplate);
    }

    // ---- allocation --------------------------------------------------------

    /// <summary>
    ///     <b>Validating a chord allocates nothing.</b> Validation re-runs after
    ///     every history step on large levels and is documented as
    ///     allocation-conscious, and this rule used to drop roughly a kilobyte per
    ///     CHORD: six LINQ allocations from the two fingering predicates (five of
    ///     them <c>OrderBy</c> buffers, re-answering the same question about the
    ///     same SHARED <c>ChordTemplate</c> for every chord that used it), plus
    ///     four boxed enumerators from <c>Count</c>/<c>All</c>/<c>Any</c> and the
    ///     <c>foreach</c> over <c>ChordNotes</c>.
    ///     <para>
    ///         Structural, not wall-clock: a pass over 2,000 chords that raise no
    ///         issues must not allocate materially more than a pass over 50 of
    ///         them.
    ///     </para>
    ///     <para>
    ///         <b>Not vacuous — it is sharp enough to see 40 bytes.</b> With the
    ///         predicates already fixed, the single boxed enumerator left by
    ///         <c>foreach (var cn in chord.ChordNotes)</c> failed it at 78,000
    ///         bytes for the 1,950-chord difference. <c>ObservableCollection&lt;T&gt;</c>
    ///         derives from <c>Collection&lt;T&gt;</c> and exposes no struct
    ///         enumerator, so every <c>foreach</c> over one boxes.
    ///     </para>
    /// </summary>
    [Fact]
    public void ValidatingManyChordsOverOneTemplate_DoesNotAllocatePerChord()
    {
        var small = MeasureCleanPass(chords: 50);
        var large = MeasureCleanPass(chords: 2000);

        var perChordGrowth = large - small;
        Assert.True(perChordGrowth < 32 * 1024,
            $"ChordRule allocated {perChordGrowth} extra bytes for 1,950 extra chords over one shared template " +
            "— the per-chord work is back.");
    }

    private static long MeasureCleanPass(int chords)
    {
        var arr = TemplateArrangement(out var template, chords);
        template.Frets[0] = 5; template.Fingers[0] = 1;
        template.Frets[1] = 7; template.Fingers[1] = 3;

        var beats = Beats(chords + 8);
        var context = ArrangementValidationContext.Build(arr, beats, null);
        var rule = new ChordRule();

        // Warm the JIT and every lazily-built index inside the context, so the
        // measured pass is the steady-state one.
        var warm = new List<ArrangementIssue>();
        rule.Validate(context, warm);
        Assert.Empty(warm);

        var issues = new List<ArrangementIssue>(16);
        var before = GC.GetAllocatedBytesForCurrentThread();
        rule.Validate(context, issues);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(issues);
        return allocated;
    }

    /// <summary>An arrangement of <paramref name="chords" /> chords, one per beat,
    /// all pointing at one shared template the caller then shapes.</summary>
    private static Arrangement TemplateArrangement(out ChordTemplate template, int chords)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        template = new ChordTemplate { Id = 0, Name = "X" };
        arr.ChordTemplates.Add(template);

        for (var i = 0; i < chords; i++)
        {
            var chord = new Chord { Position = new FractionalPosition(i + 1), TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
            arr.Levels[0].Chords.Add(chord);
        }

        return arr;
    }
}
