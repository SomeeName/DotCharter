using System.Text.Json.Serialization;

namespace DotCharter.Services.Editing.Selection.Patterns;

/// <summary>Which kinds of chart element a pattern looks at.</summary>
[Flags]
public enum PatternScope
{
    None = 0,
    Notes = 1,
    Chords = 2,
    HandShapes = 4,
    Anchors = 8,

    /// <summary>Notes and chords together — the usual musical scope.</summary>
    Sounds = Notes | Chords
}

/// <summary>
///     Whether each selected element is its own template, or the selection is one
///     ordered RUN (a lick) that must reappear intact.
/// </summary>
public enum PatternGrouping
{
    /// <summary>Every seed element matches independently.</summary>
    Individually,

    /// <summary>The whole seed selection is one sequence; a match is a whole
    ///     re-occurrence of it. Sounds only — hand shapes and anchors are always
    ///     matched individually.</summary>
    AsRun
}

/// <summary>How much the fret numbers have to agree.</summary>
public enum FretRule
{
    /// <summary>Frets do not participate at all.</summary>
    Ignore,

    /// <summary>The same absolute frets — the same place on the neck.</summary>
    Exact,

    /// <summary>
    ///     The same SHAPE anywhere on the neck: frets are compared after
    ///     subtracting the lowest FRETTED fret of the element (or of the whole
    ///     run). An open string is never transposed — fret 0 only ever matches
    ///     fret 0, because a capo-less open string cannot move with the hand.
    /// </summary>
    Transposable
}

/// <summary>How much the STRING layout has to agree — the fret rule's twin.</summary>
public enum StringRule
{
    /// <summary>Strings do not participate: the same frets on any strings.</summary>
    Ignore,

    /// <summary>The same strings.</summary>
    Exact,

    /// <summary>
    ///     The same string INTERVALS, anywhere across the neck — a shape moved
    ///     from strings 0/1/2 to 2/3/4 is the same grip. Strings are compared
    ///     after subtracting the element's own lowest string, exactly as
    ///     <see cref="FretRule.Transposable" /> does for frets.
    /// </summary>
    Relative
}

/// <summary>How the internal spacing of a RUN has to agree.</summary>
public enum RhythmRule
{
    /// <summary>Only the order matters; any spacing will do.</summary>
    Ignore,

    /// <summary>Identical spacing — the lick keeps its rhythm exactly.</summary>
    Exact,

    /// <summary>
    ///     The same rhythm at any TEMPO: every gap scaled by one constant, so a
    ///     lick and its half-time or double-time restatement match. The ratio is
    ///     taken from the first non-zero gap and every later gap must honour it.
    /// </summary>
    Proportional
}

/// <summary>How much a sound's length has to agree.</summary>
public enum SustainRule
{
    Ignore,

    /// <summary>Exactly the same length.</summary>
    SameLength,

    /// <summary>
    ///     Only whether it is HELD at all — a struck note and a ringing one are
    ///     different gestures, but two ringing notes of different lengths usually
    ///     are not.
    /// </summary>
    SameKind
}

/// <summary>Where in the arrangement a pattern is allowed to look.</summary>
public enum PatternRange
{
    /// <summary>The whole arrangement.</summary>
    Anywhere,

    /// <summary>Only inside regions whose SECTION name matches the seed's —
    ///     "every chorus", and nothing else.</summary>
    SameSection,

    /// <summary>Only inside regions whose PHRASE name matches the seed's.</summary>
    SamePhrase
}

/// <summary>
///     Which techniques have to agree between a seed and a candidate. Each flag
///     is compared as the sound's AGGREGATE — the sorted distinct set of that
///     value across the sound's strings — so a chord is compared by the technique
///     profile it carries rather than string by string. For a single note the
///     aggregate is just its own value.
/// </summary>
[Flags]
public enum TechniqueFacet
{
    None = 0,
    Mute = 1 << 0,
    Hopo = 1 << 1,
    Harmonic = 1 << 2,
    Bend = 1 << 3,
    Slide = 1 << 4,
    Vibrato = 1 << 5,
    Tremolo = 1 << 6,
    Accent = 1 << 7,
    LinkNext = 1 << 8,
    BassPicking = 1 << 9,
    Ignored = 1 << 10,
    PassOtherNotes = 1 << 11,

    All = Mute | Hopo | Harmonic | Bend | Slide | Vibrato | Tremolo | Accent | LinkNext | BassPicking |
          Ignored | PassOtherNotes
}

/// <summary>
///     Related elements a match drags along with it. This is what turns a pattern
///     from a selection into an EDITING unit: a chord, the hand shape drawn over
///     it and the fret-hand position holding it are one authored decision, and
///     re-fingering or re-fretting the chord alone leaves the other two describing
///     something that is gone.
/// </summary>
[Flags]
public enum PatternCompanions
{
    None = 0,

    /// <summary>The hand shape whose span covers a matched sound.</summary>
    CoveringHandShape = 1,

    /// <summary>The FHP anchor whose span a matched sound falls in.</summary>
    GoverningAnchor = 2,

    /// <summary>The sounds inside a matched hand shape or anchor span — the
    ///     inverse direction, for a pattern seeded from the shape rather than
    ///     from the notes.</summary>
    SoundsInside = 4,

    All = CoveringHandShape | GoverningAnchor | SoundsInside
}

/// <summary>
///     A user-authored, named, keybindable definition of what "alike" means.
///     <para>
///         Without this, "alike" would be three matchers hard-coded in C# behind ONE
///         keybind: <c>SelectLikeService.FindMatches</c> for notes,
///         <see cref="ChordShapeSignature" /> for chords and hand shapes,
///         <see cref="AnchorSpanSignature" /> for anchors. Each encodes a
///         specific, defensible answer — but only one answer, with no
///         way to ask a different question or to find out which question is
///         actually the useful one while charting. This type is that question,
///         made data: a set of FACETS that must agree, plus the keys that run it.
///     </para>
///     <para>
///         Two verbs run a pattern, and they are deliberately different jobs:
///         <b>select</b> adds every match in the arrangement to the selection,
///         <b>next/previous</b> moves the playhead to one match at a time so a
///         pattern can NAVIGATE rather than bulk-edit. The second
///         is the one this repo had no equivalent of at all.
///     </para>
///     <para>
///         A plain mutable class with a parameterless constructor on purpose: it
///         round-trips through <c>AppConfig.MatchPatterns</c> as
///         System.Text.Json, and every facet must be ABSENT-tolerant so a config
///         written by an older build still loads (a missing flag reads as its
///         default, never as an exception).
///     </para>
/// </summary>
public sealed class MatchPattern
{
    /// <summary>Stable identity, used by nothing but the store's upsert/remove.
    ///     Names are the user's and may repeat or change.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public PatternScope Scope { get; set; } = PatternScope.Sounds;

    public PatternGrouping Grouping { get; set; } = PatternGrouping.Individually;

    public FretRule Fret { get; set; } = FretRule.Exact;

    public StringRule Strings { get; set; } = StringRule.Exact;

    /// <summary>The same number of strings, wherever they are. The weaker half of
    ///     <see cref="StringRule.Exact" /> — "any three-note shape".</summary>
    public bool MatchSize { get; set; } = true;

    /// <summary>A note only matches a note and a chord only a chord. Off means a
    ///     chord and the same pitches spelled as loose notes are interchangeable,
    ///     which is what a hand cares about.</summary>
    public bool MatchSoundKind { get; set; } = true;

    public SustainRule Sustain { get; set; } = SustainRule.Ignore;

    /// <summary>
    ///     The template fingering must not CONFLICT — fingers only have to agree
    ///     on strings where both sides specify one, exactly as in
    ///     <see cref="ChordShapeSignature" />. An unauthored finger is a hole, not
    ///     a value, or the fingering workflow could never select the unfingered
    ///     twins it exists to fill in.
    /// </summary>
    public bool MatchFingering { get; set; }

    /// <summary>The chord/hand-shape template NAME must be equal.</summary>
    public bool MatchChordName { get; set; }

    /// <summary>
    ///     The template's ARPEGGIO flag must agree. Load-bearing rather than
    ///     cosmetic: it sets <c>ChordMask.Arpeggio</c> on export, picks the
    ///     <c>.eof</c> arpeggio section type, and draws purple brackets instead of
    ///     a hand-shape bar — so an arpeggio and a plain strum of the same frets
    ///     are two different authored things.
    /// </summary>
    public bool MatchArpeggio { get; set; }

    /// <summary>Only meaningful for <see cref="PatternGrouping.AsRun" />.</summary>
    public RhythmRule Rhythm { get; set; } = RhythmRule.Exact;

    /// <summary>
    ///     The sound must sit at the same place WITHIN its beat — every note on
    ///     the "and", every chord on the downbeat. Purely the fractional part of
    ///     the position, so it needs no beat map and no time signature.
    /// </summary>
    public bool MatchBeatOffset { get; set; }

    public TechniqueFacet Techniques { get; set; } = TechniqueFacet.None;

    /// <summary>An anchor's hand SPAN (width) must agree. Load-bearing on export:
    ///     Rocksmith derives the playing finger as
    ///     <c>1 + (fret - anchor.fret) - (anchor.width - 4)</c>.</summary>
    public bool MatchAnchorWidth { get; set; } = true;

    /// <summary>The run of sounds the anchor governs must agree. This is the
    ///     facet that stops "every anchor on fret 7" — there are many reasons to
    ///     park the hand at one fret, and re-fretting all of them because one was
    ///     wrong corrupts passages the user never looked at.</summary>
    public bool MatchAnchorSpan { get; set; } = true;

    /// <summary>Where the pattern is allowed to look.</summary>
    public PatternRange Range { get; set; } = PatternRange.Anywhere;

    /// <summary>
    ///     Refuse a match whose lowest fret is more than this many frets from the
    ///     seed's. <c>0</c> is off. Pairs with
    ///     <see cref="FretRule.Transposable" />, which otherwise reaches the whole
    ///     neck: "the same grip, but only around here".
    /// </summary>
    public int MaxFretDistance { get; set; }

    /// <summary>Related elements every match drags along with it.</summary>
    public PatternCompanions Companions { get; set; } = PatternCompanions.None;

    /// <summary>Add every match to the selection.</summary>
    public KeyChord SelectKey { get; set; } = KeyChord.None;

    /// <summary>Move the playhead to the next match after it.</summary>
    public KeyChord NextKey { get; set; } = KeyChord.None;

    /// <summary>Move the playhead to the previous match before it.</summary>
    public KeyChord PreviousKey { get; set; } = KeyChord.None;

    // --- 0.39.0 compatibility ------------------------------------------------
    // Three facets shipped as booleans and became tri-states one build later.
    // They are read back so a pattern authored against that build still MEANS
    // what it said (in particular "Same bend anywhere", whose whole point is
    // MatchStrings=false), and are never written again: nullable + WhenWritingNull
    // keeps them out of every config this build saves. Delete them once no
    // 0.39.0 config can plausibly survive.

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MatchStrings { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MatchRhythm { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MatchSustain { get; set; }

    /// <summary>True when at least one verb is reachable from the keyboard.</summary>
    [JsonIgnore]
    public bool HasAnyKey => SelectKey.IsBound || NextKey.IsBound || PreviousKey.IsBound;

    public MatchPattern Clone() => new()
    {
        Id = Id,
        Name = Name,
        Scope = Scope,
        Grouping = Grouping,
        Fret = Fret,
        Strings = Strings,
        MatchSize = MatchSize,
        MatchSoundKind = MatchSoundKind,
        Sustain = Sustain,
        MatchFingering = MatchFingering,
        MatchChordName = MatchChordName,
        MatchArpeggio = MatchArpeggio,
        Rhythm = Rhythm,
        MatchBeatOffset = MatchBeatOffset,
        Techniques = Techniques,
        MatchAnchorWidth = MatchAnchorWidth,
        MatchAnchorSpan = MatchAnchorSpan,
        Range = Range,
        MaxFretDistance = MaxFretDistance,
        Companions = Companions,
        SelectKey = SelectKey,
        NextKey = NextKey,
        PreviousKey = PreviousKey
    };

    /// <summary>
    ///     A stored-shape copy: an id and a name are guaranteed, the 0.39.0
    ///     booleans are folded into their tri-states and dropped, and a scope of
    ///     <see cref="PatternScope.None" /> is repaired to
    ///     <see cref="PatternScope.Sounds" />. An empty scope would match nothing
    ///     forever while looking perfectly configured, which is the one way this
    ///     type can silently do nothing.
    /// </summary>
    public MatchPattern Normalized()
    {
        // Fold BEFORE cloning — Clone deliberately does not carry the legacy
        // fields, so folding after would drop them.
        if (MatchStrings == false) Strings = StringRule.Ignore;
        if (MatchRhythm == false) Rhythm = RhythmRule.Ignore;
        if (MatchSustain == true) Sustain = SustainRule.SameLength;

        var copy = Clone();
        if (string.IsNullOrWhiteSpace(copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Pattern" : copy.Name.Trim();
        if (copy.Scope == PatternScope.None) copy.Scope = PatternScope.Sounds;
        copy.MaxFretDistance = Math.Clamp(copy.MaxFretDistance, 0, 24);
        return copy;
    }
}
