namespace DotCharter.Services.Editing.Anchors;

/// <summary>
///     Where a chord's authored fingering puts the index finger — the one fret an
///     FHP anchor is not free to disagree with.
///     <para>
///         <b>The FHP fret IS the index finger's fret.</b> That is not a
///         convention: Rocksmith derives which finger plays a fret as
///         <c>1 + (fret − anchor.fret) − (anchor.width − 4)</c>, so an anchor that
///         disagrees makes the game show a different fingering than the chart
///         states.
///     </para>
///     <para>
///         This exists so the DETECTOR (inspector rule E04,
///         <c>IndexFingerAnchorRule</c>) and the CORRECTOR
///         (<c>AppConfig.SyncAnchorToChordFingering</c>) read the same fret. They
///         were one inlined loop and one new call site away from being able to
///         disagree — a fixer that "corrects" an anchor the rule still flags, or
///         flags one it just wrote, is worse than having neither.
///     </para>
/// </summary>
public static class IndexFingerPosition
{
    /// <summary>
    ///     The fret <paramref name="template" /> puts the index finger on — the
    ///     LOWEST of <see cref="Find" />'s frets — or null when its fingering
    ///     states no position.
    /// </summary>
    public static int? Of(ChordTemplate? template, int capo = 0) => Find(template, capo)?.Fret;

    /// <summary>
    ///     Where the fingering puts the index finger, and which finger says so.
    ///     <para>
    ///         <b>An authored finger 1 wins.</b> When several strings are fingered
    ///         1 — a barre — the LOWEST fret wins: a barre lies flat across one
    ///         fret, so a template naming two different frets for finger 1 is
    ///         already malformed, and the lowest is where the hand actually sits.
    ///         An OPEN string (at or below the capo) is stopped by the capo rather
    ///         than by a finger, so it has no position to state; EOF treats a
    ///         finger on an open string as its own separate fault, which
    ///         <c>TemplateAndOverlapRule</c> already reports. A template whose only
    ///         finger 1 sits on an open string states nothing — it DID author the
    ///         index, just malformed, so no other finger infers past it.
    ///     </para>
    ///     <para>
    /// <b>No finger 1 at all: the index is INFERRED</b> from the
    ///         LOWEST finger <i>f</i> (ties: its lowest fret <i>x</i>), the one
    ///         nearest the index; the thumb (0) and fingers on open strings say
    ///         nothing. A chord fingered 2-3-4 on fret 5 (an Am7 under FHP 5)
    ///         puts the index on fret 4 just as surely as a finger 1 would:
    ///         nothing sits between the index and the middle finger, so finger 2
    ///         on <i>x</i> means the index on <i>x − 1</i>.
    ///     </para>
    ///     <para>
    ///         <b>From the ring or the little finger it is a RANGE,</b>
    ///         <c>x − (f − 1) … x − 1</c>: one fret per finger at the low end, and
    ///         at the high end the squeeze charters use for a ring-and-pinky grip
    ///         on one fret — index one fret below it, not two. On the verified
    ///         corpus the point <c>x − (f − 1)</c> alone flagged that squeeze
    ///         hundreds of times where the sounds around it put the index on the
    ///         charter's anchor (measured in
    ///         <c>docs/agents/services/editing-invariants/anchors-and-capo.md</c>).
    ///         A range whose top is at or below the capo is no position an anchor
    ///         could take (the floor is <c>capo + 1</c>), so it states nothing.
    ///     </para>
    /// </summary>
    public static IndexFret? Find(ChordTemplate? template, int capo = 0)
    {
        if (template == null || template.Fingers.Count == 0) return null;

        var authorsIndex = false;
        int? lowestIndexFret = null;
        int? lowestFinger = null;
        var lowestFingerFret = 0;
        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (finger == 1) authorsIndex = true;
            if (finger is < 1 or > 4) continue;
            if (!template.Frets.TryGetValue(stringIndex, out var fret)) continue;
            if (fret <= capo) continue;

            if (finger == 1)
            {
                if (lowestIndexFret == null || fret < lowestIndexFret) lowestIndexFret = fret;
            }
            else if (lowestFinger == null || finger < lowestFinger || (finger == lowestFinger && fret < lowestFingerFret))
            {
                lowestFinger = finger;
                lowestFingerFret = fret;
            }
        }

        if (authorsIndex) return lowestIndexFret is { } index ? new IndexFret(index, index, 1, index) : null;
        if (lowestFinger is not { } f) return null;

        var low = Math.Max(lowestFingerFret - (f - 1), capo + 1);
        var high = lowestFingerFret - 1;
        return low <= high ? new IndexFret(low, high, f, lowestFingerFret) : null;
    }

    /// <summary>
    ///     True when <paramref name="anchor" /> contradicts the fingering — the
    ///     exact condition rule E04 reports.
    /// </summary>
    public static bool Disagrees(ChordTemplate? template, FretAnchor? anchor, int capo = 0) =>
        anchor != null && Find(template, capo) is { } index && !index.Allows(anchor.Fret);
}

/// <summary>
///     Where the index finger sits, frets <see cref="Fret" /> to
///     <see cref="Highest" /> (one fret unless inferred from the ring or little
///     finger), and the finger that states it: finger 1 on that very fret when
///     authored, else the lowest other finger and the fret it was inferred from
///     (<see cref="IndexFingerPosition.Find" />).
/// </summary>
public readonly record struct IndexFret(int Fret, int Highest, int FromFinger, int FromFret)
{
    /// <summary>True when no finger 1 is authored and the fret was worked out
    ///     from another finger.</summary>
    public bool Inferred => FromFinger != 1;

    /// <summary>True when an anchor on <paramref name="anchorFret" /> agrees.</summary>
    public bool Allows(int anchorFret) => anchorFret >= Fret && anchorFret <= Highest;

    /// <summary>The agreeing fret nearest <paramref name="anchorFret" /> — the
    ///     smallest move that ends the disagreement.</summary>
    public int Nearest(int anchorFret) => Math.Clamp(anchorFret, Fret, Highest);

    /// <summary>"fret 4", or "fret 6 or 7" / "frets 5 to 7" for a range.</summary>
    public string Frets => Highest == Fret ? $"fret {Fret}"
        : Highest == Fret + 1 ? $"fret {Fret} or {Highest}"
        : $"frets {Fret} to {Highest}";

    /// <summary>What the fingering states, saying when it was inferred.</summary>
    public string Describe() => Inferred
        ? $"no finger 1 is authored, but finger {FromFinger} on fret {FromFret} puts the index finger on {Frets} (inferred)"
        : $"the index finger is on fret {Fret}";
}
