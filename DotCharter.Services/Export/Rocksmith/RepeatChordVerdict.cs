namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     WHY a chord does or does not ship as Rocksmith's repeat box — the answer
///     <see cref="RepeatChords" /> gives, one reason per chord. The rule
///     (<see cref="RsChordDensity" />) RETURNS these; its yes/no set is derived from
///     them (<see cref="RepeatChordVerdict.Ships" />), so the inspector's line can
///     never disagree with the export. Order of the members is not meaningful; the
///     rule's own order decides which reason a chord gets when several apply.
/// </summary>
public enum RepeatChordReason
{
    /// <summary>Ships as a repeat: the same shape as the strum before it.</summary>
    Repeat,

    /// <summary>Ships as a repeat because the charter set "No notes" (<see cref="Chord.ForceNoNotes" />).</summary>
    ForcedNoNotes,

    /// <summary>The level's first chord: there is no previous strum to repeat.</summary>
    FirstChord,

    /// <summary>The previous chord is a different chord template (as the export writes ids).</summary>
    DifferentTemplate,

    /// <summary>Fewer than two strings sound: not a chord to the game.</summary>
    SingleString,

    /// <summary>The chord links into the next sound.</summary>
    LinkNext,

    /// <summary>A string slides (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Slide,

    /// <summary>A string has vibrato (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Vibrato,

    /// <summary>A string has tremolo (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Tremolo,

    /// <summary>A string is a hammer-on, pull-off or tap (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Hopo,

    /// <summary>A string is a harmonic (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Harmonic,

    /// <summary>A string is slapped or popped (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    BassPicking,

    /// <summary>A string bends (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    Bend,

    /// <summary>"Pass other" (EOF's crazy status) is set, which keeps the full chord box.</summary>
    PassOtherNotes,

    /// <summary>A string has an explicit sustain (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    ExplicitSustain,

    /// <summary>One string's accent differs from the chord's (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    PerStringAccent,

    /// <summary>One string's ignore flag differs from the chord's (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    PerStringIgnore,

    /// <summary>Muted on some strings and not others (<see cref="RepeatChordVerdict.StringIndex" />).</summary>
    MixedMute,

    /// <summary>No sound comes before it in the level.</summary>
    NoEarlierSound,

    /// <summary>The sound right before it is a chord slide.</summary>
    PreviousIsChordSlide,

    /// <summary>The first sound in its hand shape always shows the full chord.</summary>
    FirstInHandShape,

    /// <summary>The chord it is compared with frets the strings differently.</summary>
    DifferentFrets,

    /// <summary>A single note sits between it and the previous chord.</summary>
    PreviousSoundIsNote,

    /// <summary>More than the repeat threshold after the previous sound ends.</summary>
    TooFarFromPrevious,

    /// <summary>A linked note lands on it, so the export writes it as single notes.</summary>
    LinkLanding,

    /// <summary>
    ///     "Split notes" (<see cref="Chord.SplitIntoNotes" />): the export writes it as single notes, so the
    /// game never sees a chord here — and the chords around it are judged as if notes stood here.
    /// </summary>
    SplitIntoNotes,

    /// <summary>No live template and no notes to synthesize one from, so the export drops it.</summary>
    UnresolvedTemplate
}

/// <summary>One chord's answer: the reason, and for a per-string reason the MODEL string index (else -1).</summary>
public readonly record struct RepeatChordVerdict(RepeatChordReason Reason, int StringIndex = -1)
{
    /// <summary>True when the chord ships WITHOUT chord notes — the game's repeat box.</summary>
    public bool Ships => Reason is RepeatChordReason.Repeat or RepeatChordReason.ForcedNoNotes;
}
