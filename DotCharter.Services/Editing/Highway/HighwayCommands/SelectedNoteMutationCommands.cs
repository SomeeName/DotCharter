using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     The keyboard mutation vocabulary, applied to the selection.
///     <para>
///         <b>What a verb means on a CHORD (v0.35 — decided deliberately, see
///         <c>docs/agents/ui/editor-input.md</c>):</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Per-string, honouring the <see cref="StringEditMask" /></b> —
///             fret ±1 / ±12 / set, mute, HOPO, harmonic, bass picking, vibrato,
///             tremolo, link-next. These are all <see cref="IGuitarSound" />
///             members that <c>ChordNote</c> already carries per string, and RS
///             stores them per string. An EMPTY mask means every string, so the
///             whole shape moves / toggles — EOF's <c>CTRL+±</c> over an all-on
///             <c>eof_pro_guitar_fret_bitmask</c>.
///         </item>
///         <item>
///             <b>Whole chord</b> — accent, ignore, pass-other-notes. Each has a
///             <c>Chord</c>-level field that is the one the exporters read
///             (<c>Chord.Accent</c>, <c>Chord.Ignore</c>,
///             <c>Chord.PassOtherNotes</c>), each is what the inspector's "Whole
///             chord" row already writes, and <c>Chord</c> has no per-string
///             <c>PassOtherNotes</c> at all. Writing the per-string
///             <c>ChordNote.Accent</c>/<c>Ignore</c> instead would create state
///             the .eof exporter rolls up, GP drops, and no visible control can
///             show.
///         </item>
///         <item>
///             <b>Whole shape</b> — string up / down. Every chord note moves one
///             lane together, or none does: shifting one string of a strum onto
///             another string collides two notes on one lane, and a chord whose
///             lowest/highest note is already at the neck edge simply does not
///             move (the note path clamps instead, which for a chord would merge
///             two notes). The mask still decides WHICH chords are targeted, not
///             which of their strings move.
///         </item>
///     </list>
///     <para>
///         Fret and string verbs are shape changes, so they repoint
///         <c>TemplateId</c> through <c>ChordTemplateResolver</c> — see
///         <see cref="SelectedNoteMutationCommand" />. Frets stay ABSOLUTE.
///     </para>
///     <para>
///         <b>…and they are NOT capo-agnostic, which this summary claimed until
///         0.50.1.</b> Fret 0 is the open (capo'd) string, but a NON-ZERO fret
///         below the capo is malformed — inspector rule <c>L01</c>
///         (<c>NoteFretBelowCapo</c>), which <c>FixCapoFrets</c> silently raises
///         on the next auto-fix and <c>SoundingPitch.MidiFor</c> refuses to price
///         at all. A plain <c>Math.Clamp(fret, 0, 24)</c> therefore manufactured
///         that finding on every capo'd chart: on a capo-5 arrangement a typed
///         <c>3</c>, a <c>FretDown</c> off the capo and an octave step all wrote
///         a fret the neck does not have. Every fret write now lands through
///         <see cref="SoundingPitch.SnapToNeck" />, whose legal set is
///         <c>{0} ∪ [capo, 24]</c> and which is a strict no-op at capo 0.
///     </para>
/// </summary>
internal static class SelectedNoteMutationCommands
{
    /// <summary>Model ceiling — <c>ChartExportValidator.KeyFretOutOfRange</c>
    /// and the inspector's numeric editors use the same 0..24 range.</summary>
    private const int MaxFret = 24;

    public static IHighwayEditCommand MoveStringUp(SelectedSoundMutationTargets t, int maxStrings)
    {
        return new SelectedNoteMutationCommand(t,
            n => n.StringIndex = Math.Min(maxStrings - 1, n.StringIndex + 1),
            c => ShiftChordStrings(c, 1, maxStrings),
            MergeVerbs.String);
    }

    /// <summary>
    ///     Moves the selection one lane while keeping the PITCH — fret 5 on the
    ///     low E becomes fret 0 on the A, because both sound A2.
    ///     <para>
    ///         Guitar Pro's <c>Alt+Up</c>/<c>Alt+Down</c>. The plain string verbs
    ///         carry the fret across unchanged, which re-pitches the note; this
    ///         one re-frets it so the music does not change, which is what a
    ///         charter wants when they are moving a line onto a more playable
    ///         string.
    ///     </para>
    ///     <para>
    ///         A sound that CANNOT keep its pitch on the new string — the string
    ///         does not go that low, or the fret would pass the 24th — is left
    ///         exactly where it is rather than re-pitched. A chord moves whole or
    ///         not at all, the same rule the plain string verbs follow: shifting
    ///         part of a strum collides two notes on one lane.
    ///     </para>
    /// </summary>
    public static IHighwayEditCommand MoveStringPreservingPitch(
        SelectedSoundMutationTargets t, int delta, int maxStrings)
    {
        var arrangement = t.Arrangement;
        return new SelectedNoteMutationCommand(t,
            n =>
            {
                if (TryRefret(arrangement, n.StringIndex, n.Fret, delta, maxStrings, out var to, out var fret))
                {
                    n.StringIndex = to;
                    n.Fret = fret;
                }
            },
            c => ShiftChordStringsPreservingPitch(arrangement, c, delta, maxStrings),
            MergeVerbs.String);
    }

    /// <summary>
    ///     Where <paramref name="fret" /> on <paramref name="from" /> lands
    ///     <paramref name="delta" /> lanes away at the same pitch, or false when
    ///     that string cannot sound it.
    /// </summary>
    private static bool TryRefret(
        Arrangement? arrangement, int from, int fret, int delta, int maxStrings, out int to, out int newFret)
    {
        to = from + delta;
        newFret = fret;
        if (to < 0 || to > maxStrings - 1) return false;
        if (SoundingPitch.MidiFor(arrangement, from, fret) is not { } midi) return false;
        if (SoundingPitch.FretForPitch(arrangement, to, midi, MaxFret) is not { } resolved) return false;

        newFret = resolved;
        return true;
    }

    private static void ShiftChordStringsPreservingPitch(
        Arrangement? arrangement, Chord chord, int delta, int maxStrings)
    {
        if (chord.ChordNotes.Count == 0) return;

        // All or nothing: resolve every string first, then commit.
        var moved = new List<(ChordNote Note, int String, int Fret)>(chord.ChordNotes.Count);
        foreach (var chordNote in chord.ChordNotes)
        {
            if (!TryRefret(arrangement, chordNote.StringIndex, chordNote.Fret, delta, maxStrings,
                    out var to, out var fret))
            {
                return;
            }

            moved.Add((chordNote, to, fret));
        }

        foreach (var (chordNote, stringIndex, fret) in moved)
        {
            chordNote.StringIndex = stringIndex;
            chordNote.Fret = fret;
        }
    }

    public static IHighwayEditCommand MoveStringDown(SelectedSoundMutationTargets t)
    {
        // No ceiling needed going down — only the floor can be hit.
        return new SelectedNoteMutationCommand(t,
            n => n.StringIndex = Math.Max(0, n.StringIndex - 1),
            c => ShiftChordStrings(c, -1, int.MaxValue),
            MergeVerbs.String);
    }

    /// <summary>
    ///     Moves every targeted string's fret by <paramref name="delta" />,
    ///     clamped to the neck. The ±1 keys and the ±12 OCTAVE keys are both
    ///     this — the octave is not twelve separate
    ///     <c>MoveFretUp</c> calls, which would cost twelve of
    ///     the 500 history nodes, twelve spacing-enforcer passes and up to eleven
    ///     intermediate chord templates that nothing collects, for ONE keystroke.
    ///     <para>
    ///         Doing it in one pass is behaviour-identical because the clamp is
    ///         idempotent per string: twelve applications of
    ///         <c>min(24, f + 1)</c> and one of <c>clamp(f + 12, 0, 24)</c> agree
    ///         for every <c>f</c>, and likewise downward. (A shape that runs into
    ///         the ceiling still squashes — that is the pre-existing note-path
    ///         semantics for the fret keys, unchanged here.)
    ///     </para>
    /// </summary>
    public static IHighwayEditCommand MoveFretBy(SelectedSoundMutationTargets t, int delta)
    {
        var capo = CapoOf(t);
        var direction = Math.Sign(delta);
        return PerString(t, s => s.Fret = SoundingPitch.SnapToNeck(s.Fret + delta, capo, direction, MaxFret),
            MergeVerbs.Fret);
    }

    public static IHighwayEditCommand MoveFretUp(SelectedSoundMutationTargets t) => MoveFretBy(t, 1);

    public static IHighwayEditCommand MoveFretDown(SelectedSoundMutationTargets t) => MoveFretBy(t, -1);

    /// <summary>
    ///     Writes an ABSOLUTE fret. Carries no merge verb on purpose: its call
    ///     sites are the keyboard pen's typed digits and the sheet pane, and the
    ///     pen already owns its own run granularity (a continuation digit undoes
    ///     the run's own node before committing the new value). A second
    ///     coalescing mechanism layered on that is the exact "two verbs over one
    ///     concept" confusion <see cref="MergeVerbs" /> exists to prevent.
    /// </summary>
    public static IHighwayEditCommand SetFret(SelectedSoundMutationTargets t, int fret)
    {
        var clamped = SoundingPitch.SnapToNeck(fret, CapoOf(t), 0, MaxFret);
        return PerString(t, s => s.Fret = clamped);
    }

    /// <summary>The arrangement's capo, or 0 when there is none (or no
    /// arrangement — the headless/paste harnesses). Read HERE rather than
    /// captured by a caller, so a verb cannot clamp against the capo of the
    /// arrangement the charter has just navigated away from — the same rule
    /// <c>AppKeybindRegistrar.NudgeAnchor</c> already follows for the FHP
    /// floor.</summary>
    private static int CapoOf(SelectedSoundMutationTargets t) => Math.Max(0, t.Arrangement?.Capo ?? 0);

    /// <summary>
    ///     The NO-MASK form of typing a fret: the typed number becomes the
    ///     shape's LOWEST fret and the whole chord moves with it — rscharter's
    ///     <c>GuitarSoundsHandler:379-400</c> over <c>moveTemplateFrets:213-229</c>,
    ///     the idiom that replaces most <c>FretUp</c>×n sequences on a barre
    ///     chord. A standalone NOTE is its own lowest fret, so it simply takes
    ///     the value (which is what rscharter's <c>isNote()</c> branch does too).
    ///     <para>
    ///         A move that would push the shape's HIGHEST fret off the neck is
    ///         REFUSED outright rather than clamped: clamping would silently
    ///         squash the shape into a different chord. Same for a move of zero.
    ///         Both leave the chord untouched, so no history node is recorded.
    ///     </para>
    ///     <para>
    ///         Only sensible while the mask is empty — with a mask the caller
    ///         wants <see cref="SetFret" />, which writes the masked strings.
    ///     </para>
    /// </summary>
    public static IHighwayEditCommand TransposeShapeTo(SelectedSoundMutationTargets t, int fret)
    {
        var capo = CapoOf(t);
        var target = SoundingPitch.SnapToNeck(fret, capo, 0, MaxFret);
        return new SelectedNoteMutationCommand(t,
            n => n.Fret = target,
            c => TransposeChordTo(c, target, capo));
    }

    private static void TransposeChordTo(Chord chord, int fret, int capo)
    {
        if (chord.ChordNotes.Count == 0) return;

        var lowest = int.MaxValue;
        var highest = int.MinValue;
        foreach (var chordNote in chord.ChordNotes)
        {
            if (chordNote.Fret < lowest) lowest = chordNote.Fret;
            if (chordNote.Fret > highest) highest = chordNote.Fret;
        }

        var change = fret - lowest;
        if (change == 0 || highest + change > MaxFret) return;

        // Behind the capo is the same refusal as past the 24th, for the same
        // reason: a note that has to be snapped forward onto the capo (or back
        // onto the open string) to become legal has been squashed, and a squashed
        // grip is a DIFFERENT chord. A transpose either moves the whole shape or
        // it does not happen.
        foreach (var chordNote in chord.ChordNotes)
        {
            var moved = chordNote.Fret + change;
            if (moved > 0 && moved < capo) return;
        }

        foreach (var chordNote in chord.ChordNotes)
            chordNote.Fret = Math.Clamp(chordNote.Fret + change, 0, MaxFret);
    }

    public static IHighwayEditCommand ToggleHarmonic(SelectedSoundMutationTargets t)
    {
        return PerString(t, s => s.Harmonic = s.Harmonic == HarmonicType.None
            ? HarmonicType.Normal
            : HarmonicType.None);
    }

    public static IHighwayEditCommand ToggleHopo(SelectedSoundMutationTargets t) => PerString(t, s => s.Hopo = NextHopo(s.Hopo));

    public static IHighwayEditCommand ToggleMute(SelectedSoundMutationTargets t) => PerString(t, s => s.Mute = NextMute(s.Mute));

    public static IHighwayEditCommand ToggleLinkNext(SelectedSoundMutationTargets t) => PerString(t, s => s.LinkNext = !s.LinkNext);

    public static IHighwayEditCommand ToggleVibrato(SelectedSoundMutationTargets t)
    {
        return PerString(t, s =>
        {
            s.Vibrato = !s.Vibrato;
            if (!s.Vibrato) s.VibratoStrength = 0;
        });
    }

    public static IHighwayEditCommand ToggleTremolo(SelectedSoundMutationTargets t) => PerString(t, s => s.Tremolo = !s.Tremolo);

    public static IHighwayEditCommand TogglePicking(SelectedSoundMutationTargets t) => PerString(t, s => s.BassPicking = NextBassPicking(s.BassPicking));

    public static IHighwayEditCommand ToggleAccent(SelectedSoundMutationTargets t) => WholeChord(t, n => n.Accent = !n.Accent, c => c.Accent = !c.Accent);

    /// <summary>
    ///     AUTHORED WINS (§6.5): toggling <c>Ignore</c> from the highway clears
    ///     <c>IgnoreDerived</c> in the SAME mutation, exactly as the inspector
    ///     chip does (<c>InspectorViewModel.OnNoteIgnoreChanged</c>).
    ///     <para>
    ///         What it costs, precisely. <c>ChartSpacingEnforcer.ApplyDerivedIgnore</c>
    ///         runs on every save and tells an authored flag from its own by
    ///         <c>IgnoreDerived</c>: while the structural reason holds (fret 23+,
    ///         or a sustained 7th-fret harmonic) it re-applies <c>Ignore</c>
    ///         regardless — that part is intended — and when the reason GOES it
    ///         retracts, but "only what this pass is responsible for".
    ///     </para>
    ///     <para>
    ///         So the loss lands later, which is why nobody caught it: a note the
    ///         enforcer had flagged carries <c>IgnoreDerived</c>; the charter
    ///         authors <c>Ignore</c> on it from the highway; the flag stays marked
    ///         as OURS; and the moment the structural reason disappears — the note
    ///         is dragged back into range — the enforcer reads its own mark and
    ///         retracts a flag the charter set. The keypress and the loss are
    ///         separated by an unrelated edit. The enforcer's own doc already
    ///         names "the highway toggle" as a user-authoring path; this makes
    ///         that true.
    ///     </para>
    /// </summary>
    public static IHighwayEditCommand ToggleIgnore(SelectedSoundMutationTargets t)
    {
        return WholeChord(t,
            n => { n.Ignore = !n.Ignore; n.IgnoreDerived = false; },
            c => { c.Ignore = !c.Ignore; c.IgnoreDerived = false; });
    }

    public static IHighwayEditCommand TogglePassOtherNotes(SelectedSoundMutationTargets t)
    {
        return WholeChord(t, n => n.PassOtherNotes = !n.PassOtherNotes,
            c => c.PassOtherNotes = !c.PassOtherNotes);
    }

    // === Group variants ==========================================================
    // The "group" form mirrors RsCharter's `cyclicalToggleNotes`: it computes the
    // next value from the FIRST selected sound and applies that same value to
    // every selected sound. The non-group forms above mirror RsCharter's
    // `independentCyclicalToggleNotes` which advances each sound from its own
    // current value. Group is useful for "make all of these have HOPO" in one
    // press; independent is useful for cycling distinct values quickly.
    // "First" is the first MASKED selected note, else the first masked ChordNote
    // of the first targeted chord (SelectedSoundMutationTargets.FirstMaskedSound).

    public static IHighwayEditCommand ToggleHarmonicGroup(SelectedSoundMutationTargets t)
    {
        return PerStringGroup(t,
            first => first.Harmonic == HarmonicType.None ? HarmonicType.Normal : HarmonicType.None,
            (s, v) => s.Harmonic = v);
    }

    public static IHighwayEditCommand ToggleHopoGroup(SelectedSoundMutationTargets t) => PerStringGroup(t, first => NextHopo(first.Hopo), (s, v) => s.Hopo = v);

    public static IHighwayEditCommand ToggleMuteGroup(SelectedSoundMutationTargets t) => PerStringGroup(t, first => NextMute(first.Mute), (s, v) => s.Mute = v);

    public static IHighwayEditCommand ToggleLinkNextGroup(SelectedSoundMutationTargets t) => PerStringGroup(t, first => !first.LinkNext, (s, v) => s.LinkNext = v);

    public static IHighwayEditCommand ToggleVibratoGroup(SelectedSoundMutationTargets t)
    {
        return PerStringGroup(t, first => !first.Vibrato, (s, v) =>
        {
            s.Vibrato = v;
            if (!v) s.VibratoStrength = 0;
        });
    }

    public static IHighwayEditCommand ToggleTremoloGroup(SelectedSoundMutationTargets t) => PerStringGroup(t, first => !first.Tremolo, (s, v) => s.Tremolo = v);

    public static IHighwayEditCommand TogglePickingGroup(SelectedSoundMutationTargets t) => PerStringGroup(t, first => NextBassPicking(first.BassPicking), (s, v) => s.BassPicking = v);

    public static IHighwayEditCommand ToggleAccentGroup(SelectedSoundMutationTargets t)
    {
        return WholeChordGroup(t, n => n.Accent, c => c.Accent,
            (n, v) => n.Accent = v, (c, v) => c.Accent = v);
    }

    /// <summary>The group form authors just as hard as the single one — see
    /// <see cref="ToggleIgnore" /> for why the derived flag must go with it.</summary>
    public static IHighwayEditCommand ToggleIgnoreGroup(SelectedSoundMutationTargets t)
    {
        return WholeChordGroup(t, n => n.Ignore, c => c.Ignore,
            (n, v) => { n.Ignore = v; n.IgnoreDerived = false; },
            (c, v) => { c.Ignore = v; c.IgnoreDerived = false; });
    }

    public static IHighwayEditCommand TogglePassOtherNotesGroup(SelectedSoundMutationTargets t)
    {
        return WholeChordGroup(t, n => n.PassOtherNotes, c => c.PassOtherNotes,
            (n, v) => n.PassOtherNotes = v, (c, v) => c.PassOtherNotes = v);
    }

    /// <summary>A verb that writes <see cref="IGuitarSound" /> state: straight
    /// onto a standalone note, and onto every MASKED <c>ChordNote</c> of a
    /// chord.</summary>
    private static IHighwayEditCommand PerString(SelectedSoundMutationTargets t, Action<IGuitarSound> mutate,
        string mergeVerb = "")
    {
        var mask = t.Mask;
        return new SelectedNoteMutationCommand(t,
            n => mutate(n),
            c =>
            {
                foreach (var chordNote in c.ChordNotes)
                {
                    if (mask.Includes(chordNote.StringIndex))
                        mutate(chordNote);
                }
            },
            mergeVerb);
    }

    private static IHighwayEditCommand PerStringGroup<T>(
        SelectedSoundMutationTargets t,
        Func<IGuitarSound, T> nextFromFirst,
        Action<IGuitarSound, T> apply)
    {
        var first = t.FirstMaskedSound();
        if (first == null) return new SelectedNoteMutationCommand(t, null, null);

        var target = nextFromFirst(first);
        return PerString(t, s => apply(s, target));
    }

    /// <summary>A verb whose chord form is a CHORD-level flag, not a per-string
    /// one. The mask still gates which chords are targeted.</summary>
    private static IHighwayEditCommand WholeChord(
        SelectedSoundMutationTargets t,
        Action<GuitarNote> mutateNote,
        Action<Chord> mutateChord) => new SelectedNoteMutationCommand(t, mutateNote, mutateChord);

    private static IHighwayEditCommand WholeChordGroup(
        SelectedSoundMutationTargets t,
        Func<GuitarNote, bool> readNote,
        Func<Chord, bool> readChord,
        Action<GuitarNote, bool> writeNote,
        Action<Chord, bool> writeChord)
    {
        var current = FirstWholeChordFlag(t, readNote, readChord);
        if (current == null) return new SelectedNoteMutationCommand(t, null, null);

        var target = !current.Value;
        return new SelectedNoteMutationCommand(t, n => writeNote(n, target), c => writeChord(c, target));
    }

    /// <summary>The value a whole-chord group verb inverts: the first masked
    /// note's flag, else the first targeted chord's. Same "notes first" order as
    /// <c>SelectedSoundMutationTargets.FirstMaskedSound</c>.</summary>
    private static bool? FirstWholeChordFlag(
        SelectedSoundMutationTargets t,
        Func<GuitarNote, bool> readNote,
        Func<Chord, bool> readChord)
    {
        foreach (var note in t.SelectedNotes)
        {
            if (t.Mask.Includes(note.StringIndex))
                return readNote(note);
        }

        var chords = t.MaskedChords();
        return chords.Count > 0 ? readChord(chords[0]) : null;
    }

    /// <summary>
    ///     Moves EVERY note of the strum one lane, or none of them. A partial
    ///     move would stack two chord notes on one string, which is not a state
    ///     the model, the renderers or the exporters can express (the SNG
    ///     per-string arrays are indexed BY string).
    /// </summary>
    private static void ShiftChordStrings(Chord chord, int delta, int maxStrings)
    {
        if (chord.ChordNotes.Count == 0) return;

        var lowest = int.MaxValue;
        var highest = int.MinValue;
        foreach (var chordNote in chord.ChordNotes)
        {
            if (chordNote.StringIndex < lowest) lowest = chordNote.StringIndex;
            if (chordNote.StringIndex > highest) highest = chordNote.StringIndex;
        }

        if (lowest + delta < 0 || highest + delta > maxStrings - 1) return;
        foreach (var chordNote in chord.ChordNotes) chordNote.StringIndex += delta;
    }

    private static HopoType NextHopo(HopoType value)
    {
        return value switch
        {
            HopoType.None => HopoType.HammerOn,
            HopoType.HammerOn => HopoType.PullOff,
            HopoType.PullOff => HopoType.Tap,
            _ => HopoType.None
        };
    }

    private static MuteType NextMute(MuteType value)
    {
        return value switch
        {
            MuteType.None => MuteType.Full,
            MuteType.Full => MuteType.Palm,
            _ => MuteType.None
        };
    }

    private static BassPickingType NextBassPicking(BassPickingType value)
    {
        return value switch
        {
            BassPickingType.None => BassPickingType.Pop,
            BassPickingType.Pop => BassPickingType.Slap,
            _ => BassPickingType.None
        };
    }
}
