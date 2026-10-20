namespace DotCharter.Core.Domain.Validation;

/// <summary>
///     Catalog of issues that the <c>ArrangementValidationService</c> can detect on an arrangement.
///     Codes mirror the DLC Builder / Rocksmith2014.NET checker codes (I-series, V-series, S-series)
///     so that operators familiar with that toolchain immediately recognize them.
/// </summary>
public enum ArrangementIssueType
{
    // Link next family
    LinkNextMissingTargetNote,
    LinkNextSlideMismatch,
    LinkNextFretMismatch,
    LinkNextBendMismatch,
    IncorrectLinkNext,
    UnpitchedSlideWithLinkNext,
    PhraseChangeOnLinkNextNote,
    MissingLinkNextChordNotes,

    // Harmonics / bends
    DoubleHarmonic,
    NaturalHarmonicWithBend,
    SeventhFretHarmonicWithSustain,
    MissingBendValue,
    OverlappingBendValues,

    // Anchors / hand shapes
    AnchorInsideHandShape,
    AnchorInsideHandShapeAtPhraseBoundary,

    /// <summary>A populated phrase whose notes are governed by an anchor that
    /// began in an EARLIER phrase. Ported from Editor on Fire
    /// (eof_enforce_rs_phrase_begin_with_fret_hand_position, EOF_SRC/rs.c:5035),
    /// which refuses a clean save over it. Rocksmith rebuilds each phrase
    /// independently for dynamic difficulty, so an anchor that merely spans the
    /// boundary is not carried in — the phrase is played with no hand position.
    /// The fix restates the SAME fret at the phrase start, so nothing changes
    /// visually or musically; it only stops the anchor crossing.</summary>
    AnchorMissingAtPhraseStart,

    /// <summary>A bend authored on an open string. Ported from Editor on Fire
    /// ("One or more open notes are defined as bends"), which blocks the save:
    /// a Rocksmith bend is expressed relative to a fretted note, so an open
    /// string has nothing to bend from and the game has no defined behaviour
    /// for it.</summary>
    OpenNoteWithBend,

    // Phrases / sections
    FirstPhraseNotEmpty,
    NoEndPhrase,
    MoreThan100Phrases,
    NoteInsideNoguitarSection,
    NoteAfterSongEnd,
    ToneChangeOnNote,

    // Note technique / fretting
    HopoIntoSameNote,
    FretNumberMoreThan24,
    InvalidBassArrangementString,
    TechniqueNoteWithoutSustain,
    MutedStringInNonMutedChord,

    // Anchors / fingering (DLC Builder parity)
    NoteLinkedToChord,
    PositionShiftIntoPullOff,
    FingerChangeDuringSlide,
    PossiblyWrongChordFingering,
    BarreOverOpenStrings,
    FingeringAnchorMismatch,
    AnchorCloseToUnpitchedSlide,

    // Capo / anchor coverage (Log Charter parity)
    NoteFretBelowCapo,
    AnchorOnOrBelowCapo,
    AnchorTopFretTooHigh,
    NoteWithoutAnchor,
    NoteOutsideAnchorRange,

    // Chord templates (Log Charter parity)
    FingerSetForOpenString,
    FingerMissingOnFrettedString,
    FirstFingerNotOnLowestFret,

    /// <summary>A chord fingered with the index finger on a fret other than
    /// the active anchor's. Ported from Editor on Fire (EOF_SRC/song.c:12577),
    /// which refuses a clean save over it: the FHP fret IS the index finger's
    /// position, and Rocksmith derives the playing finger from it as
    /// 1 + (fret - anchor.fret) - (anchor.width - 4), so a disagreement makes
    /// the game show a fingering the chart does not state.</summary>
    IndexFingerNotOnAnchorFret,

    /// <summary>A sound played inside a hand shape that plays a string the
    /// shape does not declare, or declares at another fret. Ported from Editor
    /// on Fire (EOF_SRC/song.c:12596-12612, its -1). The shape is what the game
    /// draws the hand from for the whole span, so a note it cannot reach is
    /// shown one way and asked for another. Measured at 0.01% on the 98
    /// hand-finished exports in TestData/EOF, so it fires on mistakes rather
    /// than on ordinary arpeggio charting.</summary>
    SoundViolatesHandShape,

    // Slides / HOPO techniques (Log Charter + EOF parity)
    SlideFromOpenString,
    PitchedSlideNotLinked,
    SlideEndsOnStartFret,
    HammerOnOnOpenString,
    TapOnOpenString,
    PullOffWithoutPrecedingNote,
    PullOffOntoHigherOrEqualFret,

    // Bend limits (EOF crash guards)
    BendStrengthTooHigh,
    TooManyBendPoints,

    // Structural overlaps (Log Charter parity)
    OverlappingNotes,
    OverlappingAnchors,
    OverlappingHandShapes,

    // Pre-export gates surfaced live in the FEHLER inspector (DotCharter native,
    // D-series). Editing-time ports of the project-level ChartExportValidator so
    // tone/silence/phrase problems show up with click-to-select while editing.
    ToneChangeWithoutMatchingTone,
    FirstNoteTooEarly,
    EmptyPhrase,

    /// <summary>
    ///     The tone change names a tone the arrangement DECLARES, but the project
    ///     owns no BODY for it (<c>ChartProject.ToneDefinitions</c>) — so
    ///     there is nothing to build a rig from and the change plays SILENTLY.
    ///     <para>
    ///         Distinct from <see cref="ToneChangeWithoutMatchingTone" />, which is
    ///         a chart that contradicts itself. This one is internally consistent
    ///         and still silent, which is why it went unreported: the rule only
    ///         ever compared tone changes against the arrangement's slots, so
    ///         every psarc-opened chart — none of which carried bodies at all —
    ///         validated perfectly clean while not one of its tone changes could
    ///         be heard.
    ///     </para>
    /// </summary>
    ToneChangeWithoutDefinition,

    /// <summary>
    ///     No audio is attached, so the "after the end of the song audio" check
    ///     (<see cref="NoteAfterSongEnd" />, I29) cannot run at all. ONE
    ///     summarized, non-positional issue per arrangement with content —
    ///     never one per note.
    ///     <para>
    ///         It exists because the per-note rule compared against a raw
    ///         <c>TotalAudioLengthMs</c> where <b>0 means "no audio decoded",
    ///         not "the song ends at 0:00"</b> — so an audio-less GP import
    ///         flooded the inspector with one I29 for EVERY sound (a measured
    ///         1,011 duplicates burying every real finding). The validation
    ///         boundary now normalizes ≤ 0 to null and this rule states the
    ///         honest fact once. Auto-Fix deliberately does not touch it:
    ///         nothing mechanical fixes "attach your audio".
    ///     </para>
    /// </summary>
    SongEndUnverifiedNoAudio,

    /// <summary>
    ///     The arrangement could not be checked AT ALL because it has no beat
    ///     grid. Reported rather than swallowed: every rule positions its finding
    ///     on the beat map, so with none the validator has nothing to say — and
    ///     "nothing to say" and "nothing wrong" must never render the same. The
    ///     export validator already treats this state as a hard error, so a
    ///     silent inspector put the two in open disagreement.
    /// </summary>
    NoBeatGridSoNoChecksRan,

    /// <summary>
    ///     One validation rule threw, so whatever it checks went UNCHECKED. A log line
    ///     alone would leave the user a shorter list with
    ///     no marker, which is the same false-negative class as
    ///     <see cref="NoBeatGridSoNoChecksRan" /> one level in.
    /// </summary>
    ValidationRuleCrashed,

    /// <summary>
    ///     A chord names no chord template AND the arrangement declares no
    ///     template for the shape its notes play, so the fingering checks
    ///     (I44/I45, L06–L08) cannot judge it at all. ONE summarized,
    ///     non-positional issue per arrangement — never one per chord.
    ///     <para>
    ///         It exists because every rule that read <c>chord.TemplateId</c>
    ///         guarded it with <c>&gt;= 0</c>, turning a missing input into a
    ///         clean bill of health. A chord on the <c>-1</c> sentinel usually
    ///         still HAS a resolvable shape —
    ///         <c>ArrangementValidationContext.ChordTemplateFor</c> finds the
    ///         template <c>ChartSpacingEnforcer</c> will repoint it at on the
    ///         next save, and the chord is judged normally. This code is the
    ///         residue: the chords for which even that cannot answer, stated as
    ///         "cannot judge" rather than passed off as "no issue".
    ///     </para>
    /// </summary>
    ChordWithoutTemplate,

    /// <summary>
    ///     A hand shape names no chord template, so it declares no grip: it
    ///     exports with <c>chordId -1</c> and neither shape check (I46, E05) can
    ///     judge it. ONE summarized, non-positional issue per arrangement.
    ///     <para>
    ///         The two rules that judge a shape must AGREE about this
    ///         input. <c>HandShapeRule</c> would look the id up with no negative
    ///         guard, so on <c>TemplateId == -1</c> — the field's own default —
    ///         it would judge the shape against whichever UNNUMBERED template sat
    ///         first in the collection (a false positive), while
    ///         <c>HandShapeContentRule</c> would use
    ///         <c>ChordTemplateResolver.FindById</c>, get null and skip (a
    ///         false negative). Both therefore ask
    ///         <c>ArrangementValidationContext.HandShapeTemplate</c>, and the
    ///         null case is stated rather than swallowed.
    ///     </para>
    /// </summary>
    HandShapeWithoutTemplate,

    /// <summary>
    ///     A phrase name Rocksmith does not accept (it allows only
    ///     <c>[a-zA-Z0-9 _#]</c>), so the export writes a different spelling —
    ///     <c>Export/Rocksmith/RsPhraseName</c> transliterates it, drops what is
    ///     left, and falls back to <c>phrase{n}</c> when nothing is. The CHART
    ///     keeps the charter's name; this only says what the game will show.
    ///     Advice, never a wall: the export succeeds.
    /// </summary>
    PhraseNameRewrittenForRocksmith,

    /// <summary>
    ///     A chord that no hand shape covers (the half-open span
    ///     <c>[start, end)</c>, judged per difficulty level). The export makes up no
    ///     grip for it — <c>SngFromDomainMapper</c> turns only the shapes that exist
    ///     into fingerprints — so the chord ships with no chord frame and no name, and
    ///     the repeat-chord density rule (<c>RsChordDensity</c>, which reads the
    ///     level's shapes) cannot see it. ONE finding per chord POSITION, however many
    ///     difficulty levels repeat it.
    ///     <para>
    ///         Measured: 0 of 50,709 chords in the owner's 190 verified
    ///         arrangements, 0 of 27,016 in <c>TestData/EOF</c> (EOF builds the shapes
    ///         itself), 670 of 27,717 (2.42%) in <c>TestData/RsCharter</c>, and 247 of
    ///         1,122,295 (0.022%, 9 of 691 arrangements, 46 findings) in the official
    ///         psarcs. EVERY one of those 917 is a chord sitting exactly ON the end of
    ///         the shape before it — the case DLC Builder reports as I19
    ///         ("a chord exactly at the end of a handshape looks ugly") and fixes by
    ///         lengthening the shape. Hence a Warning: on finished charts this is
    ///         cosmetic; a chord far from any shape has not been seen in any corpus.
    /// Auto-fix covers them with the "Generate hand shapes" planner.
    ///     </para>
    /// </summary>
    ChordOutsideHandShape
}

/// <summary>
///     Severity hint used by the inspector to colour-code issues. Mirrors the
///     "important" filter from <c>IssueViewer.fs</c> in DLC Builder.
/// </summary>
public enum ArrangementIssueSeverity
{
    Warning,
    Important
}

public static class ArrangementIssueTypeExtensions
{
    /// <summary>
    ///     Returns the short code that identifies the issue type (e.g. <c>"I04"</c>).
    /// </summary>
    public static string GetCode(this ArrangementIssueType type)
    {
        return type switch
        {
            ArrangementIssueType.LinkNextMissingTargetNote => "I04",
            ArrangementIssueType.LinkNextSlideMismatch => "I05",
            ArrangementIssueType.LinkNextFretMismatch => "I06",
            ArrangementIssueType.LinkNextBendMismatch => "I07",
            ArrangementIssueType.IncorrectLinkNext => "I08",
            ArrangementIssueType.UnpitchedSlideWithLinkNext => "I09",
            ArrangementIssueType.PhraseChangeOnLinkNextNote => "I10",
            ArrangementIssueType.DoubleHarmonic => "I11",
            ArrangementIssueType.NaturalHarmonicWithBend => "I12",
            ArrangementIssueType.SeventhFretHarmonicWithSustain => "I13",
            ArrangementIssueType.MissingBendValue => "I14",
            ArrangementIssueType.OverlappingBendValues => "I34",
            ArrangementIssueType.AnchorInsideHandShape => "I20",
            ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary => "I21",
            ArrangementIssueType.FirstPhraseNotEmpty => "I25",
            ArrangementIssueType.NoEndPhrase => "I26",
            ArrangementIssueType.MoreThan100Phrases => "I27",
            ArrangementIssueType.NoteInsideNoguitarSection => "I28",
            ArrangementIssueType.NoteAfterSongEnd => "I29",
            ArrangementIssueType.ToneChangeOnNote => "I30",
            ArrangementIssueType.HopoIntoSameNote => "I31",
            ArrangementIssueType.FretNumberMoreThan24 => "I38",
            ArrangementIssueType.InvalidBassArrangementString => "I39",
            ArrangementIssueType.TechniqueNoteWithoutSustain => "I40",
            ArrangementIssueType.MissingLinkNextChordNotes => "I33",
            ArrangementIssueType.MutedStringInNonMutedChord => "I32",
            ArrangementIssueType.NoteLinkedToChord => "I41",
            ArrangementIssueType.PositionShiftIntoPullOff => "I42",
            ArrangementIssueType.FingerChangeDuringSlide => "I43",
            ArrangementIssueType.PossiblyWrongChordFingering => "I44",
            ArrangementIssueType.BarreOverOpenStrings => "I45",
            ArrangementIssueType.FingeringAnchorMismatch => "I46",
            ArrangementIssueType.AnchorCloseToUnpitchedSlide => "I47",
            // L## = Log Charter parity, E## = Editor on Fire parity
            ArrangementIssueType.NoteFretBelowCapo => "L01",
            ArrangementIssueType.AnchorOnOrBelowCapo => "L02",
            ArrangementIssueType.AnchorTopFretTooHigh => "L03",
            ArrangementIssueType.NoteWithoutAnchor => "L04",
            ArrangementIssueType.NoteOutsideAnchorRange => "L05",
            ArrangementIssueType.FingerSetForOpenString => "L06",
            ArrangementIssueType.FingerMissingOnFrettedString => "L07",
            ArrangementIssueType.FirstFingerNotOnLowestFret => "L08",
            ArrangementIssueType.SlideFromOpenString => "L09",
            ArrangementIssueType.PitchedSlideNotLinked => "L10",
            ArrangementIssueType.HammerOnOnOpenString => "L11",
            ArrangementIssueType.TapOnOpenString => "L12",
            ArrangementIssueType.PullOffWithoutPrecedingNote => "L13",
            ArrangementIssueType.PullOffOntoHigherOrEqualFret => "L14",
            ArrangementIssueType.OverlappingNotes => "L15",
            ArrangementIssueType.OverlappingAnchors => "L16",
            ArrangementIssueType.OverlappingHandShapes => "L17",
            ArrangementIssueType.SlideEndsOnStartFret => "E01",
            ArrangementIssueType.BendStrengthTooHigh => "E02",
            ArrangementIssueType.TooManyBendPoints => "E03",
            ArrangementIssueType.IndexFingerNotOnAnchorFret => "E04",
            ArrangementIssueType.SoundViolatesHandShape => "E05",
            ArrangementIssueType.AnchorMissingAtPhraseStart => "E06",
            ArrangementIssueType.OpenNoteWithBend => "E07",
            // D## = DotCharter native pre-export gates (live in the FEHLER inspector)
            ArrangementIssueType.ToneChangeWithoutMatchingTone => "D01",
            ArrangementIssueType.FirstNoteTooEarly => "D02",
            ArrangementIssueType.EmptyPhrase => "D03",
            ArrangementIssueType.ToneChangeWithoutDefinition => "D04",
            ArrangementIssueType.SongEndUnverifiedNoAudio => "D05",
            ArrangementIssueType.NoBeatGridSoNoChecksRan => "D06",
            ArrangementIssueType.ValidationRuleCrashed => "D07",
            ArrangementIssueType.ChordWithoutTemplate => "D08",
            ArrangementIssueType.HandShapeWithoutTemplate => "D09",
            ArrangementIssueType.PhraseNameRewrittenForRocksmith => "D10",
            ArrangementIssueType.ChordOutsideHandShape => "D11",
            _ => type.ToString()
        };
    }

    public static ArrangementIssueSeverity GetSeverity(this ArrangementIssueType type)
    {
        return type switch
        {
            ArrangementIssueType.LinkNextMissingTargetNote
                or ArrangementIssueType.LinkNextSlideMismatch
                or ArrangementIssueType.LinkNextFretMismatch
                or ArrangementIssueType.LinkNextBendMismatch
                or ArrangementIssueType.IncorrectLinkNext
                or ArrangementIssueType.UnpitchedSlideWithLinkNext
                or ArrangementIssueType.PhraseChangeOnLinkNextNote
                or ArrangementIssueType.MissingLinkNextChordNotes
                or ArrangementIssueType.MissingBendValue
                or ArrangementIssueType.OverlappingBendValues
                or ArrangementIssueType.AnchorInsideHandShape
                or ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary
                or ArrangementIssueType.AnchorMissingAtPhraseStart
                or ArrangementIssueType.OpenNoteWithBend
                or ArrangementIssueType.FirstPhraseNotEmpty
                or ArrangementIssueType.MutedStringInNonMutedChord
                or ArrangementIssueType.NoteLinkedToChord
                or ArrangementIssueType.BarreOverOpenStrings
                or ArrangementIssueType.InvalidBassArrangementString
                or ArrangementIssueType.NoteFretBelowCapo
                or ArrangementIssueType.AnchorTopFretTooHigh
                or ArrangementIssueType.NoteWithoutAnchor
                or ArrangementIssueType.BendStrengthTooHigh
                or ArrangementIssueType.TooManyBendPoints
                or ArrangementIssueType.OverlappingNotes
                or ArrangementIssueType.OverlappingAnchors
                or ArrangementIssueType.OverlappingHandShapes
                or ArrangementIssueType.ToneChangeWithoutMatchingTone
                // Important, not Warning: the chart is internally consistent and
                // still plays nothing at this marker. There is no louder symptom
                // to notice it by.
                or ArrangementIssueType.ToneChangeWithoutDefinition
                // Important, not Warning: an UNCHECKED arrangement is not a clean
                // one, and this group is what says so — the whole pass (D06), one
                // rule (D07), or the chords and shapes no rule could judge
                // (D08/D09). A silent skip would read as a clean arrangement.
                or ArrangementIssueType.NoBeatGridSoNoChecksRan
                or ArrangementIssueType.ValidationRuleCrashed
                or ArrangementIssueType.ChordWithoutTemplate
                or ArrangementIssueType.HandShapeWithoutTemplate => ArrangementIssueSeverity.Important,
            _ => ArrangementIssueSeverity.Warning
        };
    }
}
