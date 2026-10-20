using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Which chords Rocksmith draws as a COMPACT REPEAT rather than as a full
///     chord panel with its name — the second and later strums of one shape
///     inside a hand shape.
///     <para>
///         <b>The game keys this off the ABSENCE of chord notes, not off the
///         <c>highDensity</c> attribute.</b> Rocksmith2014.NET sets the
///         <c>ChordPanel</c> mask from <c>chord.HasChordNotes</c> alone
///         (<c>XmlToSngNote.fs</c>), and <c>InstrumentalArrangement</c>
///         <c>.FixHighDensity</c> says so outright: it exists "to use the absence
///         of chord notes to indicate repeated strums (matches official XML
///         files), instead of using the high density attribute (which is only
///         used in custom files)".
///     </para>
///     <para>
///         <b>And that translation only runs for XML older than version 8</b>
///         (<c>PackageBuilder.fs</c>: <c>if xml.Version &lt; 8uy then</c>
///         <c>xml.FixHighDensity()</c>). DotCharter writes version 8, so DLC
///         Builder skips it and whatever we emit is what ships. Writing
///         <c>highDensity="1"</c> while KEEPING the <c>&lt;chordNote&gt;</c>
///         children — what DotCharter did up to v0.48.0 — therefore rendered
///         every repeat as a full panel with its chord name in game, even though
///         the exported XML looked correct on its own terms. EOF has the same
///         escape hatch (<c>eof_rs2_export_version_8</c>, EOF_SRC/rs.c:2957): it
///         drops both the attribute and the subtags "to suit DLC Builder's
///         handling".
///     </para>
///     <para>
///         So a compact chord ships with NO chord notes and no <c>highDensity</c>
///         attribute, in the XML and in the SNG alike. Nothing is lost by
///         dropping the children: every technique that lives on a
///         <c>&lt;chordNote&gt;</c> already disqualifies the chord below.
///     </para>
///     <para>
///         The decision itself is EOF's <c>eof_note_has_high_chord_density</c>
///         (EOF_SRC/rs.c:4919), ported rule for rule.
///     </para>
/// </summary>
internal static class RsChordDensity
{
    /// <summary>
    /// EOF's <c>eof_chord_density_threshold</c> default (EOF_SRC/main.c:199,
    /// config.c:222): the maximum distance in milliseconds between the end of
    /// the previous sound and a repeated chord that still compacts. Checked at
    /// EOF_SRC/rs.c:4993.
    /// </summary>
    private const double ThresholdMs = 10_000.0;
    // ── The decision ───────────────────────────────────────────────
    //
    // Ported from EOF's eof_note_has_high_chord_density (EOF_SRC/rs.c:4919)
    // plus the chordId-change check its caller applies (rs.c:2929-2932).
    // A chord compacts only when it repeats the immediately preceding chord
    // closely enough; ANY doubt keeps the full panel, because that is the
    // conservative side of the one export decision the game renders
    // differently.
    //
    // Deliberately NOT ported: EOF's "return 2" special case (rs.c:4955-4958)
    // that force-adopts a fully string-muted, fingering-less chord into the
    // previous chord's handshape. It exists to work around EOF's own handshape
    // model and would compact exactly the charts we can least verify.

    /// <summary>
    /// Which chords in this level ship as a compact repeat. Reference
    /// identity is intentional — chords are mutable domain objects.
    /// </summary>
    public static HashSet<Chord> Compute(ChartLevel level, IList<BeatMarker> beats,
        Func<Chord, int> chordIdOf) =>
        Compute(level.Notes, level.Chords, level.HandShapes, beats, chordIdOf);

    /// <summary>
    /// The same decision over one level's three collections, for a caller that
    /// holds them without a <see cref="ChartLevel" /> — the editor's active level
    /// (<see cref="RepeatChords" />).
    /// </summary>
    public static HashSet<Chord> Compute(IList<GuitarNote> notes, IList<Chord> levelChords,
        IList<HandShape> handShapes, IList<BeatMarker> beats, Func<Chord, int> chordIdOf)
    {
        var compact = new HashSet<Chord>();
        if (levelChords.Count < 2) return compact;

        foreach (var (chord, verdict) in Explain(notes, levelChords, handShapes, beats, chordIdOf))
        {
            if (verdict.Reason == RepeatChordReason.Repeat)
                compact.Add(chord);
        }

        return compact;
    }

    /// <summary>
    /// The same decision with its REASON, for every chord of the level.
    ///     <c>Compute</c> is exactly the chords answered <see cref="RepeatChordReason.Repeat" />,
    ///     so the inspector's "why not" line and the export cannot disagree.
    /// </summary>
    public static Dictionary<Chord, RepeatChordVerdict> Explain(IList<GuitarNote> notes, IList<Chord> levelChords,
        IList<HandShape> handShapes, IList<BeatMarker> beats, Func<Chord, int> chordIdOf)
    {
        var verdicts = new Dictionary<Chord, RepeatChordVerdict>(ReferenceEqualityComparer.Instance);
        if (levelChords.Count == 0) return verdicts;

        // A "Split notes" chord ships as single notes, so it is judged — and
        // judges its neighbours — as the notes the game sees. EOF breaks
        // it up before this decision runs (rs.c:2082-2124 precede the chord
        // writer), so for the chord after it the previous sound is a NOTE and
        // the previous chord is the one before the split.
        var split = levelChords.Where(LinkLandingBreakup.IsSplit).ToList();
        foreach (var chord in split)
            verdicts[chord] = new RepeatChordVerdict(RepeatChordReason.SplitIntoNotes);

        var chords = levelChords.Where(c => !LinkLandingBreakup.IsSplit(c)).OrderBy(c => c.Position).ToList();
        if (chords.Count == 0) return verdicts;

        var sounds = notes
            .Select(n => new SoundRef(n.Position, null))
            .Concat(split.Select(c => new SoundRef(c.Position, null)))
            .Concat(chords.Select(c => new SoundRef(c.Position, c)))
            .OrderBy(s => s.Position)
            .ToList();

        verdicts[chords[0]] = new RepeatChordVerdict(RepeatChordReason.FirstChord);
        for (var i = 1; i < chords.Count; i++)
        {
            var chord = chords[i];
            // rs.c:2929-2932 — a chordId change forces the chord tag low
            // full panel regardless of everything below. EOF compares the id it is
            // about to WRITE, so this compares emitted indices too: two chords
            // whose unresolvable domain ids differ but whose shapes dedupe onto
            // one exported template are the same chord to the game.
            verdicts[chord] = chordIdOf(chord) != chordIdOf(chords[i - 1])
                ? new RepeatChordVerdict(RepeatChordReason.DifferentTemplate)
                : CompactRepeatVerdict(chord, sounds, handShapes, beats);
        }

        return verdicts;
    }

    /// <summary>
    /// One playable sound on the level's timeline. <c>AsChord</c> is null for a
    /// single note — the rules only ever need to know whether the
    /// neighbouring sound was a chord, and which one.
    /// </summary>
    private readonly record struct SoundRef(FractionalPosition Position, Chord? AsChord);

    private static RepeatChordVerdict CompactRepeatVerdict(Chord chord,
        IReadOnlyList<SoundRef> sounds, IEnumerable<HandShape> handShapes, IList<BeatMarker> beats)
    {
        // rs.c:4931 — fewer than two sounding strings is not a chord to RS.
        if (chord.ChordNotes.Count < 2) return new RepeatChordVerdict(RepeatChordReason.SingleString);
        // rs.c:4935-4936 — crazy status ("Pass other") forces the full chord box,
        // even for an exact repeat inside the same hand shape.
        if (chord.PassOtherNotes) return new RepeatChordVerdict(RepeatChordReason.PassOtherNotes);

        // rs.c:4944-4947 — any technique needing per-string <chordNote> data.
        if (ChordNoteDataReason(chord) is { } needsData) return needsData;

        // rs.c:4937-4939 — there must be an earlier sound in this difficulty,
        // and rs.c:4990-4991 compares against it, so a single note in between
        // breaks the repeat.
        //
        // `sounds` is sorted ascending by Position (Compute builds it with
        // OrderBy), so "the last sound strictly before this chord" is
        // LowerBound - 1. A backward LINEAR scan of the whole
        // list per chord would be O(chords x sounds), and the single dominant cost of
        // the entire SNG build (measured 159 ms of a 167 ms build on a
        // 1,071-chord arrangement).
        var earlierCount = LowerBound(sounds, chord.Position, 0, sounds.Count);
        if (earlierCount == 0) return new RepeatChordVerdict(RepeatChordReason.NoEarlierSound);
        var previousSound = sounds[earlierCount - 1];

        // rs.c:4949-4953 — a chord slide immediately before keeps the full panel.
        var previousSoundChord = previousSound.AsChord;
        if (previousSoundChord != null && previousSoundChord.ChordNotes.Any(cn => cn.SlideTo.HasValue))
            return new RepeatChordVerdict(RepeatChordReason.PreviousIsChordSlide);

        // rs.c:4961-4988 — handshape rules take precedence when the chord sits
        // inside one.
        var shape = FindHandShape(handShapes, chord.Position);
        if (shape != null)
        {
            // The sounds inside a hand shape are a CONTIGUOUS RANGE of the
            // sorted list ([shape.Position, shape.EndPosition) — end exclusive,
            // exactly what the old SoundsWithinHandShape filter selected), and
            // the ones before this chord are the prefix of that range. Two
            // binary searches replace two full-list scans per chord.
            var shapeLo = LowerBound(sounds, shape.Position, 0, sounds.Count);
            var shapeHi = LowerBound(sounds, shape.EndPosition, shapeLo, sounds.Count);
            var beforeChord = LowerBound(sounds, chord.Position, shapeLo, shapeHi);

            // rs.c:4964-4965 — the first sound in a handshape is always the
            // full chord box.
            if (beforeChord == shapeLo) return new RepeatChordVerdict(RepeatChordReason.FirstInHandShape);

            // rs.c:4967-4986 — otherwise the nearest EARLIER chord in the same
            // handshape decides, ignoring single notes in between. The old code
            // took LastOrDefault over the filtered range; scanning back from the
            // end of that range for the first chord is the same element.
            for (var i = beforeChord - 1; i >= shapeLo; i--)
            {
                if (sounds[i].AsChord != null)
                    return SameFretsVerdict(chord, sounds[i].AsChord!);
            }
        }

        // rs.c:4990-4991 — must match the previous sound exactly.
        if (previousSoundChord == null) return new RepeatChordVerdict(RepeatChordReason.PreviousSoundIsNote);
        if (!SameFrets(chord, previousSoundChord)) return new RepeatChordVerdict(RepeatChordReason.DifferentFrets);

        // rs.c:4993-4994 — and fall within the repeat threshold, measured from
        // the END of the previous sound.
        var chordMs = chord.Position.GetPositionMs(beats);
        var previousEndMs = previousSoundChord.EndPosition.GetPositionMs(beats);
        return chordMs <= previousEndMs + ThresholdMs
            ? new RepeatChordVerdict(RepeatChordReason.Repeat)
            : new RepeatChordVerdict(RepeatChordReason.TooFarFromPrevious);
    }

    private static RepeatChordVerdict SameFretsVerdict(Chord chord, Chord other) =>
        new(SameFrets(chord, other) ? RepeatChordReason.Repeat : RepeatChordReason.DifferentFrets);

    /// <summary>
    /// The techniques whose data lives on <c>&lt;chordNote&gt;</c> children, and
    /// which therefore keep a chord at its full panel (EOF_SRC/rs.c:5818-5820) —
    /// this set is also why dropping the children of a COMPACT chord loses
    /// nothing. Chord-level attributes (palm mute, fret-hand mute, accent) are
    /// NOT in it: they ride the <c>&lt;chord&gt;</c> tag itself and survive.
    /// </summary>
    /// <returns>Null when nothing needs chord-note data, else the first reason found.</returns>
    private static RepeatChordVerdict? ChordNoteDataReason(Chord chord)
    {
        if (chord.HasLinkNext()) return new RepeatChordVerdict(RepeatChordReason.LinkNext);

        foreach (var cn in chord.ChordNotes)
        {
            if (ChordNoteTechnique(cn) is { } technique) return new RepeatChordVerdict(technique, cn.StringIndex);
        }

        // ── Beyond EOF: per-string state EOF cannot even represent ──────
        //
        // Dropping the <chordNote> children also drops every per-string flag
        // they carried. EOF's mask does not guard accent / ignore / mute because
        // EOF has no per-STRING form of them — they are whole-note flags there,
        // and the <chord> tag carries them losslessly. DotCharter's ChordNote
        // does have per-string forms (the inspector's per-string chips write
        // them), and the compact chord tag has nowhere to put one that disagrees
        // with the strum. So anything the chord tag will not say for itself
        // keeps the full panel — the same "ANY doubt" side the rules above take.
        foreach (var cn in chord.ChordNotes)
        {
            if (cn.Accent != chord.Accent)
                return new RepeatChordVerdict(RepeatChordReason.PerStringAccent, cn.StringIndex);
            if (cn.Ignore != chord.Ignore)
                return new RepeatChordVerdict(RepeatChordReason.PerStringIgnore, cn.StringIndex);
            // A UNIFORM mute is expressible: all-Full becomes fretHandMute="1"
            // and all-Palm becomes palmMute="1" on the chord (Chord.IsFullyMuted
            // / .IsPalmMuted are all-or-nothing for exactly this reason). A
            // strum muted on some strings and not others is not.
            if (cn.Mute != chord.ChordNotes[0].Mute)
                return new RepeatChordVerdict(RepeatChordReason.MixedMute, cn.StringIndex);
        }

        return null;
    }

    /// <summary>The first technique on one string that lives on a <c>&lt;chordNote&gt;</c>, or null.</summary>
    private static RepeatChordReason? ChordNoteTechnique(ChordNote cn)
    {
        if (cn.SlideTo.HasValue) return RepeatChordReason.Slide;
        if (cn.Vibrato) return RepeatChordReason.Vibrato;
        if (cn.Tremolo) return RepeatChordReason.Tremolo;
        if (cn.Hopo != HopoType.None) return RepeatChordReason.Hopo;
        if (cn.Harmonic != HarmonicType.None) return RepeatChordReason.Harmonic;
        if (cn.BassPicking != BassPickingType.None) return RepeatChordReason.BassPicking;
        if (cn.BendValues.Count > 0) return RepeatChordReason.Bend;
        // EOF_SRC/rs.c:5826-5828 — the EXPLICIT sustain status, not the
        // presence of a length. See ChordNote.ExplicitSustain: reading this
        // off `cn.Sustain > Zero` is what made highDensity unreachable, since
        // essentially every charted chord has some sustain.
        return cn.ExplicitSustain ? RepeatChordReason.ExplicitSustain : null;
    }

    /// <summary>
    ///     First index in <c>[lo, hi)</c> whose sound is NOT before
    ///     <paramref name="target" /> — equivalently, the COUNT of sounds in that
    ///     window that are strictly before it. <c>sounds</c> is ordered by the
    ///     same <see cref="FractionalPosition.CompareTo" /> that
    ///     <c>Compute</c>'s <c>OrderBy</c> used, and <c>FractionalPosition</c>'s
    ///     relational operators all delegate to it, so this answers exactly what
    ///     the linear scans it replaced answered.
    /// </summary>
    private static int LowerBound(
        IReadOnlyList<SoundRef> sounds, FractionalPosition target, int lo, int hi)
    {
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (sounds[mid].Position < target) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static HandShape? FindHandShape(IEnumerable<HandShape> handShapes, FractionalPosition position)
    {
        // End is EXCLUSIVE, matching the rest of DotCharter's handshape maths.
        foreach (var shape in handShapes)
        {
            if (position >= shape.Position && position < shape.EndPosition)
                return shape;
        }

        return null;
    }

    /// <summary>
    /// EOF's <c>eof_same_excluding_ghosts</c> (EOF_SRC/rs.c:4890): the two
    /// chords must fret every string identically.
    /// </summary>
    private static bool SameFrets(Chord left, Chord right)
    {
        if (left.ChordNotes.Count != right.ChordNotes.Count) return false;

        foreach (var cn in left.ChordNotes)
        {
            var other = right.ChordNotes.FirstOrDefault(n => n.StringIndex == cn.StringIndex);
            if (other == null || other.Fret != cn.Fret) return false;
        }

        return true;
    }
}
