namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>Which selected sounds the Selection page's Values card writes.</summary>
public enum BulkSource
{
    NotesAndChords,
    Notes,
    Chords
}

/// <summary>The on/off values the Selection page stages for every target at once.</summary>
public enum BulkFlag
{
    LinkNext,
    Ignore,
    Accent,
    Vibrato,
    Tremolo,
    PassOtherNotes
}

/// <summary>What the Selection page's Move card steps.</summary>
public enum BulkMove
{
    Fret,
    String,
    Sustain
}

/// <summary>
///     The Selection page's staged values; null leaves a field alone ("Keep"). <see cref="Slide" /> is a
///     number of frets each target slides from its own fret, 0 for "no slide".
/// </summary>
public sealed record BulkDraft
{
    public static readonly BulkDraft None = new();

    public FractionalPosition? Sustain { get; init; }

    /// <summary>A chord's strum; a note passes it by.</summary>
    public PickDirectionType? Strum { get; init; }

    /// <summary>A note's mute; a chord passes it by.</summary>
    public MuteType? Mute { get; init; }

    public HopoType? Legato { get; init; }

    public HarmonicType? Harmonic { get; init; }

    public int? Slide { get; init; }

    public IReadOnlyDictionary<BulkFlag, bool> Flags { get; init; } = new Dictionary<BulkFlag, bool>();

    public bool IsEmpty => Sustain is null && Strum is null && Mute is null && Legato is null && Harmonic is null &&
                           Slide is null && Flags.Count == 0;
}

/// <summary>
///     The Selection page (design D's <c>FakeColumnFlowBulk</c>/<c>BulkUnwritten</c> on real types), pure.
///     <list type="bullet">
///         <item>A chord's Link, Vibrato and Tremolo live on its strings, its Accent, Ignore and Pass other on
///         the chord — the same fields the Technique page writes. A chord whose strings disagree reads MIXED
///         (null), never Off.</item>
///         <item>A target counts only when a staged value lands on it: a strum on a note, a note mute on a
///         chord, or a slide that would leave the neck (or change nothing) passes it by, and the review names
///         it ("no staged value lands there"). A target that takes the other values but keeps its slide is
///         named as written only in part, never as skipped.</item>
///         <item>A slide on a chord moves only its FRETTED strings inside the string mask, like
///         <see cref="ChordSlideScope" /> and <see cref="WholeFretMove" />: an open string never slides.</item>
///         <item>Move steps a sound whole or not at all (<see cref="WholeFretMove" /> for frets, its slides
///         carried; one string up or down); a sound it would push off the neck stays and is named. The sustain
///         step is the keyboard's (<see cref="IHighwayEditingService.PreviewSelectedNotesSustain" />).</item>
///     </list>
/// </summary>
internal static class StagedBulk
{
    /// <summary>The targets <paramref name="source" /> picks from the selection.</summary>
    internal static IReadOnlyList<ITimedElement> Targets(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords,
        BulkSource source) =>
    [
        .. source == BulkSource.Chords ? [] : notes.Cast<ITimedElement>(),
        .. source == BulkSource.Notes ? [] : chords.Cast<ITimedElement>()
    ];

    /// <summary>One target's flag: a chord's string-borne flag reads null when its strings disagree.</summary>
    internal static bool? Read(ITimedElement target, BulkFlag flag) => target switch
    {
        GuitarNote note => flag switch
        {
            BulkFlag.LinkNext => note.LinkNext,
            BulkFlag.Ignore => note.Ignore,
            BulkFlag.Accent => note.Accent,
            BulkFlag.Vibrato => note.Vibrato,
            BulkFlag.Tremolo => note.Tremolo,
            _ => note.PassOtherNotes
        },
        Chord chord => flag switch
        {
            BulkFlag.Ignore => chord.Ignore,
            BulkFlag.Accent => chord.Accent,
            BulkFlag.PassOtherNotes => chord.PassOtherNotes,
            BulkFlag.LinkNext => Agreed(chord.ChordNotes.Select(n => (bool?)n.LinkNext)),
            BulkFlag.Vibrato => Agreed(chord.ChordNotes.Select(n => (bool?)n.Vibrato)),
            _ => Agreed(chord.ChordNotes.Select(n => (bool?)n.Tremolo))
        },
        _ => null
    };

    /// <summary>The flag across every target: on, off, or mixed (null).</summary>
    internal static bool? Read(IEnumerable<ITimedElement> targets, BulkFlag flag) => Agreed(targets.Select(t => Read(t, flag)));

    /// <summary>A per-string choice across every target and string (legato, harmonic, mute), or null when mixed.</summary>
    internal static int? ReadChoice(IEnumerable<ITimedElement> targets, Func<IGuitarSound, int> choice) =>
        Agreed(targets.SelectMany(t => t switch
        {
            GuitarNote note => [choice(note)],
            Chord chord => chord.ChordNotes.Select(n => (int?)choice(n)),
            _ => []
        }));

    /// <summary>The one value every item agrees on; null when they differ (or one is itself mixed).</summary>
    internal static T? Agreed<T>(IEnumerable<T?> values) where T : struct
    {
        T? first = null;
        foreach (var value in values)
        {
            if (value is null) return null;
            if (first is null) first = value;
            else if (!EqualityComparer<T>.Default.Equals(first.Value, value.Value)) return null;
        }

        return first;
    }

    /// <summary>Which targets the draft writes, which it passes by, and why.</summary>
    internal static StagedEditPlan Plan(BulkDraft draft, IReadOnlyList<ITimedElement> targets, int capo, StringEditMask mask)
    {
        if (draft.IsEmpty) return StagedEditPlan.Empty;
        var writes = new List<ITimedElement>();
        var notes = new List<StagedSkip>();
        foreach (var target in targets)
        {
            var slideSkipped = draft.Slide is { } slide && !SlideFits(target, slide, capo, mask);
            if (slideSkipped) notes.Add(new StagedSkip(target, StagedSkipReason.SlideOffNeck));
            if (Lands(draft, target, capo, mask)) writes.Add(target);
            else if (!slideSkipped) notes.Add(new StagedSkip(target, StagedSkipReason.NothingLands));
        }

        return StagedEditPlan.From(writes, notes, []);
    }

    /// <summary>True when any staged value lands on <paramref name="target" /> (D's <c>BulkWrites</c>).</summary>
    internal static bool Lands(BulkDraft draft, ITimedElement target, int capo, StringEditMask mask)
    {
        var chord = target is Chord;
        return draft.Sustain is not null || draft.Legato is not null || draft.Harmonic is not null || draft.Flags.Count > 0 ||
               chord && draft.Strum is not null || !chord && draft.Mute is not null ||
               draft.Slide is { } slide && SlideFits(target, slide, capo, mask) && SlideChanges(target, slide, mask);
    }

    /// <summary>What Apply does to one note.</summary>
    internal static Action<GuitarNote> NoteWrite(BulkDraft draft, int capo) => note =>
    {
        if (draft.Sustain is { } sustain) note.Sustain = sustain;
        if (draft.Mute is { } mute) note.Mute = mute;
        if (draft.Flags.TryGetValue(BulkFlag.Ignore, out var ignore)) note.Ignore = ignore;
        if (draft.Flags.TryGetValue(BulkFlag.Accent, out var accent)) note.Accent = accent;
        if (draft.Flags.TryGetValue(BulkFlag.PassOtherNotes, out var pass)) note.PassOtherNotes = pass;
        WriteString(note, draft);
        if (draft.Slide is { } slide && SlideFits(note, slide, capo, StringEditMask.Empty)) note.SlideTo = SlideEnd(note, slide);
    };

    /// <summary>What Apply does to one chord: chord-borne flags on the chord, string-borne ones on every string.</summary>
    internal static Action<Chord> ChordWrite(BulkDraft draft, int capo, StringEditMask mask) => chord =>
    {
        if (draft.Strum is { } strum) chord.Strum = strum;
        if (draft.Flags.TryGetValue(BulkFlag.Ignore, out var ignore)) chord.Ignore = ignore;
        if (draft.Flags.TryGetValue(BulkFlag.Accent, out var accent)) chord.Accent = accent;
        if (draft.Flags.TryGetValue(BulkFlag.PassOtherNotes, out var pass)) chord.PassOtherNotes = pass;
        foreach (var strand in chord.ChordNotes)
        {
            if (draft.Sustain is { } sustain) strand.Sustain = sustain;
            WriteString(strand, draft);
        }

        if (draft.Slide is not { } slide || !SlideFits(chord, slide, capo, mask)) return;
        foreach (var strand in SlideStrings(chord, mask)) strand.SlideTo = SlideEnd(strand, slide);
    };

    private static void WriteString(IGuitarSound s, BulkDraft draft)
    {
        if (draft.Legato is { } legato) s.Hopo = legato;
        if (draft.Harmonic is { } harmonic) s.Harmonic = harmonic;
        if (draft.Flags.TryGetValue(BulkFlag.LinkNext, out var link)) s.LinkNext = link;
        if (draft.Flags.TryGetValue(BulkFlag.Vibrato, out var vibrato)) InspectorViewModel.SetVibrato(s, vibrato);
        if (draft.Flags.TryGetValue(BulkFlag.Tremolo, out var tremolo)) s.Tremolo = tremolo;
    }

    private static int? SlideEnd(IGuitarSound s, int slide) => slide == 0 ? null : s.Fret + slide;

    /// <summary>A slide fits when every sliding string's end stays on the neck ("no slide" always fits).</summary>
    internal static bool SlideFits(ITimedElement target, int slide, int capo, StringEditMask mask) => slide == 0 ||
        SlideStrings(target, mask).All(s => StagedTechnique.OnNeck(s.Fret + slide, capo));

    private static bool SlideChanges(ITimedElement target, int slide, StringEditMask mask) =>
        SlideStrings(target, mask).Any(s => s.SlideTo != SlideEnd(s, slide));

    /// <summary>The strings a slide moves: a note itself; a chord's fretted strings inside the mask.</summary>
    internal static IEnumerable<IGuitarSound> SlideStrings(ITimedElement target, StringEditMask mask) => target switch
    {
        GuitarNote note => [note],
        Chord chord => chord.ChordNotes.Where(n => n.Fret > 0 && mask.Includes(n.StringIndex)),
        _ => []
    };

    private static IEnumerable<IGuitarSound> Strings(ITimedElement target) => target switch
    {
        GuitarNote note => [note],
        Chord chord => chord.ChordNotes,
        _ => []
    };

    /// <summary>Which targets a Fret or String Move step reaches, and which it leaves where they are. A
    /// String step is one string up or down (the keyboard's verbs); any other step reaches nothing.</summary>
    internal static StagedEditPlan PlanMove(BulkMove move, int delta, IReadOnlyList<ITimedElement> targets, int capo,
        int stringCount)
    {
        if (delta == 0 || move == BulkMove.String && Math.Abs(delta) != 1 || move == BulkMove.Sustain) return StagedEditPlan.Empty;
        var writes = new List<ITimedElement>();
        var skips = new List<StagedSkip>();
        foreach (var target in targets)
        {
            var fits = move == BulkMove.Fret
                ? target is GuitarNote note ? WholeFretMove.Fits(note, delta, capo) : target is Chord chord && WholeFretMove.Fits(chord, delta, capo)
                : Strings(target).All(s => s.StringIndex + delta >= 0 && s.StringIndex + delta < stringCount);
            if (fits) writes.Add(target);
            else skips.Add(new StagedSkip(target, StagedSkipReason.LeavesNeck));
        }

        return StagedEditPlan.From(writes, skips, []);
    }

    /// <summary>The sustain Move step's plan from the keyboard's own preview: a sound whose sustain changes is
    /// written (named in part when the step was cut short); one that does not change is named as skipped.</summary>
    internal static StagedEditPlan PlanSustain(IReadOnlyList<SustainStepOutcome> outcomes) => StagedEditPlan.From(
        [.. outcomes.Where(o => o.Changes).Select(o => o.Sound)],
        outcomes.Where(o => o.Limit != SustainStepLimit.None).Select(o => new StagedSkip(o.Sound, o.Limit switch
        {
            SustainStepLimit.Zero => StagedSkipReason.SustainFloor,
            SustainStepLimit.NextSound => StagedSkipReason.SustainNextSound,
            SustainStepLimit.AudioEnd => StagedSkipReason.SustainAudioEnd,
            _ => StagedSkipReason.SustainLinked
        })),
        []);
}
