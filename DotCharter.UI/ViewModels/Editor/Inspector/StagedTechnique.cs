namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>A technique field the Technique page stages. Bools are 0/1, enums their value, a slide its end fret.</summary>
public enum TechniqueField
{
    Accent,
    Vibrato,
    LinkNext,
    PassOtherNotes,
    Ignore,
    Tremolo,
    UnpitchedSlide,
    SlideTo,
    Mute,
    Hopo,
    Harmonic,
    BassPicking,
    SplitIntoNotes,
    ForceNoNotes,
    Strum,

    /// <summary>A note's fretting finger (-1 unset, 0 thumb, 1-4); a chord's fingers are the Shape page's.</summary>
    Finger,

    /// <summary>A note's pick direction; a chord's is its <see cref="Strum" />.</summary>
    PickDirection,

    /// <summary>The vibrato's depth, written only where the vibrato is on ("Copy to strings" carries it).</summary>
    VibratoStrength
}

/// <summary>One staged field: on the sound itself (<paramref name="StringIndex" /> null: a note, or a chord's
/// whole-chord flags) or on one string of a chord.</summary>
public readonly record struct StagedKey(TechniqueField Field, int? StringIndex = null);

/// <summary>
///     The Technique page's staged edit (design D's <c>TechniquePlan</c> on real domain types), pure:
///     <list type="bullet">
///         <item>A per-string value lands on each target's string of the same index; a target that does not
///         play that string is passed by for it and named (<see cref="StagedSkipReason.StringSilent" />).</item>
///         <item>A slide is an INTERVAL: each target string slides as far as the source string does, the way
///         the Technique page's "Copy to strings" copies it. Where that would end off the neck the target keeps its
///         slides (all of them: a chord's strings slide together, <see cref="ChordSlideScope" />) and the
///         review names it rather than refusing the whole edit (decision 2).</item>
///         <item>Reset clears every technique on each target before the staged values are written, except a
///         slide the review says the target keeps (its staged slide would leave the neck there).</item>
///     </list>
/// </summary>
internal static class StagedTechnique
{
    private static readonly HashSet<TechniqueField> ChordLevel =
    [
        TechniqueField.Accent, TechniqueField.PassOtherNotes, TechniqueField.Ignore, TechniqueField.SplitIntoNotes,
        TechniqueField.ForceNoNotes, TechniqueField.Strum
    ];

    private static readonly HashSet<TechniqueField> ChordOnly =
        [TechniqueField.SplitIntoNotes, TechniqueField.ForceNoNotes, TechniqueField.Strum];

    private static readonly HashSet<TechniqueField> NoteOnly = [TechniqueField.Finger, TechniqueField.PickDirection];

    /// <summary>True when <paramref name="key" /> is a field a note (or a chord, <paramref name="chord" />) carries.</summary>
    internal static bool Accepts(StagedKey key, bool chord)
    {
        if (key.StringIndex is not null)
            return chord && !NoteOnly.Contains(key.Field) && (!ChordLevel.Contains(key.Field) || key.Field == TechniqueField.Ignore);
        return chord ? ChordLevel.Contains(key.Field) : !ChordOnly.Contains(key.Field);
    }

    /// <summary>What <paramref name="sound" /> holds for <paramref name="key" /> now; null when it has no such string.</summary>
    internal static int? Read(ITimedElement sound, StagedKey key, out bool present)
    {
        present = true;
        switch (sound)
        {
            case GuitarNote note when key.StringIndex is null:
                return ReadString(note, key.Field) ?? (key.Field == TechniqueField.PassOtherNotes ? Bit(note.PassOtherNotes) : null);
            case Chord chord when key.StringIndex is null:
                return ReadChord(chord, key.Field);
            case Chord chord when chord.ChordNotes.FirstOrDefault(n => n.StringIndex == key.StringIndex) is { } strand:
                return ReadString(strand, key.Field) ?? (key.Field == TechniqueField.Ignore ? Bit(strand.Ignore) : null);
            default:
                present = false;
                return null;
        }
    }

    /// <summary>Which targets the draft writes, and what it passes by.</summary>
    internal static StagedEditPlan Plan(ITimedElement source, IReadOnlyDictionary<StagedKey, int?> draft, bool reset,
        IReadOnlyList<ITimedElement> targets, int capo)
    {
        if (draft.Count == 0 && !reset) return StagedEditPlan.Empty;
        var writes = new List<ITimedElement>();
        var skips = new List<StagedSkip>();
        foreach (var target in targets)
        {
            var lands = Landing(source, draft, reset, target, capo, skips);
            if (reset || lands.Count > 0) writes.Add(target);
            else if (!skips.Any(s => ReferenceEquals(s.Target, target))) skips.Add(new StagedSkip(target, StagedSkipReason.NothingLands));
        }

        return StagedEditPlan.From(writes, skips, []);
    }

    /// <summary>The values that land on <paramref name="target" />, slides already moved to its frets. Where the
    /// slide stays and <paramref name="reset" /> clears first, the target's own slides land again.</summary>
    internal static IReadOnlyList<KeyValuePair<StagedKey, int?>> Landing(ITimedElement source,
        IReadOnlyDictionary<StagedKey, int?> draft, bool reset, ITimedElement target, int capo, List<StagedSkip>? skips = null)
    {
        var chord = target is Chord;
        var lands = new List<KeyValuePair<StagedKey, int?>>();
        var slides = new List<KeyValuePair<StagedKey, int?>>();
        var offBoard = (int?)null;
        foreach (var (key, value) in draft)
        {
            if (!Accepts(key, chord)) continue;
            Read(target, key, out var present);
            if (!present)
            {
                if (key.StringIndex is { } silent && skips?.Any(s => ReferenceEquals(s.Target, target) && s.StringIndex == silent) == false)
                    skips.Add(new StagedSkip(target, StagedSkipReason.StringSilent, silent));
                continue;
            }

            if (key.Field != TechniqueField.SlideTo)
            {
                lands.Add(new(key, value));
                continue;
            }

            var end = SlideAt(source, target, key, value);
            if (end is { } fret && !OnNeck(fret, capo)) offBoard ??= key.StringIndex ?? -1;
            slides.Add(new(key, end));
        }

        if (offBoard is not { } where)
        {
            lands.AddRange(slides);
            return lands;
        }

        skips?.Add(new StagedSkip(target, StagedSkipReason.SlideOffNeck, where < 0 ? null : where));
        if (reset) lands.AddRange(OwnSlides(target));
        return lands;
    }

    /// <summary>The slides <paramref name="target" /> holds now, as values that write them back.</summary>
    private static IEnumerable<KeyValuePair<StagedKey, int?>> OwnSlides(ITimedElement target) => target switch
    {
        GuitarNote note => [new(new StagedKey(TechniqueField.SlideTo), note.SlideTo)],
        Chord chord => chord.ChordNotes.Select(n => new KeyValuePair<StagedKey, int?>(new StagedKey(TechniqueField.SlideTo, n.StringIndex), n.SlideTo)),
        _ => []
    };

    /// <summary>What a staged write does to one note: reset first when asked, then each landing value.</summary>
    internal static Action<GuitarNote> NoteWrite(IReadOnlyList<KeyValuePair<StagedKey, int?>> lands, bool reset) => note =>
    {
        if (reset) ResetNote(note);
        foreach (var (key, value) in DepthLast(lands))
        {
            if (key.Field == TechniqueField.PassOtherNotes) note.PassOtherNotes = value == 1;
            else WriteString(note, key.Field, value);
        }
    };

    /// <summary>What a staged write does to one chord: reset first when asked, then each landing value.</summary>
    internal static Action<Chord> ChordWrite(IReadOnlyList<KeyValuePair<StagedKey, int?>> lands, bool reset) => chord =>
    {
        if (reset) ResetChord(chord);
        foreach (var (key, value) in DepthLast(lands))
        {
            if (key.StringIndex is null)
            {
                WriteChord(chord, key.Field, value);
                continue;
            }

            var strand = chord.ChordNotes.First(n => n.StringIndex == key.StringIndex);
            if (key.Field == TechniqueField.Ignore) strand.Ignore = value == 1;
            else WriteString(strand, key.Field, value);
        }
    };

    /// <summary>The vibrato depth lands after the vibrato itself: it is written only on a string whose vibrato is on.</summary>
    private static IEnumerable<KeyValuePair<StagedKey, int?>> DepthLast(IReadOnlyList<KeyValuePair<StagedKey, int?>> lands) =>
        lands.OrderBy(l => l.Key.Field == TechniqueField.VibratoStrength);

    /// <summary>Where a staged slide ends on <paramref name="target" />: the source's interval from the target's
    /// own fret; null clears it. A slide staged on an open source string is the typed fret itself.</summary>
    private static int? SlideAt(ITimedElement source, ITimedElement target, StagedKey key, int? value)
    {
        if (value is not { } end || ReferenceEquals(source, target)) return value;
        var from = FretOf(source, key.StringIndex);
        var to = FretOf(target, key.StringIndex);
        return from is not { } sourceFret || to is not { } targetFret || sourceFret == 0 ? end : targetFret + end - sourceFret;
    }

    private static int? FretOf(ITimedElement sound, int? stringIndex) => sound switch
    {
        GuitarNote note => note.Fret,
        Chord chord => chord.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex)?.Fret,
        _ => null
    };

    internal static bool OnNeck(int slideEnd, int capo) => slideEnd >= Math.Max(1, capo) && slideEnd <= ChordSlideScope.MaxFret;

    private static int Bit(bool on) => on ? 1 : 0;

    private static int? ReadString(IGuitarSound s, TechniqueField field) => field switch
    {
        TechniqueField.Accent => Bit(s.Accent),
        TechniqueField.Vibrato => Bit(s.Vibrato),
        TechniqueField.LinkNext => Bit(s.LinkNext),
        TechniqueField.Tremolo => Bit(s.Tremolo),
        TechniqueField.UnpitchedSlide => Bit(s.UnpitchedSlide),
        TechniqueField.SlideTo => s.SlideTo,
        TechniqueField.Mute => (int)s.Mute,
        TechniqueField.Hopo => (int)s.Hopo,
        TechniqueField.Harmonic => (int)s.Harmonic,
        TechniqueField.BassPicking => (int)s.BassPicking,
        TechniqueField.Ignore when s is GuitarNote note => Bit(note.Ignore),
        TechniqueField.Finger when s is GuitarNote note => note.LeftHandFinger,
        TechniqueField.PickDirection when s is GuitarNote note => (int)note.PickDirection,
        TechniqueField.VibratoStrength => s.VibratoStrength,
        _ => null
    };

    private static int? ReadChord(Chord c, TechniqueField field) => field switch
    {
        TechniqueField.Accent => Bit(c.Accent),
        TechniqueField.PassOtherNotes => Bit(c.PassOtherNotes),
        TechniqueField.Ignore => Bit(c.Ignore),
        TechniqueField.SplitIntoNotes => Bit(c.SplitIntoNotes),
        TechniqueField.ForceNoNotes => Bit(c.ForceNoNotes),
        TechniqueField.Strum => (int)c.Strum,
        _ => null
    };

    private static void WriteString(IGuitarSound s, TechniqueField field, int? value)
    {
        var on = value == 1;
        var number = value ?? 0;
        switch (field)
        {
            case TechniqueField.Accent:
                s.Accent = on;
                break;
            case TechniqueField.Vibrato:
                InspectorViewModel.SetVibrato(s, on);
                break;
            case TechniqueField.LinkNext:
                s.LinkNext = on;
                break;
            case TechniqueField.Tremolo:
                s.Tremolo = on;
                break;
            case TechniqueField.UnpitchedSlide:
                s.UnpitchedSlide = on;
                break;
            case TechniqueField.SlideTo:
                s.SlideTo = value;
                break;
            case TechniqueField.Mute:
                s.Mute = (MuteType)number;
                break;
            case TechniqueField.Hopo:
                s.Hopo = (HopoType)number;
                break;
            case TechniqueField.Harmonic:
                s.Harmonic = (HarmonicType)number;
                break;
            case TechniqueField.BassPicking:
                s.BassPicking = (BassPickingType)number;
                break;
            // Authored wins: an Ignore written here clears the enforcer's derived mark in the same write.
            case TechniqueField.Ignore when s is GuitarNote note:
                note.Ignore = on;
                note.IgnoreDerived = false;
                break;
            case TechniqueField.Finger when s is GuitarNote note:
                note.LeftHandFinger = value ?? -1;
                break;
            case TechniqueField.PickDirection when s is GuitarNote note:
                note.PickDirection = (PickDirectionType)number;
                break;
            // Never a depth under no vibrato: the very state SetVibrato exists to prevent.
            case TechniqueField.VibratoStrength when s.Vibrato:
                s.VibratoStrength = number;
                break;
        }
    }

    private static void WriteChord(Chord c, TechniqueField field, int? value)
    {
        var on = value == 1;
        switch (field)
        {
            case TechniqueField.Accent:
                c.Accent = on;
                break;
            case TechniqueField.PassOtherNotes:
                c.PassOtherNotes = on;
                break;
            case TechniqueField.Ignore:
                c.Ignore = on;
                break;
            // Under Split notes the inspector disables No notes and keeps its STORED flag: the export reads Split first.
            case TechniqueField.SplitIntoNotes:
                c.SplitIntoNotes = on;
                break;
            case TechniqueField.ForceNoNotes:
                c.ForceNoNotes = on && !c.SplitIntoNotes;
                break;
            case TechniqueField.Strum:
                c.Strum = (PickDirectionType)(value ?? 0);
                break;
        }
    }

    private static void ResetString(IGuitarSound s)
    {
        s.Accent = false;
        InspectorViewModel.SetVibrato(s, false);
        s.LinkNext = false;
        s.Tremolo = false;
        s.UnpitchedSlide = false;
        s.SlideTo = null;
        s.Mute = MuteType.None;
        s.Hopo = HopoType.None;
        s.Harmonic = HarmonicType.None;
        s.BassPicking = BassPickingType.None;
    }

    private static void ResetNote(GuitarNote note)
    {
        ResetString(note);
        note.PassOtherNotes = false;
        note.Ignore = false;
    }

    private static void ResetChord(Chord chord)
    {
        chord.Accent = false;
        chord.PassOtherNotes = false;
        chord.Ignore = false;
        chord.SplitIntoNotes = false;
        chord.ForceNoNotes = false;
        chord.Strum = PickDirectionType.None;
        foreach (var strand in chord.ChordNotes)
        {
            ResetString(strand);
            strand.Ignore = false;
        }
    }
}
