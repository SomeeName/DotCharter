using DotCharter.Services.Export.Validation;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>Which inspector field an <see cref="ArrangementIssue" /> lands on.
/// <see cref="Card" /> is the fallback — an unmapped code still surfaces, on the
/// card header.</summary>
public enum InspectorField
{
    Fret,
    String,
    Length,
    SlideTo,
    Bend,
    Fingering,
    Name,
    Mute,
    Hopo,
    Harmonic,
    LinkNext,
    Position,
    Span,
    Anchor,
    Card
}

/// <summary>
///     Two pure tables the inspector needs and did not have (04-inspector-redesign.md
///     §9).
///     <para>
///         <b>Map</b> — <c>ArrangementIssue</c> has carried the back-references
///         (<c>Note</c>, <c>Chord</c>, <c>HandShape</c>, <c>Anchor</c>,
///         <c>StringIndex</c>) all along and the panel never used them, so every
///         finding was a row in a different tab rather than a marker on the field
///         that caused it.
///     </para>
///     <para>
///         <b>BlocksExport</b> — the editing validator and the export validator
///         are separate, so a charter saw <c>L15</c>/<c>L01</c> as chips that
///         blocked nothing and only learned they were export ERRORS after
///         pressing "check export readiness". This is the pairing, and a test
///         pins it so the two cannot drift apart silently.
///     </para>
///     <para>
///         Both are exhaustive over <see cref="ArrangementIssueType" /> BY TEST,
///         not by a <c>switch</c> the compiler checks — a new code must be given
///         an explicit home rather than degrading to <see cref="InspectorField.Card" />
///         forever.
///     </para>
/// </summary>
public static class InspectorIssueFieldMap
{
    public static InspectorField Map(ArrangementIssueType type) => type switch
    {
        // Fret — the number itself is wrong or unplayable.
        ArrangementIssueType.NoteFretBelowCapo => InspectorField.Fret,
        ArrangementIssueType.FretNumberMoreThan24 => InspectorField.Fret,

        // Length — it is the SUSTAIN that is wrong, not the placement.
        ArrangementIssueType.OverlappingNotes => InspectorField.Length,
        ArrangementIssueType.TechniqueNoteWithoutSustain => InspectorField.Length,

        // String.
        ArrangementIssueType.InvalidBassArrangementString => InspectorField.String,

        // Slide.
        ArrangementIssueType.SlideFromOpenString => InspectorField.SlideTo,
        ArrangementIssueType.SlideEndsOnStartFret => InspectorField.SlideTo,
        ArrangementIssueType.PitchedSlideNotLinked => InspectorField.SlideTo,
        ArrangementIssueType.FingerChangeDuringSlide => InspectorField.SlideTo,
        ArrangementIssueType.AnchorCloseToUnpitchedSlide => InspectorField.SlideTo,

        // Bends.
        ArrangementIssueType.BendStrengthTooHigh => InspectorField.Bend,
        ArrangementIssueType.TooManyBendPoints => InspectorField.Bend,
        ArrangementIssueType.MissingBendValue => InspectorField.Bend,
        ArrangementIssueType.OverlappingBendValues => InspectorField.Bend,
        ArrangementIssueType.NaturalHarmonicWithBend => InspectorField.Bend,

        // Fingering (+ the string's rail pip when StringIndex is set).
        ArrangementIssueType.FingerSetForOpenString => InspectorField.Fingering,
        ArrangementIssueType.FingerMissingOnFrettedString => InspectorField.Fingering,
        ArrangementIssueType.FirstFingerNotOnLowestFret => InspectorField.Fingering,
        ArrangementIssueType.PossiblyWrongChordFingering => InspectorField.Fingering,
        ArrangementIssueType.BarreOverOpenStrings => InspectorField.Fingering,

        // Link-next. The pass CLEARS LinkNext and no provenance flag exists, so
        // validation-on-the-field is how the user sees WHY it will go.
        ArrangementIssueType.LinkNextMissingTargetNote => InspectorField.LinkNext,
        ArrangementIssueType.LinkNextFretMismatch => InspectorField.LinkNext,
        ArrangementIssueType.LinkNextSlideMismatch => InspectorField.LinkNext,
        ArrangementIssueType.LinkNextBendMismatch => InspectorField.LinkNext,
        ArrangementIssueType.IncorrectLinkNext => InspectorField.LinkNext,
        ArrangementIssueType.UnpitchedSlideWithLinkNext => InspectorField.LinkNext,
        ArrangementIssueType.MissingLinkNextChordNotes => InspectorField.LinkNext,
        ArrangementIssueType.NoteLinkedToChord => InspectorField.LinkNext,

        // HOPO.
        ArrangementIssueType.HammerOnOnOpenString => InspectorField.Hopo,
        ArrangementIssueType.TapOnOpenString => InspectorField.Hopo,
        ArrangementIssueType.PullOffWithoutPrecedingNote => InspectorField.Hopo,
        ArrangementIssueType.PullOffOntoHigherOrEqualFret => InspectorField.Hopo,
        ArrangementIssueType.HopoIntoSameNote => InspectorField.Hopo,
        ArrangementIssueType.PositionShiftIntoPullOff => InspectorField.Hopo,

        // Harmonic.
        ArrangementIssueType.SeventhFretHarmonicWithSustain => InspectorField.Harmonic,
        ArrangementIssueType.DoubleHarmonic => InspectorField.Harmonic,

        // Mute.
        ArrangementIssueType.MutedStringInNonMutedChord => InspectorField.Mute,

        // Anchors / FHP — card-level, with a "generate FHP" inline action.
        ArrangementIssueType.NoteWithoutAnchor => InspectorField.Anchor,
        ArrangementIssueType.NoteOutsideAnchorRange => InspectorField.Anchor,
        ArrangementIssueType.FingeringAnchorMismatch => InspectorField.Anchor,
        // EOF's own advice is "change the fret hand position to match", so the
        // anchor is the field to jump to — not the fingering.
        ArrangementIssueType.IndexFingerNotOnAnchorFret => InspectorField.Anchor,
        // The disagreement is about which FRET the sound plays under the shape,
        // and the sound is the movable half — the shape usually describes what
        // the passage is supposed to be.
        ArrangementIssueType.SoundViolatesHandShape => InspectorField.Fret,
        // The fix is an ANCHOR at the phrase start (restating the inherited
        // fret), so the anchor is what the user needs in front of them — the
        // phrase marker itself is correct and must not move.
        ArrangementIssueType.AnchorMissingAtPhraseStart => InspectorField.Anchor,
        // Either the bend is wrong or the fret is; the fret is the field that
        // decides which, since a bend is only legal above the capo.
        ArrangementIssueType.OpenNoteWithBend => InspectorField.Fret,
        ArrangementIssueType.AnchorOnOrBelowCapo => InspectorField.Anchor,
        ArrangementIssueType.AnchorTopFretTooHigh => InspectorField.Anchor,
        ArrangementIssueType.OverlappingAnchors => InspectorField.Anchor,

        // Span (hand shapes).
        ArrangementIssueType.AnchorInsideHandShape => InspectorField.Span,
        ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary => InspectorField.Span,
        ArrangementIssueType.OverlappingHandShapes => InspectorField.Span,

        // Position.
        ArrangementIssueType.NoteAfterSongEnd => InspectorField.Position,
        ArrangementIssueType.NoteInsideNoguitarSection => InspectorField.Position,
        ArrangementIssueType.FirstNoteTooEarly => InspectorField.Position,
        ArrangementIssueType.PhraseChangeOnLinkNextNote => InspectorField.Position,
        ArrangementIssueType.ToneChangeOnNote => InspectorField.Position,

        // Structure — nothing on a SOUND causes these, so they stay on the card.
        ArrangementIssueType.FirstPhraseNotEmpty => InspectorField.Card,
        ArrangementIssueType.NoEndPhrase => InspectorField.Card,
        ArrangementIssueType.MoreThan100Phrases => InspectorField.Card,
        ArrangementIssueType.EmptyPhrase => InspectorField.Card,
        ArrangementIssueType.ToneChangeWithoutMatchingTone => InspectorField.Card,
        ArrangementIssueType.ToneChangeWithoutDefinition => InspectorField.Card,
        // D05 is one non-positional summary per arrangement ("no audio, so
        // I29 could not run") — there is no sound to land it on.
        ArrangementIssueType.SongEndUnverifiedNoAudio => InspectorField.Card,
        // Arrangement-wide "this could not be checked" reports: they belong to no
        // note, chord or shape, so they sit on the card like their D05 sibling.
        ArrangementIssueType.NoBeatGridSoNoChecksRan => InspectorField.Card,
        ArrangementIssueType.ValidationRuleCrashed => InspectorField.Card,
        // D08 is the same shape one level in: one summary saying the fingering
        // checks could not run because these chords name no template. No single
        // sound owns it either.
        ArrangementIssueType.ChordWithoutTemplate => InspectorField.Card,
        // D09 likewise: one summary for the shapes that declare no grip at all.
        ArrangementIssueType.HandShapeWithoutTemplate => InspectorField.Card,
        // D10 names a PHRASE, which no sound owns.
        ArrangementIssueType.PhraseNameRewrittenForRocksmith => InspectorField.Card,
        // D11 is about a hand shape that does NOT exist, so no field of the chord
        // owns it; the fix is auto-fix or Generate hand shapes.
        ArrangementIssueType.ChordOutsideHandShape => InspectorField.Card,

        _ => InspectorField.Card
    };

    /// <summary>
    ///     True when the export validator would refuse the package over this
    ///     issue — i.e. the finding is not advice, it is a wall.
    ///     <para>
    ///         Paired with the twelve <c>ChartExportValidator.Key*</c> constants
    ///         that produce an <c>ExportFindingSeverity.Error</c>. Four of them
    ///         (<c>KeyNoBeatMap</c>, <c>KeyNoArrangements</c>, <c>KeyNoNotes</c>,
    ///         <c>KeyTooManyTones</c>) are project-level and have no per-sound
    ///         editing counterpart, and <c>KeyLyricTooLong</c> is a vocal track,
    ///         so eight issue types map here.
    ///     </para>
    ///     <para>
    ///         <b>Known divergence, carried deliberately:</b> <c>L01</c> /
    ///         <see cref="ArrangementIssueType.NoteFretBelowCapo" /> now covers
    ///         chord notes in BOTH validators (the export validator was taught to
    ///         flag a chord note below the capo on the branch base), so marking
    ///         the chord-note case as blocking is TRUE.
    ///     </para>
    /// </summary>
    public static bool BlocksExport(ArrangementIssueType type) => type switch
    {
        // ChartExportValidator.KeyNoteBelowCapo
        ArrangementIssueType.NoteFretBelowCapo => true,
        // KeyFretOutOfRange
        ArrangementIssueType.FretNumberMoreThan24 => true,
        // KeyStringOutOfRange
        ArrangementIssueType.InvalidBassArrangementString => true,
        // KeyOverlappingNotes
        ArrangementIssueType.OverlappingNotes => true,
        // KeyMissingFhpCoverage
        ArrangementIssueType.NoteWithoutAnchor => true,
        ArrangementIssueType.NoteOutsideAnchorRange => true,
        // KeyTooManyPhrases
        ArrangementIssueType.MoreThan100Phrases => true,
        // KeyFirstPhraseNotAtStart
        ArrangementIssueType.FirstPhraseNotEmpty => true,
        _ => false
    };

    /// <summary>The twelve export keys that produce an ERROR. Kept next to
    /// <see cref="BlocksExport" /> so the pairing test can assert against the
    /// validator's own constants rather than a copy of the list.</summary>
    public static IReadOnlyList<string> BlockingExportKeys { get; } = new[]
    {
        ChartExportValidator.KeyNoBeatMap,
        ChartExportValidator.KeyNoArrangements,
        ChartExportValidator.KeyNoNotes,
        ChartExportValidator.KeyMissingFhpCoverage,
        ChartExportValidator.KeyOverlappingNotes,
        ChartExportValidator.KeyTooManyPhrases,
        ChartExportValidator.KeyFirstPhraseNotAtStart,
        ChartExportValidator.KeyTooManyTones,
        ChartExportValidator.KeyFretOutOfRange,
        ChartExportValidator.KeyNoteBelowCapo,
        ChartExportValidator.KeyStringOutOfRange,
        ChartExportValidator.KeyLyricTooLong
    };
}
