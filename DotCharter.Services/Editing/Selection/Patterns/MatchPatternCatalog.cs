namespace DotCharter.Services.Editing.Selection.Patterns;

/// <summary>
///     The starter patterns a fresh install ships with. They exist to be
///     ARGUED WITH: each one is a different answer to "what does alike mean",
///     and the point of the pattern builder is finding out which of them a real
///     charting session actually reaches for.
///     <para>
///         <b>All of them ship UNBOUND.</b> Eleven patterns times three verbs is
///         thirty-three chords, and this repo resolves a keybind collision
///         last-registration-wins with nothing but a log line — silently killing
///         a shipped editing key to make room for a speculative one would be a
///         bad trade. The builder can run any pattern on the spot without a key,
///         so binding is a decision the user makes after the pattern has earned
///         it, not before.
///     </para>
/// </summary>
public static class MatchPatternCatalog
{
    /// <summary>Ids are stable and deliberately readable — a built-in that the
    ///     user edited keeps its id, so a later build can still recognise it.</summary>
    public const string SameChordShapeId = "builtin.chord.exact";

    public const string MovableShapeId = "builtin.chord.movable";
    public const string ChordWithItsShapeAndAnchorId = "builtin.chord.withcompanions";
    public const string SameGripAnyStringsId = "builtin.chord.anystrings";
    public const string SameNoteId = "builtin.note.exact";
    public const string SameBendId = "builtin.note.bend";
    public const string SameLickId = "builtin.run.lick";
    public const string SameLickAnyTempoId = "builtin.run.lick.anytempo";
    public const string ThisSectionOnlyId = "builtin.chord.thissection";
    public const string SameHandJobId = "builtin.anchor.job";
    public const string SameHandPositionId = "builtin.anchor.fretwidth";

    public static IReadOnlyList<MatchPattern> BuiltIns() =>
    [
        // The shipped select-like answer for shapes, written out as data: the
        // string→fret map the sound PLAYS, technique absent by construction.
        new MatchPattern
        {
            Id = SameChordShapeId,
            Name = "Same chord shape",
            Scope = PatternScope.Chords | PatternScope.HandShapes,
            Fret = FretRule.Exact,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = true,
            MatchFingering = true
        },

        // The one to reach for before a bulk edit: the chord, the bar drawn over
        // it and the hand position holding it are ONE authored decision, and
        // re-fingering the chord alone leaves the other two describing a shape
        // that is gone.
        new MatchPattern
        {
            Id = ChordWithItsShapeAndAnchorId,
            Name = "Same chord + its shape & FHP",
            Scope = PatternScope.Chords,
            Fret = FretRule.Exact,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = true,
            MatchFingering = true,
            Companions = PatternCompanions.CoveringHandShape | PatternCompanions.GoverningAnchor
        },

        // The same grip anywhere on the neck — every barre chord of one shape.
        // An open string pins a shape in place, so a shape containing one only
        // ever matches another with the same string open.
        new MatchPattern
        {
            Id = MovableShapeId,
            Name = "Same shape, any position",
            Scope = PatternScope.Chords | PatternScope.HandShapes,
            Fret = FretRule.Transposable,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = true
        },

        // …and the same grip moved ACROSS the strings too: a power chord on
        // 6/5 and the same one on 5/4 are one hand doing one thing.
        new MatchPattern
        {
            Id = SameGripAnyStringsId,
            Name = "Same grip, any strings",
            Scope = PatternScope.Chords | PatternScope.HandShapes,
            Fret = FretRule.Transposable,
            Strings = StringRule.Relative,
            MatchSize = true,
            MatchSoundKind = true
        },

        // One note, same place, played the same way.
        new MatchPattern
        {
            Id = SameNoteId,
            Name = "Same note, same technique",
            Scope = PatternScope.Notes,
            Fret = FretRule.Exact,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = true,
            Sustain = SustainRule.SameKind,
            Techniques = TechniqueFacet.All
        },

        // Everything that bends the same WAY, wherever it is — the shape of the
        // curve is the question, not the fret it starts on.
        new MatchPattern
        {
            Id = SameBendId,
            Name = "Same bend anywhere",
            Scope = PatternScope.Notes,
            Fret = FretRule.Ignore,
            Strings = StringRule.Ignore,
            MatchSize = false,
            MatchSoundKind = true,
            Techniques = TechniqueFacet.Bend
        },

        // The whole selection as one contiguous run, transposed anywhere, with
        // its rhythm intact.
        new MatchPattern
        {
            Id = SameLickId,
            Name = "Same lick (run)",
            Scope = PatternScope.Sounds,
            Grouping = PatternGrouping.AsRun,
            Fret = FretRule.Transposable,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = false,
            Rhythm = RhythmRule.Exact
        },

        // …and the same lick restated at half or double time, which is a real
        // thing songs do and a plain rhythm comparison can never find.
        new MatchPattern
        {
            Id = SameLickAnyTempoId,
            Name = "Same lick, any tempo",
            Scope = PatternScope.Sounds,
            Grouping = PatternGrouping.AsRun,
            Fret = FretRule.Transposable,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = false,
            Rhythm = RhythmRule.Proportional
        },

        // "Every chorus and nothing else" is a charting request, not a search
        // refinement — it is the difference between fixing one passage and
        // rewriting the whole chart.
        new MatchPattern
        {
            Id = ThisSectionOnlyId,
            Name = "Same chord, this section only",
            Scope = PatternScope.Sounds,
            Fret = FretRule.Exact,
            Strings = StringRule.Exact,
            MatchSize = true,
            MatchSoundKind = true,
            Range = PatternRange.SameSection
        },

        // The shipped anchor answer: the hand's JOB, not its fret.
        new MatchPattern
        {
            Id = SameHandJobId,
            Name = "Same hand job (FHP + span)",
            Scope = PatternScope.Anchors,
            Fret = FretRule.Exact,
            MatchAnchorWidth = true,
            MatchAnchorSpan = true,
            Companions = PatternCompanions.SoundsInside
        },

        // The looser anchor question the shipped rule deliberately refuses to
        // answer. It is here to be TRIED, not because it is safe: without the
        // span it will happily select hand positions held for completely
        // different passages.
        new MatchPattern
        {
            Id = SameHandPositionId,
            Name = "Same FHP fret + width (no span)",
            Scope = PatternScope.Anchors,
            Fret = FretRule.Exact,
            MatchAnchorWidth = true,
            MatchAnchorSpan = false
        }
    ];
}
